using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyNotch.Controls;
using MyNotch.Localization;
using MyNotch.Mascot;
using MyNotch.Settings;
using MyNotch.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using static MyNotch.Modules.Study.StudyPaint;

namespace MyNotch.Modules.Study;

/// <summary>What a course's sheet shows: revising its cards, its summary sheet, or its cards one by one.</summary>
internal enum SheetMode
{
    Review,
    Summary,
    Cards,
    Maps,
}

/// <summary>
/// Where a sheet is (see <see cref="CourseSheet.Export"/>): the course, the mode, the revision as it goes, the new cards'
/// choices, and the mind map looked at. A sheet detached to its own window, or brought back to the notch, goes on from it.
/// </summary>
internal sealed record SheetState(
    StudyDeck? Deck, SheetMode Mode, ReviewState? Review, CardFocus Focus, int Count, bool MoreOpen, int MapIndex,
    IReadOnlyList<StudyDeck>? Scope = null, string? ScopeName = null);

/// <summary>
/// A course open (or, with no course, everything due): its title and numbers, and its modes. Revising its flashcards
/// (question, then answer, then how well it came: the card comes back when it should), its summary sheet, its cards (each
/// one edited or removed), new cards on demand (the whole course, a chapter, or the weak points), and its mind maps (the
/// whole course, then each important notion, drawn by Notch on demand). While Notch still
/// makes what the mode shows, the mascot reads the course and the steps fill. In the bubble under the notch (see
/// StudyView), or in a window of its own (see DetachedWindow): « Détacher », « Rattacher ».
/// </summary>
public sealed partial class CourseSheet : UserControl, IDetachableSheet
{
    private static readonly TimeSpan ConfirmTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CopiedTime = TimeSpan.FromSeconds(1.6);
    private static readonly int[] CountOptions = [10, 20, 30];

    private readonly AppSettings _settings;
    private readonly ReviewView _review;
    private readonly DispatcherQueueTimer _deleteTimer;
    private readonly DispatcherQueueTimer _copyTimer;
    private readonly DispatcherQueueTimer _clock;
    private readonly Brush _chosenBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private readonly Brush _clearBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private DispatcherQueueTimer? _volumeTimer;
    private DispatcherQueueTimer? _hideVolume;
    private bool _volumeHeld;
    private bool _reading;
    private bool _open;
    private StudyDeck? _deck;
    private SheetMode _mode;
    private CardFocus _focus = CardFocus.All;
    private int _count = 20;
    private bool _confirmingDelete;
    private StudyCard? _editing;
    private int _mapIndex = -1; // The mind map looked at; -1: the whole course.
    private IReadOnlyList<StudyDeck>? _scope; // Everything due: in these courses (a section's); none given, all of them.
    private string? _scopeName; // That section's name.
    private IReadOnlyList<MindMap>? _listed; // The maps the list was made for.
    private readonly List<MotionButton> _mapEntries = [];
    private readonly MindMapCanvas _mapCanvas = new();
    private DispatcherQueueTimer? _inkTimer;
    private StudyDeck? _inked; // The course whose maps were drawn on, its ink not written yet.

