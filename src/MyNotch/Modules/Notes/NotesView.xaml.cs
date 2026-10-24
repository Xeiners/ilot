using System.Collections.Specialized;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using MyNotch.Composition;
using MyNotch.Controls;
using MyNotch.Notch;
using MyNotch.Localization;
using MyNotch.Windowing;

namespace MyNotch.Modules.Notes;

/// <summary>
/// Notes tab: the notes as a row of cards; a click opens one in a sheet that drops from the notch as a bubble (a
/// <see cref="NoteSheet"/>, with a toolbar the way Word has it), which « Détacher » turns into a window of its own (see
/// <see cref="DetachedWindow"/>). The notes are read the first time the tab shows, and nothing runs while it is away.
/// Typing takes the keyboard from the app in use (the notch stays open meanwhile); Escape, or a click elsewhere, gives it
/// back. Leaving the tab, or the notch closing, folds the sheet away (the note is saved).
/// </summary>
public sealed partial class NotesView : UserControl
{
    // The sheet: this wide at first; never smaller than its toolbar.
    private const double DefaultSheetWidth = 900;
    private const double DefaultWindowHeight = 640;
    private static readonly Windows.Foundation.Size SmallestSheet = new(720, 320);

    private readonly NotchContext _context;
    private readonly ImplicitAnimationCollection _reflow;
    private readonly CompositionAnimationGroup _pop;
    private readonly Dictionary<Note, NoteCard> _cards = [];
    private readonly RowScroller _row;
    private readonly NoteSheet _sheet;
    private IDisposable? _keyboard;
    private NotchBubble? _bubble;
    private Control? _typing;
    private string[] _words = [];
    private DetachedWindow? _window; // The note detached, if one is: one at a time.
    private NoteSheet? _detached;

    public NotesView(NotesViewModel viewModel, NotchContext context)
    {
        ViewModel = viewModel;
        _context = context;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.

        Scroller.Margin = new Thickness(0, context.ContentTop + 2, 0, 0);
        RowHint.Margin = new Thickness(136, context.ContentTop + 44, 0, 0);

        // Opens with the notch, after the tab bar; slides and fades with the tab selection.
        var compositor = context.Motion.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(LayoutRoot, true);
        var root = ElementCompositionPreview.GetElementVisual(LayoutRoot);
        context.Bind($"Clamp((n.Expansion - 0.45) * 2.4, 0, 1) * {NotchContext.TabOpacity}").Start(root, "Opacity");
        context.Bind($"Vector3({NotchContext.TabOffset}, (1 - n.Progress) * -8, 0)").Start(root, "Translation");

        // Cards move aside for each other, and pop in.
        _reflow = ImplicitMotion.Reflow(compositor, Motion.Reflow);
        _pop = ImplicitMotion.Show(compositor, Motion.Pop, fromScale: 0.82f);
        ((INotifyCollectionChanged)viewModel.Notes).CollectionChanged += (_, _) => SyncCards();
        _row = new RowScroller(Scroller, LayoutRoot); // Arrows at its ends, and the wheel, for a mouse.

        // The sheet goes into the bubble under the notch when a note is open (see ShowNote).
        _sheet = new NoteSheet(viewModel, detached: false);
        _sheet.NoteChanged += (_, _) => ShowNote();
        _sheet.CloseRequested += (_, _) => CloseSheet();
        _sheet.MoveRequested += (_, _) => Detach();
        _sheet.KeyboardWanted += (_, field) => TakeKeyboard(field);
        _sheet.KeyboardDone += (_, _) => ReturnKeyboard();
        BindSearch();

        context.ExpandedChanged += (_, _) => OnShownChanged();
        context.SelectionChanged += (_, _) => OnShownChanged();
    }

    public NotesViewModel ViewModel { get; }

