using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Ilot.Composition;
using Ilot.Controls;
using Ilot.Localization;
using Ilot.Notch;
using Ilot.Settings;
using Ilot.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.UI;
using static Ilot.Modules.Study.StudyPaint;

namespace Ilot.Modules.Study;

/// <summary>
/// The Révisions tab: the sections (subjects: « Maths », « Physique »…, made by the user, each its colour; « Global »
/// shows every course) as tabs above the courses; the chosen section's courses as a row of cards (a course imported, or
/// dropped on the notch while it shows this tab, goes in it), and one opened in a sheet under the notch (a <see cref="CourseSheet"/>: revising, its summary sheet, its cards,
/// new cards on demand), which « Détacher » turns into a window of its own (see <see cref="DetachedWindow"/>). « Réviser »
/// at the start of the row goes through everything due.
/// </summary>
public sealed partial class StudyView : UserControl, INotchDropTarget
{
    private const double DefaultSheetWidth = 880;
    private const double DefaultSheetHeight = 660;
    private static readonly Windows.Foundation.Size SmallestSheet = new(700, 420);
    private static readonly Windows.Foundation.Size SmallestWindow = new(700, 440); // Its four modes and « Nouvelles cartes » side by side.
    private static readonly TimeSpan MessageTime = TimeSpan.FromSeconds(6);
    private const double SectionRow = 34; // The sections, above the courses: the open notch is this much taller.

    private readonly NotchContext _context;
    private readonly AppSettings _settings;
    private readonly ImplicitAnimationCollection _reflow;
    private readonly CompositionAnimationGroup _pop;
    private readonly Dictionary<StudyDeck, DeckTile> _tiles = [];
    private readonly DispatcherQueueTimer _messageTimer;
    private readonly RowScroller _row;
    private readonly CourseSheet _sheet;
    private NotchBubble? _bubble;
    private Windows.Foundation.Size? _sheetSize;
    private IDisposable? _keyboard;
    private readonly WindowPlace _place;
    private DetachedWindow? _window; // The sheet detached, if it is: one at a time.
    private CourseSheet? _detached;
    private readonly Brush _chosenBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private readonly Brush _clearBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private long _section; // The section shown; 0: « Global ».
    private NotchBubble? _editing; // A section being made or changed, in a bubble under the notch (see SectionEditor).
    private bool _making; // That one is a new section (« + » again closes it).
    private IDisposable? _editKeyboard;