    /// <param name="detached">In a window of its own: « Rattacher » rather than « Détacher ».</param>
    internal CourseSheet(StudyViewModel viewModel, AppSettings settings, bool detached)
    {
        ViewModel = viewModel;
        _settings = settings;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.

        // The steps' bars grow smoothly to what is done (their scale, on the compositor).
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        var grow = compositor.CreateVector3KeyFrameAnimation();
        grow.Target = "Scale";
        grow.InsertExpressionKeyFrame(1, "this.FinalValue", compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1)));
        grow.Duration = TimeSpan.FromMilliseconds(450);
        var growing = compositor.CreateImplicitAnimationCollection();
        growing["Scale"] = grow;
        foreach (var fill in new[] { OutlineFill, SummaryFill, CardsFill, MapsFill })
        {
            var visual = ElementCompositionPreview.GetElementVisual(fill);
            visual.Scale = new Vector3(0, 1, 1);
            visual.ImplicitAnimations = growing;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(SheetContent, true);

        // The revision itself: the card, its choices, its answer.
        _review = new ReviewView(viewModel, settings);
        ReviewPanel.Content = _review;
        _review.CardAnswered += (_, _) => ShowHeader();
        _review.Escaped += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        // The mind maps: a canvas, written on; what is drawn kept a moment after the last stroke.
        MapHost.Child = _mapCanvas;
        MapHost.Padding = new Thickness(0);
        _mapCanvas.TypingStarted += (_, _) => KeyboardWanted?.Invoke(this, EventArgs.Empty);
        _mapCanvas.InkChanged += (_, _) =>
        {
            _inked = _deck;
            _inkTimer ??= CreateTimer(TimeSpan.FromMilliseconds(800), WriteInk);
            _inkTimer.Stop();
            _inkTimer.Start();
        };

        _deleteTimer = CreateTimer(ConfirmTime, ResetDelete);

        // While Notch works, the time it has taken so far, by the second: it is at work, not stuck.
        _clock = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => ShowBusyText();
        _copyTimer = CreateTimer(CopiedTime, () =>
        {
            CopySummaryIcon.Glyph = "\uE8C8";
            CopySummaryText.Text = Loc.T("Copier");
        });

        BuildCounts();

        VolumeSlider.Value = settings.StudySoundVolume;
        VolumeSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _volumeHeld = true), handledEventsToo: true);
        VolumeSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => _volumeHeld = false), handledEventsToo: true);
        VolumeSlider.PointerCaptureLost += (_, _) => _volumeHeld = false;
        ShowSoundButton();

        string move = Loc.T(detached ? "Rattacher à la notch" : "Détacher dans une fenêtre");
        MoveIcon.Glyph = detached ? "\uE73F" : "\uE8A7";
        ToolTipService.SetToolTip(MoveButton, move);
        AutomationProperties.SetName(MoveButton, move);
        if (detached)
        {
            BusyHint.Text = Loc.T("Vous pouvez fermer cette fenêtre : Notch vous prévient quand c'est prêt.");
        }

        settings.PropertyChanged += OnSettingsChanged;
    }

    public StudyViewModel ViewModel { get; }

    /// <summary>The course open; <c>null</c>: everything due, or nothing open.</summary>
    public StudyDeck? Deck => _deck;

    /// <summary>A course (or everything due) is open in it.</summary>
    public bool IsOpen => _open;

    /// <summary>The courses being revised (one, or all of them for everything due).</summary>
    internal IReadOnlyList<StudyDeck> Revised => _review.Decks;

    /// <summary>« Fermer », Échap while revising, or the course deleted: the host puts the sheet away.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>« Détacher » in the notch, « Rattacher » in its window.</summary>
    public event EventHandler? MoveRequested;

    /// <summary>A card being edited: the notch must give the keyboard (in a window, it has it).</summary>
    public event EventHandler? KeyboardWanted;

    public void AddWindowTools(UIElement pin, UIElement minimize)
    {
        Actions.Children.Insert(Actions.Children.IndexOf(MoveButton), pin);
        Actions.Children.Insert(Actions.Children.IndexOf(CloseButton), minimize);
    }

    /// <summary>Its two glows reach the corners: rounded as the window's.</summary>
    public void RoundCorners(double radius)
    {
        foreach (var glow in new[] { GlowTop, GlowBottom })
        {
            glow.RadiusX = radius;
            glow.RadiusY = radius;
        }
    }

    /// <summary>
    /// A course (or, <c>null</c>, everything due: in <paramref name="scope"/>, a section's courses called
    /// <paramref name="scopeName"/>, or all of them), on <paramref name="mode"/>: a new revision, or the one given
    /// (<paramref name="review"/>, going on where it was).
    /// </summary>
    internal void Open(StudyDeck? deck, SheetMode mode, ReviewState? review = null, IReadOnlyList<StudyDeck>? scope = null, string? scopeName = null)
    {
        _scope = deck is null ? scope : null;
        _scopeName = deck is null ? scopeName : null;
        if (_deck is { } before)
        {
            before.PropertyChanged -= OnDeckChanged;
        }

        _open = true;
        _deck = deck;
        if (review is not null)
        {
            _review.Import(review);
        }
        else
        {
            _review.Start(new ReviewSession(deck is null ? (_scope ?? ViewModel.Decks).Where(each => !each.Busy).ToList() : [deck]));
        }

        _focus = CardFocus.All;
        _editing = null;
        _mapIndex = -1;
        _listed = null;
        MoreStrip.Visibility = Visibility.Collapsed;
        ResetDelete();
        if (deck is not null)
        {
            deck.PropertyChanged += OnDeckChanged;
            ViewModel.CompleteChoices(deck); // Cards from before the multiple choices get theirs, quietly.
        }

        Tint();

        // Everything due has no summary nor cards of its own: revising only.
        bool course = deck is not null;
        SummaryMode.Visibility = CardsMode.Visibility = MapsMode.Visibility = MoreButton.Visibility = DeleteButton.Visibility =
            course ? Visibility.Visible : Visibility.Collapsed;
        ShowHeader();
        SetMode(mode);
        Rise(SheetContent, 0);
    }

    /// <summary>Where the sheet is, to go on elsewhere (see <see cref="Import"/>).</summary>
    internal SheetState Export() =>
        new(_deck, _mode, _review.Export(), _focus, _count, MoreStrip.Visibility == Visibility.Visible, _mapIndex, _scope, _scopeName);

    /// <summary>The sheet goes on from <paramref name="state"/>: its course, its mode, its revision, the new cards' choices.</summary>
    internal void Import(SheetState state)
    {
        Open(state.Deck, state.Mode, state.Review, state.Scope, state.ScopeName);
        if (state.MapIndex != _mapIndex)
        {
            _mapIndex = state.MapIndex;
            if (_mode == SheetMode.Maps)
            {
                ShowMaps();
            }
        }

        _focus = state.Focus;
        _count = state.Count;
        if (state.MoreOpen && state.Deck is not null)
        {
            MoreStrip.Visibility = Visibility.Visible;
            BuildFocus();
            ShowMoreStatus();
        }

        ShowChoices();
    }

    /// <summary>Put away: its course no longer followed, its clock and its reading mascot stopped.</summary>
    public void Close()
    {
        _mapCanvas.CommitTyping();
        WriteInk();
        ResetDelete();
        if (_deck is { } deck)
        {
            deck.PropertyChanged -= OnDeckChanged;
        }

        _open = false;
        _deck = null;
        _editing = null;
        ShowReader(false);
        _clock.Stop();
    }

    /// <summary>For good (its window closed): it no longer follows the settings either.</summary>
    public void Release()
    {
        Close();
        _settings.PropertyChanged -= OnSettingsChanged;
    }

    /// <summary>The keys to the card (Espace, 1 to 4, ← →), while revising.</summary>
    public void FocusContent()
    {
        if (_open && ReviewPanel.Visibility == Visibility.Visible)
        {
            _review.Focus(FocusState.Programmatic);
        }
        else if (_open && MapsBrowser.Visibility == Visibility.Visible && MapsPanel.Visibility == Visibility.Visible)
        {
            _mapCanvas.FocusKeys(); // Ctrl + Z on the map.
        }
    }

    private void OnMoveClick(object sender, RoutedEventArgs e) => MoveRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.MascotColor) && _open)
        {
            Tint();
            ShowHeader();
            ShowContent();
        }
    }

    // ── Colours, sounds ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sheet's colours, from the mascot's: its two glows, and the card (lit from above, a luminous edge, its chapter
    /// and its choices in that colour); the rest stays neutral.
    /// </summary>
    private void Tint()
    {
        var accent = Accent(_settings);
        _review.Tint(accent);
        TintGlows();
        foreach (var (button, each) in Modes())
        {
            button.Background = each == _mode ? _chosenBrush : _clearBrush;
        }
    }

    private (MotionButton Button, SheetMode Mode)[] Modes() =>
        [(ReviewMode, SheetMode.Review), (SummaryMode, SheetMode.Summary), (CardsMode, SheetMode.Cards), (MapsMode, SheetMode.Maps)];

    /// <summary>
    /// The sheet's two glows, in the mascot's colour: a wider one from the top left corner (behind the title, faint), a
    /// smaller one from the bottom right; both fade out long before the middle, where the text is.
    /// </summary>
    private void TintGlows() => (GlowTop.Fill, GlowBottom.Fill) = Glows(Accent(_settings));

    private void Sound(Action<double> sound) => StudySounds.Play(_settings, sound);

    /// <summary>The sheet's speaker: its glyph says the volume (or that the sounds are off).</summary>
    private void ShowSoundButton()
    {
        bool on = _settings.StudySounds && _settings.StudySoundVolume > 0;
        double volume = _settings.StudySoundVolume;
        SoundIcon.Glyph = !on ? "\uE74F" : volume < 34 ? "\uE993" : volume < 67 ? "\uE994" : "\uE995";
        string name = Loc.T(_settings.StudySounds ? "Couper les sons" : "Remettre les sons");
        AutomationProperties.SetName(SoundButton, name);
        ToolTipService.SetToolTip(SoundButton, name);
    }

    private void OnSoundClick(object sender, RoutedEventArgs e)
    {
        _settings.StudySounds = !_settings.StudySounds;
        ShowSoundButton();
        Sound(StudySounds.Right); // As they will sound.
    }

    /// <summary>The speaker hovered: its volume opens beside it (and stays while the pointer is on either).</summary>
    private void OnSoundEntered(object sender, PointerRoutedEventArgs e)
    {
        _hideVolume?.Stop();
        VolumeSlider.Value = _settings.StudySoundVolume; // As changed elsewhere (the notch, or a window).
        if (VolumePill.Visibility != Visibility.Visible)
        {
            VolumePill.Visibility = Visibility.Visible;
            Slide(VolumePill, 14, 0);
        }
    }

    /// <summary>Left: the volume folds away a moment later (not while its thumb is held).</summary>
    private void OnSoundExited(object sender, PointerRoutedEventArgs e)
    {
        _hideVolume ??= CreateTimer(TimeSpan.FromMilliseconds(700), () =>
        {
            if (!_volumeHeld)
            {
                VolumePill.Visibility = Visibility.Collapsed;
            }
        });
        _hideVolume.Stop();
        _hideVolume.Start();
    }

    /// <summary>The volume moved: kept; once it settles, a sound at that volume (the sounds on again, if they were off).</summary>
    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Math.Abs(e.NewValue - _settings.StudySoundVolume) < 0.5)
        {
            return;
        }

        _settings.StudySoundVolume = e.NewValue;
        _settings.StudySounds = e.NewValue > 0;
        ShowSoundButton();
        _volumeTimer ??= CreateTimer(TimeSpan.FromMilliseconds(260), () => Sound(StudySounds.Right));
        _volumeTimer.Stop();
        _volumeTimer.Start();
    }

    // ── The course ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The course's title, and its numbers: pages, cards, due, known.</summary>
    private void ShowHeader()
    {
        var now = DateTimeOffset.Now;
        if (_deck is not { } deck)
        {
            var decks = _scope ?? ViewModel.Decks;
            SheetTitle.Text = _scopeName is null ? Loc.T("Révisions du jour") : Loc.F("Révisions du jour · {0}", _scopeName);
            int due = decks.Sum(each => each.DueCount(now));
            ShowStats(
                (decks.Count == 1 ? Loc.T("1 cours") : Loc.F("{0} cours", decks.Count), false),
                (Loc.Count(due, "carte à revoir", "cartes à revoir"), due > 0));
            return;
        }

        SheetTitle.Text = deck.Title;
        var stats = new List<(string, bool)>();
        if (deck.Pages > 0)
        {
            stats.Add((Loc.Count(deck.Pages, "page", "pages"), false));
        }

        stats.Add((Loc.Count(deck.Cards.Count, "carte", "cartes"), false));
        int dueNow = deck.DueCount(now);
        stats.Add((Loc.Count(dueNow, "à revoir", "à revoir"), dueNow > 0));
        if (deck.Cards.Count > 0)
        {
            stats.Add((Loc.F("{0} % maîtrisé", Math.Round(deck.Mastery * 100)), false));
        }

        ShowStats([.. stats]);
        CardsModeText.Text = Loc.F("Cartes ({0})", deck.Cards.Count);
    }

    /// <summary>
    /// The summary sheet, drawn as cards (see <see cref="StudySheet"/>), from its top; its parts as chips above it, a click
    /// on one scrolling to it.
    /// </summary>
    private void ShowSummary(string summary)
    {
        var (sheet, parts) = StudySheet.Build(summary);
        SheetHost.Child = sheet;
        SheetParts.Children.Clear();
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            label.Children.Add(new TextBlock { Text = (i + 1).ToString(Loc.Culture), Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) });
            label.Children.Add(new TextBlock { Text = part.Title, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis });
            var chip = new MotionButton { Style = (Style)Application.Current.Resources["StudyChoiceStyle"], Content = label, Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)) };
            ToolTipService.SetToolTip(chip, part.Title);
            chip.Click += (_, _) => part.Card.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0, VerticalOffset = -6 });
            SheetParts.Children.Add(chip);
        }

        SummaryScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>The course's numbers, as chips of glass; <c>lit</c> ones brighter (what is due).</summary>
    private void ShowStats(params (string Text, bool Lit)[] stats)
    {
        SheetStats.Children.Clear();
        foreach (var (text, lit) in stats)
        {
            SheetStats.Children.Add(StudySheet.Chip(text, lit ? Microsoft.UI.Colors.White : null));
        }
    }

    /// <summary>The course changed (Notch done, a step further, cards added…): what shows it follows.</summary>
    private void OnDeckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != _deck)
        {
            return;
        }

        ShowHeader();
        ShowMoreStatus();
        switch (e.PropertyName)
        {
            case nameof(StudyDeck.MindMaps) or nameof(StudyDeck.MindMapsStep) or nameof(StudyDeck.MindMapsWritten) or nameof(StudyDeck.MindMapsNote):
                if (_mode == SheetMode.Maps)
                {
                    ShowMaps();
                }

                break;

            case nameof(StudyDeck.Busy):
            case nameof(StudyDeck.Failed):
            case nameof(StudyDeck.Summary):
                ShowContent();
                break;

            case nameof(StudyDeck.Cards):
                if (BusyPanel.Visibility == Visibility.Visible && !IsWaiting)
                {
                    ShowContent(); // Its first cards: they can be revised while the sheet is still being written.
                }
                else if (_mode == SheetMode.Cards && _editing is null)
                {
                    FillCards();
                }
                else if (_mode == SheetMode.Review)
                {
                    _review.CardsChanged(); // New cards came while nothing was left: the next one.
                }

                break;

            default:
                if (BusyPanel.Visibility == Visibility.Visible)
                {
                    ShowBusy(); // A step further.
                }

                break;
        }
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out SheetMode mode) && mode != _mode)
        {
            SetMode(mode);
            Rise(SheetContent, 0);
        }
    }

    private void SetMode(SheetMode mode)
    {
        _mode = mode;
        foreach (var (button, each) in Modes())
        {
            button.Background = each == mode ? _chosenBrush : _clearBrush;
        }

        ShowContent();
    }

    /// <summary>
    /// The mode shown has nothing yet: Notch is still making it (the cards, or the sheet), or failed to. The cards can be
    /// revised as soon as they are there, the sheet still being written, and the other way round. (The mind maps say
    /// themselves where they are.)
    /// </summary>
    private bool IsWaiting => _deck is { } deck && (deck.Busy || deck.Failed) && _mode != SheetMode.Maps
        && (_mode == SheetMode.Summary ? deck.Summary.Length == 0 : deck.Cards.Count == 0);

    /// <summary>What the mode shows (or Notch at work, while it has nothing yet).</summary>
    private void ShowContent()
    {
        bool waiting = IsWaiting;
        BusyPanel.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        ReviewPanel.Visibility = !waiting && _mode == SheetMode.Review ? Visibility.Visible : Visibility.Collapsed;
        SummaryPanel.Visibility = !waiting && _mode == SheetMode.Summary ? Visibility.Visible : Visibility.Collapsed;
        CardsPanel.Visibility = !waiting && _mode == SheetMode.Cards ? Visibility.Visible : Visibility.Collapsed;
        MapsPanel.Visibility = !waiting && _mode == SheetMode.Maps ? Visibility.Visible : Visibility.Collapsed;
        ShowMoreStatus();
        if (waiting)
        {
            ShowBusy();
            return;
        }

        ShowReader(false);
        _clock.Stop();
        switch (_mode)
        {
            case SheetMode.Review:
                _review.Show();
                _review.Focus(FocusState.Programmatic);
                break;

            case SheetMode.Summary:
                ShowSummary(_deck?.Summary ?? "");
                break;

            case SheetMode.Cards:
                FillCards();
                break;

            case SheetMode.Maps:
                ShowMaps();
                break;
        }
    }

    // ── Notch at work ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Notch at work: the mascot reading the course, and its steps (the outline, the sheet part by part, the cards one
    /// by one), each with its bar. Or, stopped, why, and « Réessayer » (what is done is kept).
    /// </summary>
    private void ShowBusy()
    {
        if (_deck is not { } deck)
        {
            return;
        }

        bool failed = deck.Failed && !deck.Busy;
        BusyTitle.Text = failed ? Loc.T("La préparation s'est arrêtée") : Loc.T("Notch prépare votre cours");
        ShowBusyText();
        if (failed)
        {
            _clock.Stop();
        }
        else if (!_clock.IsRunning)
        {
            _clock.Start();
        }

        RetryButton.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        BusyHint.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
        ShowReader(!failed);

        int chapters = deck.Chapters.Count;
        ShowNote(OutlineNote, deck.OutlineNote, failed: false);
        ShowNote(SummaryNote, deck.SummaryNote, deck.SummaryStep == StudyStep.Failed);
        ShowNote(CardsNote, deck.CardsNote, deck.CardsStep == StudyStep.Failed);
        ShowStep(OutlineIcon, OutlineLabel, OutlineDetail, OutlineFill, deck.OutlineStep,
            deck.OutlineStep == StudyStep.Done ? Loc.Count(chapters, "partie", "parties") : "", deck.OutlineStep == StudyStep.Done ? 1 : 0.08);
        ShowStep(SummaryIcon, SummaryLabel, SummaryDetail, SummaryFill, deck.SummaryStep,
            deck.SummaryStep != StudyStep.Working ? ""
                : chapters > 0 ? Loc.F("{0} / {1} parties", Math.Min(deck.SummaryParts, chapters), chapters)
                : deck.SummaryParts > 0 ? Loc.Count(deck.SummaryParts, "partie", "parties") : "",
            deck.SummaryStep == StudyStep.Done ? 1 : chapters > 0 ? Math.Max(0.04, deck.SummaryParts / (double)chapters) : 0);
        ShowStep(CardsIcon, CardsLabel, CardsDetail, CardsFill, deck.CardsStep,
            deck.CardsTarget > 0 && deck.CardsStep is StudyStep.Working or StudyStep.Done
                ? Loc.F("{0} / {1}", deck.CardsStep == StudyStep.Done ? deck.Cards.Count : deck.CardsWritten, deck.CardsTarget) : "",
            deck.CardsStep == StudyStep.Done ? 1 : deck.CardsTarget > 0 ? Math.Max(0.04, deck.CardsWritten / (double)deck.CardsTarget) : 0);
    }

    /// <summary>The course, its pages, and, while Notch works, the time it has taken so far (« 1:12 »); or why it stopped.</summary>
    private void ShowBusyText()
    {
        if (_deck is not { } deck || BusyPanel.Visibility != Visibility.Visible)
        {
            _clock.Stop();
            return;
        }

        if (deck.Failed && !deck.Busy)
        {
            BusyText.Text = deck.Progress;
            return;
        }

        var parts = new List<string> { Loc.F("« {0} »", deck.Title) };
        if (deck.Pages > 0)
        {
            parts.Add(Loc.Count(deck.Pages, "page", "pages"));
        }

        if (deck.Busy)
        {
            var spent = DateTimeOffset.Now - deck.Started;
            parts.Add($"{(int)spent.TotalMinutes}:{spent.Seconds:00}");
        }

        BusyText.Text = string.Join(" · ", parts);
    }

    /// <summary>What a step waits for, or why it failed (in red), under it; nothing when there is nothing to say.</summary>
    private static void ShowNote(TextBlock note, string text, bool failed)
    {
        note.Text = text;
        note.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        note.Foreground = new SolidColorBrush(failed ? Color.FromArgb(0xFF, 0xFF, 0x8A, 0x8A) : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>A step: waiting (dim), at work, done (a check), or failed; its detail, and its bar filled so far.</summary>
    private static void ShowStep(FontIcon icon, TextBlock label, TextBlock detail, Border fill, StudyStep step, string text, double done)
    {
        (string glyph, Color color) = step switch
        {
            StudyStep.Done => ("\uE73E", Color.FromArgb(0xFF, 0x6C, 0xD9, 0x7E)),
            StudyStep.Failed => ("\uE783", Color.FromArgb(0xFF, 0xFF, 0x7A, 0x7A)),
            StudyStep.Working => ("\uE895", Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            _ => ("\uE823", Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
        };
        icon.Glyph = glyph;
        icon.Foreground = new SolidColorBrush(color);
        label.Foreground = new SolidColorBrush(step == StudyStep.Waiting ? Color.FromArgb(0x8F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF));
        detail.Text = text;
        fill.Background = new SolidColorBrush(step switch
        {
            StudyStep.Done => Color.FromArgb(0xFF, 0x6C, 0xD9, 0x7E),
            StudyStep.Failed => Color.FromArgb(0xFF, 0xFF, 0x7A, 0x7A),
            _ => Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF),
        });
        ElementCompositionPreview.GetElementVisual(fill).Scale = new Vector3((float)Math.Clamp(done, 0, 1), 1, 1);
    }

    /// <summary>The mascot reads the course while Notch works on it (in its colour); put away, it stops (nothing runs).</summary>
    private void ShowReader(bool reading)
    {
        if (reading == _reading)
        {
            return;
        }

        _reading = reading;
        if (reading)
        {
            Reader.Wear(MascotPalette.Find(_settings.MascotColor), animate: false);
            Reader.IsShown = true;
            Reader.IsAttentive = true;
            Reader.Read(true);
        }
        else
        {
            Reader.Read(false);
            Reader.IsAttentive = false;
            Reader.IsShown = false;
        }
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (_deck is { } deck)
        {
            ViewModel.Retry(deck);
        }
    }

    // ── The summary sheet ───────────────────────────────────────────────────────────────────────────────────

    private void OnCopySummaryClick(object sender, RoutedEventArgs e)
    {
        if (_deck is not { Summary.Length: > 0 } deck)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText($"# {deck.Title}\n\n{deck.Summary}");
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        CopySummaryIcon.Glyph = "\uE73E";
        CopySummaryText.Text = Loc.T("Copié");
        _copyTimer.Stop();
        _copyTimer.Start();
    }

    // ── The cards ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every card of the course, chapter after chapter, the way they are worded; each one editable.</summary>
    private void FillCards()
    {
        CardsList.Children.Clear();
        if (_deck is not { } deck)
        {
            return;
        }

        var order = deck.Chapters.Select((chapter, index) => (chapter.Title, index)).DistinctBy(pair => pair.Title).ToDictionary(pair => pair.Title, pair => pair.index);
        var now = DateTimeOffset.Now;
        foreach (var card in deck.Cards.OrderBy(card => order.GetValueOrDefault(card.Chapter, int.MaxValue)).ThenBy(card => card.Id))
        {
            CardsList.Children.Add(card == _editing ? EditRow(deck, card) : Row(deck, card, now));
        }
    }

    private FrameworkElement Row(StudyDeck deck, StudyCard card, DateTimeOffset now)
    {
        var text = new StackPanel { Spacing = 6 };
        text.Children.Add(Text(card.Question, 14, primary: true, semibold: true));
        text.Children.Add(Text(card.Answer, 13, primary: false));
        if (card.HasChoices)
        {
            // The four choices: the right one lit green, the decoys plain.
            var wrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            wrap.Children.Add(StudySheet.Chip(card.Choice, Right));
            foreach (string decoy in card.Decoys)
            {
                wrap.Children.Add(StudySheet.Chip(decoy));
            }

            text.Children.Add(new ScrollViewer
            {
                Content = wrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
        }

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 2, 0, 0) };
        meta.Children.Add(StudySheet.Chip(Status(card, now), card.IsDue(now) ? Microsoft.UI.Colors.White : null));
        if (card.Source.Length > 0)
        {
            meta.Children.Add(StudySheet.Chip(card.Source));
        }

        if (card.Chapter.Length > 0)
        {
            meta.Children.Add(StudySheet.Chip(card.Chapter));
        }

        text.Children.Add(meta);

        var edit = IconButton("\uE70F", Loc.T("Modifier la carte"));
        edit.Click += (_, _) =>
        {
            _editing = card;
            FillCards();
        };
        var remove = IconButton("\uE74D", Loc.T("Supprimer la carte"));
        remove.Click += (_, _) =>
        {
            ViewModel.Delete(deck, card);
            ShowHeader();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        actions.Children.Add(edit);
        actions.Children.Add(remove);
        return Framed(text, actions);
    }

    private FrameworkElement EditRow(StudyDeck deck, StudyCard card)
    {
        var question = Field(card.Question, Loc.T("Question"));
        var answer = Field(card.Answer, Loc.T("Réponse"));
        var save = new MotionButton { Style = (Style)Application.Current.Resources["StudyPillStyle"], Content = Loc.T("Enregistrer") };
        save.Click += (_, _) =>
        {
            if (question.Text.Trim().Length > 0 && answer.Text.Trim().Length > 0)
            {
                ViewModel.Edit(deck, card, question.Text, answer.Text);
            }

            _editing = null;
            FillCards();
        };
        var cancel = new MotionButton { Style = (Style)Application.Current.Resources["StudyPillStyle"], Content = Loc.T("Annuler") };
        cancel.Click += (_, _) =>
        {
            _editing = null;
            FillCards();
        };

        var fields = new StackPanel { Spacing = 8 };
        fields.Children.Add(question);
        fields.Children.Add(answer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        fields.Children.Add(buttons);
        return Framed(fields, null);
    }

    /// <summary>A card's frame in the list: its content, its buttons on the right.</summary>
    private static Border Framed(FrameworkElement content, FrameworkElement? actions)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(content);
        if (actions is not null)
        {
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
        }

        // Glass, lit a little from above.
        var light = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 0), EndPoint = new Windows.Foundation.Point(0.5, 1) };
        light.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) });
        light.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF) });
        return new Border
        {
            Padding = new Thickness(18, 14, 12, 14),
            Background = light,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Child = grid,
        };
    }

    private TextBox Field(string text, string name)
    {
        var field = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 13,
            Header = name,
            RequestedTheme = ElementTheme.Dark,
        };
        field.GotFocus += (_, _) => KeyboardWanted?.Invoke(this, EventArgs.Empty);
        return field;
    }

    private static TextBlock Text(string text, double size, bool primary, bool semibold = false) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI Variable Text"),
        FontSize = size,
        FontWeight = semibold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = (Brush)Application.Current.Resources[primary ? "NotchPrimaryTextBrush" : "NotchSecondaryTextBrush"],
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    private static MotionButton IconButton(string glyph, string name)
    {
        var button = new MotionButton
        {
            Width = 28,
            MinWidth = 0,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Style = (Style)Application.Current.Resources["NotchPillButtonStyle"],
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    /// <summary>Where a card is: new, being learnt, due, or when it comes back.</summary>
    private static string Status(StudyCard card, DateTimeOffset now) => card.State switch
    {
        CardState.New => Loc.T("nouvelle"),
        CardState.Learning or CardState.Relearning => Loc.T("en apprentissage"),
        _ when card.Due <= now => Loc.T("à revoir"),
        _ => Loc.F("revient {0}", StudyScheduler.When(card.Due, now)),
    };

    // ── Mind maps ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The mind maps as they are: none yet (what they are, « Générer les cartes mentales », or Notch drawing them, or why it
    /// could not); or the notions part by part, and the map looked at (with, while Notch draws them again, how far it is).
    /// </summary>
    private void ShowMaps()
    {
        if (_deck is not { } deck)
        {
            return;
        }

        bool drawn = deck.MindMaps.Count > 0;
        bool drawing = deck.MindMapsStep == StudyStep.Working;
        MapsEmpty.Visibility = drawn ? Visibility.Collapsed : Visibility.Visible;
        MapsBrowser.Visibility = drawn ? Visibility.Visible : Visibility.Collapsed;
        string status = drawing
            ? deck.MindMapsNote.Length > 0 ? deck.MindMapsNote : Drawing(deck)
            : deck.MindMapsStep == StudyStep.Failed ? deck.MindMapsNote
            : deck.Busy && deck.Summary.Length == 0 ? Loc.T("Les cartes mentales se font à partir de la fiche : dès qu'elle est prête.")
            : "";
        var tone = new SolidColorBrush(deck.MindMapsStep == StudyStep.Failed && !drawing
            ? Color.FromArgb(0xFF, 0xFF, 0x8A, 0x8A)
            : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));

        if (!drawn)
        {
            MapsIcon.Foreground = new SolidColorBrush(Mix(Accent(_settings), Microsoft.UI.Colors.White, 0.2));
            DrawButton.Visibility = drawing ? Visibility.Collapsed : Visibility.Visible;
            DrawButton.IsEnabled = !(deck.Busy && deck.Summary.Length == 0);
            MapsStatus.Text = status;
            MapsStatus.Foreground = tone;
            MapsStatus.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // The parts gone through, the one being drawn half done.
            bool parts = drawing && deck.Chapters.Count > 0 && deck.MindMapsPart > 0;
            MapsBar.Visibility = parts ? Visibility.Visible : Visibility.Collapsed;
            ElementCompositionPreview.GetElementVisual(MapsFill).Scale = new Vector3(
                parts ? (float)Math.Clamp((deck.MindMapsPart - 0.5) / deck.Chapters.Count, 0.02, 1) : 0, 1, 1);
            return;
        }

        MapsWorking.Text = status;
        MapsWorking.Foreground = tone;
        MapsWorking.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RedrawButton.IsEnabled = !drawing;
        if (_listed != deck.MindMaps)
        {
            FillMapsList(deck);
        }

        ShowMap();
    }

    /// <summary>
    /// The whole course first; then each part: its number on a dot of its colour (the one of its branch in the overview)
    /// and its name, its notions under it, set in along a thin line of that colour.
    /// </summary>
    private void FillMapsList(StudyDeck deck)
    {
        _listed = deck.MindMaps;
        _mapEntries.Clear();
        MapsList.Children.Clear();
        MapsList.Children.Add(MapEntry(-1, Loc.T("Vue d'ensemble"), "\uE8A9", 180));
        var parts = MindMap.Overview(deck.Title, deck.Chapters, deck.MindMaps).Branches.Select(branch => MindMap.Key(branch.Label)).ToList();
        StackPanel? notions = null;
        string part = "";
        for (int i = 0; i < deck.MindMaps.Count; i++)
        {
            var map = deck.MindMaps[i];
            if (notions is null || map.Chapter != part)
            {
                part = map.Chapter;
                int number = parts.IndexOf(MindMap.Key(part)) + 1;
                notions = new StackPanel { Spacing = 2 };
                MapsList.Children.Add(PartGroup(part, number, MindMapDrawing.BranchColor(Math.Max(number - 1, 0)), notions));
            }

            notions.Children.Add(MapEntry(i, map.Notion, null, part.Length > 0 ? 150 : 180));
        }
    }

    /// <summary>A part of the course in the list: its number on a dot of its colour, its name, its notions set in under it.</summary>
    private static StackPanel PartGroup(string title, int number, Color color, StackPanel notions)
    {
        var group = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Spacing = 5 };
        if (title.Length == 0)
        {
            group.Children.Add(notions); // A course without parts: its notions only.
            return group;
        }

        var header = new Grid { Margin = new Thickness(6, 0, 0, 0), ColumnSpacing = 9 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new Border
        {
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Alpha(color, 0x2E)),
            Child = new TextBlock
            {
                Text = number > 0 ? number.ToString(Loc.Culture) : "•",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Mix(color, Microsoft.UI.Colors.White, 0.25)),
            },
        });
        var name = new TextBlock
        {
            Text = title,
            Margin = new Thickness(0, 2, 0, 0),
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["NotchPrimaryTextBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(name, title);
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        group.Children.Add(header);

        // Its notions, set in under the dot, along a thin line of its colour.
        notions.Margin = new Thickness(16, 0, 0, 0);
        notions.Padding = new Thickness(8, 0, 0, 0);
        notions.BorderThickness = new Thickness(1.5, 0, 0, 0);
        notions.BorderBrush = new SolidColorBrush(Alpha(color, 0x55));
        group.Children.Add(notions);
        return group;
    }

    /// <summary>
    /// How far Notch is: the part it draws, of how many, and its name; the maps drawn so far (the parts unknown: only
    /// those).
    /// </summary>
    private static string Drawing(StudyDeck deck)
    {
        var said = new List<string>();
        if (deck.Chapters.Count > 0 && deck.MindMapsPart > 0)
        {
            said.Add(Loc.F("Partie {0} / {1}", deck.MindMapsPart, deck.Chapters.Count));
            said.Add(deck.Chapters[Math.Min(deck.MindMapsPart, deck.Chapters.Count) - 1].Title);
        }

        if (deck.MindMapsWritten > 0)
        {
            said.Add(Loc.Count(deck.MindMapsWritten, "carte", "cartes"));
        }

        return said.Count == 0 ? Loc.T("Notch lit la fiche et dessine les cartes mentales…")
            : Loc.F("Notch dessine les cartes mentales : {0}", string.Join(" · ", said));
    }

    /// <summary>A notion of the list (or the whole course): a click shows its map.</summary>
    private MotionButton MapEntry(int index, string text, string? glyph, double width)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (glyph is not null)
        {
            label.Children.Add(new FontIcon { Glyph = glyph, FontSize = 11 });
        }

        label.Children.Add(new TextBlock { Text = text, MaxWidth = width, TextTrimming = TextTrimming.CharacterEllipsis });
        var entry = new MotionButton
        {
            Style = (Style)Application.Current.Resources["StudyChoiceStyle"],
            Content = label,
            Tag = index,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HoverScale = 0.02,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
        };
        ToolTipService.SetToolTip(entry, text);
        _mapEntries.Add(entry);
        entry.Click += (_, _) =>
        {
            if (_mapIndex != index)
            {
                _mapIndex = index;
                ShowMap();
            }
        };
        return entry;
    }

    /// <summary>The map looked at, drawn to fit; its part and its place among them above it; its notion lit in the list.</summary>
    private void ShowMap()
    {
        if (_deck is not { MindMaps.Count: > 0 } deck)
        {
            return;
        }

        var maps = deck.MindMaps;
        _mapIndex = Math.Clamp(_mapIndex, -1, maps.Count - 1);
        bool overview = _mapIndex < 0;
        var map = overview ? MindMap.Overview(deck.Title, deck.Chapters, maps) : maps[_mapIndex];
        MapPart.Text = (overview ? Loc.T("Vue d'ensemble") : map.Chapter).ToUpper(Loc.Culture);
        MapCount.Text = overview
            ? Loc.Count(maps.Count, "carte mentale", "cartes mentales")
            : Loc.F("{0} / {1}", _mapIndex + 1, maps.Count);
        string key = MapInk.KeyOf(map, overview);
        if (!deck.MapInks.TryGetValue(key, out var ink))
        {
            ink = new MapInk();
            deck.MapInks[key] = ink;
        }

        var accent = Accent(_settings);
        _mapCanvas.Show(MindMapDrawing.Draw(map, accent, overview), $"{deck.Id}/{key}", ink, accent);
        PreviousMapButton.IsEnabled = _mapIndex > -1;
        NextMapButton.IsEnabled = _mapIndex < maps.Count - 1;
        foreach (var entry in _mapEntries)
        {
            bool chosen = entry.Tag is int index && index == _mapIndex;
            entry.Background = chosen ? _chosenBrush : _clearBrush;
            if (chosen)
            {
                entry.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
            }
        }
    }

    /// <summary>What was drawn on the maps, written now (a moment after the last stroke, or as the sheet is put away).</summary>
    private void WriteInk()
    {
        _inkTimer?.Stop();
        if (_inked is { } deck)
        {
            _inked = null;
            ViewModel.SaveInk(deck);
        }
    }

    private void OnPreviousMapClick(object sender, RoutedEventArgs e) => StepMap(-1);

    private void OnNextMapClick(object sender, RoutedEventArgs e) => StepMap(1);

    private void StepMap(int step)
    {
        if (_deck is { MindMaps.Count: > 0 } deck)
        {
            int index = Math.Clamp(_mapIndex + step, -1, deck.MindMaps.Count - 1);
            if (index != _mapIndex)
            {
                _mapIndex = index;
                ShowMap();
            }
        }
    }

    /// <summary>« Générer les cartes mentales », or « Redessiner »: Notch draws them (again), from the sheet.</summary>
    private void OnDrawClick(object sender, RoutedEventArgs e)
    {
        if (_deck is { } deck)
        {
            ViewModel.DrawMindMaps(deck);
        }
    }

    // ── New cards ───────────────────────────────────────────────────────────────────────────────────────────

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        bool open = MoreStrip.Visibility != Visibility.Visible;
        MoreStrip.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            BuildFocus();
            ShowMoreStatus();
            Rise(MoreStrip, 0);
        }
    }

    /// <summary>What new cards can be on: the whole course, the weak points (if there are), each chapter.</summary>
    private void BuildFocus()
    {
        FocusChoices.Children.Clear();
        if (_deck is not { } deck)
        {
            return;
        }

        Add(CardFocus.All, Loc.T("Tout le cours"));
        int weak = deck.Cards.Count(card => card.Lapses > 0 || card.State == CardState.Relearning);
        if (weak > 0)
        {
            Add(new CardFocus(Weak: true), Loc.F("Mes points faibles ({0})", weak));
        }

        foreach (var chapter in deck.Chapters)
        {
            Add(new CardFocus(chapter), chapter.Title);
        }

        void Add(CardFocus focus, string label)
        {
            var choice = new MotionButton { Style = (Style)Application.Current.Resources["StudyChoiceStyle"], Content = label, Tag = focus };
            choice.Click += (_, _) =>
            {
                _focus = focus;
                ShowChoices();
            };
            FocusChoices.Children.Add(choice);
        }

        ShowChoices();
    }

    private void BuildCounts()
    {
        foreach (int count in CountOptions)
        {
            var choice = new MotionButton { Style = (Style)Application.Current.Resources["StudyChoiceStyle"], Content = count.ToString(Loc.Culture), Tag = count };
            choice.Click += (_, _) =>
            {
                _count = count;
                ShowChoices();
            };
            CountChoices.Children.Add(choice);
        }

        ShowChoices();
    }

    private void ShowChoices()
    {
        foreach (var choice in FocusChoices.Children.OfType<MotionButton>())
        {
            choice.Background = Equals(choice.Tag, _focus) ? _chosenBrush : _clearBrush;
        }

        foreach (var choice in CountChoices.Children.OfType<MotionButton>())
        {
            choice.Background = Equals(choice.Tag, _count) ? _chosenBrush : _clearBrush;
        }
    }

    private void ShowMoreStatus()
    {
        bool busy = _deck is { Busy: true };
        GenerateButton.IsEnabled = !busy;
        MoreButton.IsEnabled = !busy;
        if (_deck is { Busy: true, CardsStep: StudyStep.Working } working)
        {
            MoreStatus.Text = Loc.F("Notch écrit les cartes… {0} / {1}", working.CardsWritten, working.CardsTarget);
        }
        else if (_deck is { Failed: true } failed)
        {
            MoreStatus.Text = failed.Progress;
        }
    }

    private async void OnGenerateClick(object sender, RoutedEventArgs e)
    {
        if (_deck is not { } deck)
        {
            return;
        }

        int made = await ViewModel.MoreCardsAsync(deck, _focus, _count);
        if (made > 0 && _deck == deck)
        {
            MoreStatus.Text = Loc.F("+{0}, ajoutées au cours. Elles arrivent dans vos révisions.", Loc.Count(made, "carte", "cartes"));
            _review.Extend(new ReviewSession([deck])); // The new cards are met in this session too.
        }
    }

    // ── Deleting ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A first click asks (the pill turns red), a second one deletes the course, its cards and its copy.</summary>
    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_deck is not { } deck)
        {
            return;
        }

        if (!_confirmingDelete)
        {
            _confirmingDelete = true;
            DeleteText.Text = Loc.T("Confirmer ?");
            DeleteButton.Background = (Brush)Application.Current.Resources["NotchDangerBrush"];
            _deleteTimer.Start();
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        ViewModel.Delete(deck);
    }

    private void ResetDelete()
    {
        _deleteTimer.Stop();
        _confirmingDelete = false;
        DeleteText.Text = Loc.T("Supprimer");
        DeleteButton.Background = (Brush)Application.Current.Resources["NotchPillBrush"];
    }

    // ── Motion ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fades in, rising a little: what just changed.</summary>
    private static void Rise(UIElement element, int delay) => Arrive(element, new Vector3(0, 8, 0), delay);

    /// <summary>Comes in from the side.</summary>
    private static void Slide(UIElement element, float x, int delay) => Arrive(element, new Vector3(x, 0, 0), delay);

    private DispatcherQueueTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }
}