    /// <summary>
    /// The notes are read when the tab first comes into view. Leaving it, or the notch closing, folds the sheet away
    /// (saved) and lets go of the keyboard.
    /// </summary>
    private async void OnShownChanged()
    {
        if (_context.IsVisible)
        {
            await ViewModel.LoadAsync();
            var now = DateTimeOffset.Now;
            foreach (var card in _cards.Values)
            {
                card.Refresh(now);
            }

            UpdateRowHint();
            return;
        }

        Search.Reset();
        ReturnKeyboard();
        _sheet.Close();
        ViewModel.Flush();
    }

    // ── The row of cards ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One card per note, in the notes' order (latest changed first), after "Nouvelle note".</summary>
    private void SyncCards()
    {
        var notes = ViewModel.Notes;
        foreach (var gone in _cards.Keys.Where(note => !notes.Contains(note)).ToList())
        {
            _cards[gone].Detach();
            Cards.Children.Remove(_cards[gone]);
            _cards.Remove(gone);
        }

        for (int i = 0; i < notes.Count; i++)
        {
            if (!_cards.TryGetValue(notes[i], out var card))
            {
                card = CreateCard(notes[i]);
                _cards[notes[i]] = card;
            }

            int index = i + 1; // After "Nouvelle note".
            if (Cards.Children.IndexOf(card) != index)
            {
                Cards.Children.Remove(card);
                Cards.Children.Insert(index, card);
            }

            Filter(card);
        }

        UpdateRowHint();
    }

    private NoteCard CreateCard(Note note)
    {
        var card = new NoteCard(note) { IsOpen = note == _sheet.Note };
        var visual = ElementCompositionPreview.GetElementVisual(card);
        visual.ImplicitAnimations = _reflow;
        visual.CenterPoint = new Vector3(75, 48, 0);
        ElementCompositionPreview.SetImplicitShowAnimation(card, _pop);
        card.Opened += (_, _) => Open(note);
        return card;
    }