    public StudyView(StudyViewModel viewModel, NotchContext context, AppSettings settings)
    {
        ViewModel = viewModel;
        _context = context;
        _settings = settings;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.

        SectionsScroller.Margin = new Thickness(0, context.ContentTop + 2, 0, 0);
        Scroller.Margin = new Thickness(0, context.ContentTop + 2 + SectionRow, 0, 0);
        RowHint.Margin = new Thickness(144, context.ContentTop + 30 + SectionRow, 0, 0);
        _section = settings.StudySection;
        ((INotifyCollectionChanged)viewModel.Sections).CollectionChanged += (_, _) => BuildSections();
        BuildSections();

        // Opens with the notch, after the tab bar; slides and fades with the tab selection.
        var compositor = context.Motion.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(LayoutRoot, true);
        var root = ElementCompositionPreview.GetElementVisual(LayoutRoot);
        context.Bind($"Clamp((n.Expansion - 0.45) * 2.4, 0, 1) * {NotchContext.TabOpacity}").Start(root, "Opacity");
        context.Bind($"Vector3({NotchContext.TabOffset}, (1 - n.Progress) * -8, 0)").Start(root, "Translation");
        _reflow = ImplicitMotion.Reflow(compositor, Motion.Reflow);
        _pop = ImplicitMotion.Show(compositor, Motion.Pop, fromScale: 0.82f);
        _row = new RowScroller(Scroller, LayoutRoot);

        // The sheet goes into the bubble under the notch when a course is open (see ShowSheet).
        _sheet = new CourseSheet(viewModel, settings, detached: false);
        _sheet.CloseRequested += (_, _) => CloseSheet();
        _sheet.MoveRequested += (_, _) => Detach();
        _sheet.KeyboardWanted += (_, _) => TakeKeyboard();

        _place = new WindowPlace(
            () => settings.CourseWindowBounds, bounds => settings.CourseWindowBounds = bounds,
            () => settings.CourseWindowOnTop, onTop => settings.CourseWindowOnTop = onTop);
        _messageTimer = CreateTimer(MessageTime, UpdateRowHint);

        ((INotifyCollectionChanged)viewModel.Decks).CollectionChanged += (_, _) => SyncTiles();
        viewModel.Announced += (_, said) => _context.Announce(said.Glyph, said.Message, null, said.Progress);
        viewModel.ActivityChanged += (_, activity) => _context.SetActivity(activity); // A course being prepared, on the closed notch.
        viewModel.Prepared += (_, _) => StudySounds.Play(settings, StudySounds.Ready);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.MascotColor))
            {
                foreach (var tile in _tiles.Values)
                {
                    tile.Tint(TileColor(tile.Deck));
                }

                UpdateReviewAll();
            }
        };
        context.ExpandedChanged += (_, _) => OnShownChanged();
        context.SelectionChanged += (_, _) => OnShownChanged();
        context.AcceptDrops(this);
    }

    public StudyViewModel ViewModel { get; }

    /// <summary>The open notch on this tab: its usual shape, a row taller (the sections).</summary>
    public NotchShape OpenShape
    {
        get
        {
            var usual = _context.Motion.Layout.Expanded;
            return usual with { Height = usual.Height + SectionRow };
        }
    }

    /// <summary>The courses are read when the tab first comes into view; leaving it folds the sheet away.</summary>
    private async void OnShownChanged()
    {
        if (_context.IsVisible)
        {
            await ViewModel.LoadAsync();
            if (_section != 0 && ViewModel.Sections.All(section => section.Id != _section))
            {
                Choose(0); // Its section was deleted.
            }

            var now = DateTimeOffset.Now;
            foreach (var tile in _tiles.Values)
            {
                tile.Refresh(now);
            }

            UpdateRowHint();
            UpdateReviewAll();
            return;
        }

        CloseEditor();
        CloseSheet();
    }

    // ── The sections ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>« Global » first, then the user's sections (each with its dot of colour), then « + »; the chosen one lit.</summary>
    private void BuildSections()
    {
        SectionsRow.Children.Clear();
        SectionsRow.Children.Add(SectionPill(null));
        foreach (var section in ViewModel.Sections)
        {
            section.PropertyChanged -= OnSectionChanged;
            section.PropertyChanged += OnSectionChanged;
            SectionsRow.Children.Add(SectionPill(section));
        }

        SectionsRow.Children.Add(AddPill());
        ShowChosen();
    }

    private void OnSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        BuildSections();
        if (e.PropertyName == nameof(StudySection.Color) && sender is StudySection section)
        {
            foreach (var tile in _tiles.Values.Where(tile => tile.Deck.SectionId == section.Id))
            {
                tile.Tint(TileColor(tile.Deck));
            }

            UpdateReviewAll();
        }
    }

    /// <summary>A section's tab (or « Global »'s): clicked, its courses only; right-clicked (a section), its menu.</summary>
    private MotionButton SectionPill(StudySection? section)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        if (section is null)
        {
            label.Children.Add(new FontIcon { Glyph = "\uE8A9", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            label.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(ColorOf(section)), VerticalAlignment = VerticalAlignment.Center });
        }

        label.Children.Add(new TextBlock { Text = section?.Name ?? Loc.T("Global"), MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis });
        var pill = new MotionButton
        {
            Style = (Style)Application.Current.Resources["StudyChoiceStyle"],
            Content = label,
            Tag = section?.Id ?? 0L,
        };
        pill.Click += (_, _) => Choose(section?.Id ?? 0);
        if (section is not null)
        {
            pill.ContextFlyout = SectionMenu(section);
            ToolTipService.SetToolTip(pill, Loc.T("Clic droit : modifier ou supprimer"));
        }

        return pill;
    }

    /// <summary>« + »: a new section, in a bubble under the notch (again: it goes back in).</summary>
    private MotionButton AddPill()
    {
        var pill = new MotionButton
        {
            Style = (Style)Application.Current.Resources["StudyChoiceStyle"],
            Width = 28,
            Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "\uE710", FontSize = 11 },
        };
        ToolTipService.SetToolTip(pill, Loc.T("Nouvelle section"));
        AutomationProperties.SetName(pill, Loc.T("Nouvelle section"));
        pill.Click += (_, _) =>
        {
            if (_editing is not null && _making)
            {
                CloseEditor();
                return;
            }

            EditSection(null);
        };
        return pill;
    }

    /// <summary>The section chosen lit (in its own colour; « Global » in white), the others plain.</summary>
    private void ShowChosen()
    {
        foreach (var pill in SectionsRow.Children.OfType<MotionButton>())
        {
            if (pill.Tag is not long id)
            {
                continue;
            }

            bool chosen = id == _section;
            var section = ViewModel.Sections.FirstOrDefault(each => each.Id == id);
            pill.Background = !chosen ? _clearBrush
                : section is null ? _chosenBrush
                : new SolidColorBrush(Alpha(ColorOf(section), 0x3A));
            pill.Foreground = (Brush)Application.Current.Resources[chosen ? "NotchPrimaryTextBrush" : "NotchSecondaryTextBrush"];
        }
    }

    /// <summary>A section chosen (0: « Global »): its courses only, its cards due, kept for the next time.</summary>
    private void Choose(long section)
    {
        _section = section;
        _settings.StudySection = section;
        ShowChosen();
        foreach (var tile in _tiles.Values)
        {
            tile.Visibility = Shows(tile.Deck) ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateRowHint();
        UpdateReviewAll();
        _row.ScrollToStart();
    }

    private bool Shows(StudyDeck deck) => _section == 0 || deck.SectionId == _section;

    private StudySection? Chosen => ViewModel.Sections.FirstOrDefault(section => section.Id == _section);

    /// <summary>A section's menu: change it (its name, its colour: see EditSection), delete it (its courses stay, in « Global »).</summary>
    private MenuFlyout SectionMenu(StudySection section)
    {
        var menu = Menu();
        menu.Items.Add(MenuItem(Loc.T("Modifier…"), "\uE70F", () => EditSection(section)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem(Loc.T("Supprimer la section"), "\uE74D", () => DeleteSection(section)));
        return menu;
    }

    private void DeleteSection(StudySection section)
    {
        ViewModel.Delete(section);
        if (_section == section.Id)
        {
            Choose(0);
        }
    }

    /// <summary>A course's menu: the section it goes in (or none), or a new one for it.</summary>
    private MenuFlyout CourseMenu(StudyDeck deck)
    {
        var menu = Menu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("Ranger dans"), IsEnabled = false });
            var none = new ToggleMenuFlyoutItem { Text = Loc.T("Aucune section"), IsChecked = deck.SectionId == 0 };
            none.Click += (_, _) => ViewModel.Move(deck, 0);
            menu.Items.Add(none);
            foreach (var section in ViewModel.Sections)
            {
                var item = new ToggleMenuFlyoutItem { Text = section.Name, IsChecked = deck.SectionId == section.Id, Icon = Dot(ColorOf(section)) };
                item.Click += (_, _) => ViewModel.Move(deck, section.Id);
                menu.Items.Add(item);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem(Loc.T("Nouvelle section…"), "\uE710", () => EditSection(null, moving: deck)));
        };
        return menu;
    }

    /// <summary>A menu of the notch's: out of its window if need be; the notch stays open while it shows.</summary>
    private MenuFlyout Menu()
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        IDisposable? hold = null;
        menu.Opened += (_, _) => hold ??= _context.HoldOpen();
        menu.Closed += (_, _) =>
        {
            hold?.Dispose();
            hold = null;
        };
        return menu;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => click();
        return item;
    }

    private static FontIcon Dot(Color color) => new() { Glyph = "\uEA3B", Foreground = new SolidColorBrush(color) };

    /// <summary>
    /// A section made (and <paramref name="moving"/> put in it, then shown) or <paramref name="section"/> changed, in a
    /// bubble under the notch: its name, its colour, its tab as it will look (see <see cref="SectionEditor"/>). The sheet,
    /// if open, folds away and leaves it the keyboard; a click elsewhere, or leaving the tab, gives up.
    /// </summary>
    private void EditSection(StudySection? section, StudyDeck? moving = null)
    {
        if (_keyboard is { } keyboard)
        {
            _keyboard = null;
            NotchContext.HandOffKeyboard(keyboard); // Kept for the bubble: not handed back to the app in between.
        }

        CloseSheet();
        CloseEditor(handOff: true);
        var editor = new SectionEditor(section, ViewModel.NextSectionColor) { Width = SectionEditor.Size.Width };
        editor.Saved += (_, saved) =>
        {
            CloseEditor();
            if (section is not null)
            {
                ViewModel.Rename(section, saved.Name);
                ViewModel.Recolor(section, saved.Color);
                return;
            }

            var made = ViewModel.CreateSection(saved.Name, saved.Color);
            if (moving is not null)
            {
                ViewModel.Move(moving, made.Id);
            }

            Choose(made.Id);
        };
        editor.Cancelled += (_, _) => CloseEditor();
        editor.Deleted += (_, _) =>
        {
            CloseEditor();
            if (section is not null)
            {
                DeleteSection(section);
            }
        };

        _editing = _context.ShowBubble(editor, SectionEditor.Size);
        _making = section is null;
        _editKeyboard = _context.TakeKeyboard(lost: () =>
        {
            _editKeyboard = null;
            CloseEditor();
        });
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            editor.NameBox.Focus(FocusState.Programmatic);
            editor.NameBox.SelectAll();
        });
    }

    /// <summary>The editor back into the notch, the keyboard handed back (or, <paramref name="handOff"/>, kept for what comes next).</summary>
    private void CloseEditor(bool handOff = false)
    {
        var keyboard = _editKeyboard;
        _editKeyboard = null;
        if (keyboard is not null)
        {
            if (handOff)
            {
                NotchContext.HandOffKeyboard(keyboard);
            }
            else
            {
                keyboard.Dispose();
            }
        }

        _editing?.Dispose();
        _editing = null;
    }

    /// <summary>A course's colour: its section's, or the mascot's when it is in none.</summary>
    private Color TileColor(StudyDeck deck) =>
        ViewModel.Sections.FirstOrDefault(section => section.Id == deck.SectionId) is { } section ? ColorOf(section) : Accent();

    private static Color ColorOf(StudySection section) => SectionEditor.Parse(section.Color);

    // ── The row of courses ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>One tile per course, the latest first, after « Importer un cours ».</summary>
    private void SyncTiles()
    {
        var decks = ViewModel.Decks;
        if (_detached is { } detached && ((detached.Deck is { } shown && !decks.Contains(shown)) || detached.Revised.Any(each => !decks.Contains(each))))
        {
            _window?.Close(); // Its course was deleted (in the notch).
        }

        foreach (var gone in _tiles.Keys.Where(deck => !decks.Contains(deck)).ToList())
        {
            _tiles[gone].Detach();
            Tiles.Children.Remove(_tiles[gone]);
            _tiles.Remove(gone);
            gone.PropertyChanged -= OnAnyDeckChanged;
        }

        for (int i = 0; i < decks.Count; i++)
        {
            if (!_tiles.TryGetValue(decks[i], out var tile))
            {
                tile = CreateTile(decks[i]);
                _tiles[decks[i]] = tile;
                decks[i].PropertyChanged += OnAnyDeckChanged;
            }

            int index = i + 1; // After « Importer un cours ».
            if (Tiles.Children.IndexOf(tile) != index)
            {
                Tiles.Children.Remove(tile);
                Tiles.Children.Insert(index, tile);
            }
        }

        UpdateRowHint();
        UpdateReviewAll();
    }

    private DeckTile CreateTile(StudyDeck deck)
    {
        var tile = new DeckTile(deck, TileColor(deck))
        {
            IsOpen = _bubble is not null && deck == _sheet.Deck,
            Visibility = Shows(deck) ? Visibility.Visible : Visibility.Collapsed,
            ContextFlyout = CourseMenu(deck),
        };
        var visual = ElementCompositionPreview.GetElementVisual(tile);
        visual.ImplicitAnimations = _reflow;
        visual.CenterPoint = new Vector3(92, 48, 0);
        ElementCompositionPreview.SetImplicitShowAnimation(tile, _pop);
        tile.Opened += (_, _) => Open(deck, deck.DueCount(DateTimeOffset.Now) > 0 ? SheetMode.Review : SheetMode.Summary);
        return tile;
    }

    private void OnAnyDeckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StudyDeck.SectionId) && sender is StudyDeck moved && _tiles.TryGetValue(moved, out var tile))
        {
            tile.Tint(TileColor(moved)); // Put in another section: its colour, and in or out of the one shown.
            tile.Visibility = Shows(moved) ? Visibility.Visible : Visibility.Collapsed;
            UpdateRowHint();
        }

        UpdateReviewAll();
        if (e.PropertyName == nameof(StudyDeck.Title) && _window is { } window && sender == _detached?.Deck)
        {
            window.Title = WindowTitle(_detached?.Deck);
        }
    }

    /// <summary>The hint beside « Importer un cours » while there is no course (or a message, for a moment).</summary>
    private void UpdateRowHint()
    {
        _messageTimer.Stop();
        bool none = ViewModel.Decks.Count == 0;
        RowHint.Text = none || Chosen is not { } section
            ? Loc.T("Glissez un cours ici (PDF, Word, PowerPoint, photo) : Notch en fait une fiche de révision et des flashcards, révisées au bon moment.")
            : Loc.F("Aucun cours dans « {0} » : glissez-en un ici, ou rangez-en un d'un clic droit dans « Global ».", section.Name);
        RowHint.Visibility = none || !ViewModel.In(_section).Any() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMessage(string message)
    {
        RowHint.Text = message;
        RowHint.Visibility = Visibility.Visible;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    /// <summary>
    /// Everything due in the section shown (all the courses, « Global »): the row's first tile, lit in its colour
    /// (« 23 cartes à revoir, Réviser »). Each section has its own, always there while it has courses: nothing due, it
    /// says so (« À jour », dimmed, not clickable).
    /// </summary>
    private void UpdateReviewAll()
    {
        var now = DateTimeOffset.Now;
        var decks = ViewModel.In(_section).ToList();
        int due = decks.Sum(deck => deck.DueCount(now));
        ReviewAllTile.Visibility = decks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (decks.Count == 0)
        {
            return;
        }

        bool done = due == 0;
        var accent = Chosen is { } section ? ColorOf(section) : Accent();
        ReviewAllCount.Text = due.ToString(Loc.Culture);
        ReviewAllCount.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        ReviewAllDone.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        ReviewAllLabel.Text = done ? Loc.T("À jour") : Loc.T(due == 1 ? "carte à revoir" : "cartes à revoir");
        ReviewAllAction.Text = done ? Loc.T("Rien à revoir") : Loc.T("Réviser");
        ReviewAllArrow.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        ReviewAllTile.IsHitTestVisible = !done;
        ReviewAllTile.Background = done
            ? Linear(new Windows.Foundation.Point(0, 0), new Windows.Foundation.Point(1, 1), (0, Mix(CardBase, accent, 0.32)), (1, Mix(CardBase, accent, 0.16)))
            : Linear(new Windows.Foundation.Point(0, 0), new Windows.Foundation.Point(1, 1), (0, Mix(CardBase, accent, 0.75)), (1, Mix(CardBase, accent, 0.4)));
    }

    /// <summary>« Réviser »: everything due in the section shown (or in all the courses, « Global »).</summary>
    private void OnReviewAllClick(object sender, RoutedEventArgs e)
    {
        ShowSheet(null);
        _sheet.Open(null, SheetMode.Review, scope: _section == 0 ? null : ViewModel.In(_section).ToList(), scopeName: Chosen?.Name);
    }

    /// <summary>The revisions' colour (see StudyPaint.Accent).</summary>
    private Color Accent() => StudyPaint.Accent(_settings);

    // ── Taking a course ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>« Importer un cours »: a document chosen in Windows' picker (the notch stays open meanwhile).</summary>
    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot?.ContentIslandEnvironment is not { } island)
        {
            return;
        }

        using var hold = _context.HoldOpen();
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(island.AppWindowId)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            foreach (string extension in StudyDocument.Extensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            if (await picker.PickSingleFileAsync() is { Path: { Length: > 0 } path })
            {
                await ImportAsync(path);
            }
        }
        catch (COMException ex)
        {
            ShowMessage(Loc.F("Le sélecteur de fichiers n'a pas pu s'ouvrir : {0}", ex.Message));
        }
    }

    private async Task ImportAsync(string path)
    {
        if (await ViewModel.ImportAsync(path, _section) is { } why)
        {
            ShowMessage(why);
            return;
        }

        // Its work follows in the sheet, if the notch is open on it.
        if (_context.IsVisible && ViewModel.Decks.FirstOrDefault() is { } deck)
        {
            Open(deck, SheetMode.Summary);
        }
    }

    public bool Accepts(DataPackageView data) => data.Contains(StandardDataFormats.StorageItems);

    public string DropCaption => Loc.T("Réviser ce cours");

    public void OnDragHover(bool over)
    {
        ImportFrame.Stroke = new SolidColorBrush(over ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        ImportFrame.StrokeThickness = over ? 2 : 1.5;
    }

    public void OnDragMoved(Windows.Foundation.Point position)
    {
    }

    public async Task DropAsync(DataPackageView data)
    {
        foreach (var file in (await data.GetStorageItemsAsync()).OfType<IStorageFile>())
        {
            await ImportAsync(file.Path);
        }
    }

    // ── The sheet ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A course (or, <c>null</c>, everything due) in the sheet under the notch, on <paramref name="mode"/>.</summary>
    private void Open(StudyDeck? deck, SheetMode mode)
    {
        ShowSheet(deck);
        _sheet.Open(deck, mode);
    }

    /// <summary>
    /// The sheet drops from the notch as a bubble (see <see cref="NotchContext.ShowBubble"/>), at the size it was last
    /// given (by pulling its corners; its corners resize it, within the screen), and takes the keyboard: open until a
    /// click elsewhere.
    /// </summary>
    private void ShowSheet(StudyDeck? deck)
    {
        foreach (var (each, tile) in _tiles)
        {
            tile.IsOpen = each == deck;
        }

        CloseEditor(handOff: true);
        if (_bubble is null)
        {
            var room = SheetRoom();
            var size = _sheetSize ?? new Windows.Foundation.Size(Math.Min(DefaultSheetWidth, room.Width), Math.Min(DefaultSheetHeight, room.Height));
            var bubble = _context.ShowBubble(_sheet, size, SmallestSheet, room);
            bubble.Resized += (_, resized) => _sheetSize = resized;
            _bubble = bubble;
        }

        TakeKeyboard();
    }

    /// <summary>All the room under the notch: as wide as the notch's window allows, down to the bottom of the screen.</summary>
    private Windows.Foundation.Size SheetRoom()
    {
        var layout = _context.Motion.Layout;
        var room = layout.ExpandedMax;
        double width = room.Width;
        double bottom = room.Height;
        try
        {
            if (XamlRoot is { } root)
            {
                var screen = DisplayArea.Primary.WorkArea;
                width = Math.Min(width, screen.Width / root.RasterizationScale - 40);
                bottom = Math.Min(bottom, screen.Height / root.RasterizationScale - layout.TopInset - 20);
            }
        }
        catch (COMException)
        {
            // All the room, then.
        }

        double height = bottom - OpenShape.Height - layout.BubbleGap;
        return new(Math.Max(width, SmallestSheet.Width), Math.Max(height, SmallestSheet.Height));
    }

    private void CloseSheet()
    {
        ReturnKeyboard();
        _sheet.Close();
        foreach (var tile in _tiles.Values)
        {
            tile.IsOpen = false;
        }

        _bubble?.Dispose();
        _bubble = null;
    }

    /// <summary>
    /// « Détacher »: the sheet becomes a window of its own, going on exactly as it was (its course, its mode, the card being
    /// answered…), where the bubble was the first time, then where it was put (the one before, if any, closes). The notch
    /// folds away, the keyboard left to the window.
    /// </summary>
    private void Detach()
    {
        var state = _sheet.Export();
        var bounds = _bubble is null ? null : DetachedWindow.ScreenBounds(_sheet);
        if (_keyboard is { } keyboard)
        {
            _keyboard = null;
            NotchContext.HandOffKeyboard(keyboard);
        }

        CloseSheet();
        _window?.Close();
        var sheet = new CourseSheet(ViewModel, _settings, detached: true);
        var window = new DetachedWindow(sheet, _place, bounds, SmallestWindow, new(DefaultSheetWidth, DefaultSheetHeight))
        {
            Title = WindowTitle(state.Deck),
        };
        sheet.Import(state);
        sheet.CloseRequested += (_, _) => window.Close();
        sheet.MoveRequested += (_, _) =>
        {
            var back = sheet.Export();
            window.Close();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Attach(back)); // Once it has closed.
        };
        window.Closed += (_, _) =>
        {
            sheet.Release();
            if (_window == window)
            {
                _window = null;
                _detached = null;
            }
        };
        _window = window;
        _detached = sheet;
        window.Activate();
        _context.Close();
    }

    /// <summary>In the taskbar: the course (or « Révisions du jour »).</summary>
    private static string WindowTitle(StudyDeck? deck) =>
        deck is null ? Loc.T("Révisions du jour") : Loc.F("Révisions · {0}", deck.Title);

    /// <summary>« Rattacher » in the sheet's window: the notch opens on the sheet, going on where the window was.</summary>
    private void Attach(SheetState state)
    {
        if (state.Deck is { } deck && !ViewModel.Decks.Contains(deck))
        {
            return; // Deleted in the meantime.
        }

        _context.Show();
        ShowSheet(state.Deck);
        _sheet.Import(state);
    }

    // ── Keyboard ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The notch takes the keyboard while the sheet is open (Espace, 1 to 4, a card edited), and so stays open as the pointer
    /// leaves; a click elsewhere (another app gets the keyboard) closes the sheet.
    /// </summary>
    private void TakeKeyboard()
    {
        _keyboard ??= _context.TakeKeyboard(lost: () =>
        {
            _keyboard = null;
            CloseSheet();
        });
    }

    private void ReturnKeyboard()
    {
        var keyboard = _keyboard;
        _keyboard = null;
        keyboard?.Dispose();
    }

    private DispatcherQueueTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }
}