    private void UpdateRowHint()
    {
        bool none = ViewModel.Notes.Count == 0;
        bool hidden = _words.Length > 0 && !_cards.Values.Any(card => card.Visibility == Visibility.Visible);
        RowHint.Text = none ? Loc.T("Vos notes apparaîtront ici.") : hidden ? Loc.F("Aucune note ne contient « {0} ».", Search.Text.Trim()) : "";
        RowHint.Visibility = none || hidden ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Search ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>In the titles and the texts; Enter opens the first note found.</summary>
    private void BindSearch()
    {
        Search.Attach(_context);
        Search.QueryChanged += (_, _) =>
        {
            _words = TextSearch.Words(Search.Text);
            foreach (var card in _cards.Values)
            {
                Filter(card);
            }

            UpdateRowHint();
            _row.ScrollToStart();
        };
        Search.Submitted += (_, _) =>
        {
            if (ViewModel.Notes.FirstOrDefault(note => _cards.TryGetValue(note, out var card) && card.Visibility == Visibility.Visible) is { } first)
            {
                Search.ReturnKeyboard();
                Open(first);
            }
        };
    }

    private void Filter(NoteCard card)
    {
        bool match = _words.Length == 0 || TextSearch.Matches($"{card.Note.Title} {card.Note.Plain}", _words);
        card.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── The sheet ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A note in the sheet; already in its own window, that window comes to the front instead.</summary>
    private void Open(Note note)
    {
        if (_window is { } window && _detached?.Note == note)
        {
            window.Activate();
            return;
        }

        _sheet.Open(note);
    }

    private void OnNewClick(object sender, RoutedEventArgs e) => _sheet.New();

    private void CloseSheet()
    {
        ReturnKeyboard();
        _sheet.Close();
    }

    /// <summary>The note open (in the bubble), or the bubble folded away when there is none.</summary>
    private void ShowNote()
    {
        foreach (var card in _cards.Values)
        {
            card.IsOpen = card.Note == _sheet.Note;
        }

        if (_sheet.Note is not { } note)
        {
            _bubble?.Dispose();
            _bubble = null;
            return;
        }

        if (_bubble is null)
        {
            ShowSheet();
        }

        if (_cards.TryGetValue(note, out var open))
        {
            open.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        }
    }

    /// <summary>
    /// The sheet drops from the notch as a bubble (see <see cref="NotchContext.ShowBubble"/>), at the size it was last
    /// given (by pulling its corners), else as tall as the screen allows. Its corners resize it, within the screen.
    /// </summary>
    private void ShowSheet()
    {
        var room = SheetRoom();
        var size = ViewModel.SheetSize ?? new Windows.Foundation.Size(Math.Min(DefaultSheetWidth, room.Width), room.Height);
        var bubble = _context.ShowBubble(_sheet, size, SmallestSheet, room);
        bubble.Resized += (_, resized) => ViewModel.SheetSize = resized;
        _bubble = bubble;
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

        double height = bottom - layout.Expanded.Height - layout.BubbleGap;
        return new(Math.Max(width, SmallestSheet.Width), Math.Max(height, SmallestSheet.Height));
    }

    // ── Detached ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// « Détacher »: the note goes on in a window of its own, the caret where it was, where the bubble was the first time,
    /// then where it was put (the one before, if any, closes). The notch folds away, the keyboard left to the window.
    /// </summary>
    private void Detach()
    {
        if (_sheet.Export() is not { } state)
        {
            return;
        }

        var bounds = _bubble is null ? null : DetachedWindow.ScreenBounds(_sheet);
        if (_keyboard is { } keyboard)
        {
            _keyboard = null;
            _typing = null;
            NotchContext.HandOffKeyboard(keyboard);
        }

        _sheet.Close(leave: false);
        _window?.Close();
        var sheet = new NoteSheet(ViewModel, detached: true);
        var window = new DetachedWindow(sheet, ViewModel.Place, bounds, SmallestSheet, new(DefaultSheetWidth, DefaultWindowHeight))
        {
            Title = WindowTitle(state.Note),
        };
        bool closed = false;
        sheet.Import(state);
        sheet.TitleChanged += (_, _) => window.Title = WindowTitle(sheet.Note);
        sheet.KeyboardDone += (_, _) => sheet.StopTyping();
        sheet.CloseRequested += (_, _) => sheet.Close();
        sheet.MoveRequested += (_, _) =>
        {
            if (sheet.Export() is { } back)
            {
                sheet.Close(leave: false); // The window closes with it (below).
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Attach(back)); // Once it has closed.
            }
        };
        sheet.NoteChanged += (_, _) =>
        {
            if (sheet.Note is null && !closed)
            {
                window.Close(); // Closed, deleted, or back in the notch.
            }
        };
        window.Closed += (_, _) =>
        {
            closed = true;
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

    /// <summary>« Rattacher » in the note's window: the notch opens on it, the caret where it was.</summary>
    private void Attach(NoteState state)
    {
        _context.Show();
        _sheet.Import(state);
    }

    /// <summary>In the taskbar: the note's title (or its first line).</summary>
    private static string WindowTitle(Note? note) => Loc.F("Notes · {0}", note?.DisplayTitle ?? Loc.T("Sans titre"));

    // ── Typing ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A field was clicked: the notch takes the keyboard from the app in use, and stays open.</summary>
    private void TakeKeyboard(Control field)
    {
        _typing = field;
        if (_keyboard is not null)
        {
            return;
        }

        _keyboard = _context.TakeKeyboard(lost: () =>
        {
            // The user clicked another app: it has the keyboard now, the note just lets go.
            _keyboard = null;
            StopTyping();
        });

        // Once the window has the keyboard, the caret must be back in the field.
        DispatcherQueue.TryEnqueue(() => _typing?.Focus(FocusState.Programmatic));
    }

    /// <summary>Done typing: the app that had the keyboard gets it back, and the notch may close again.</summary>
    private void ReturnKeyboard()
    {
        var keyboard = _keyboard;
        _keyboard = null;
        keyboard?.Dispose();
        StopTyping();
    }

    private void StopTyping()
    {
        _typing = null;
        _sheet.StopTyping();
    }
}
