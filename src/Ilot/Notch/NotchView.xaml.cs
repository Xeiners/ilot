using System.Diagnostics;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Ilot.Composition;
using Ilot.Controls;
using Ilot.Settings;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;
using Ilot.Localization;
using Ilot.Mascot;
using Ilot.Modules.Media.Audio;
using Ilot.Quick;

namespace Ilot.Notch;

/// <summary>
/// The notch: hosts the composition body, the module views and their tabs, turns pointer input into
/// <see cref="NotchController"/> events and drives the morph between phases.
/// </summary>
public sealed partial class NotchView : UserControl
{
    // Once inside, the pointer must leave a slightly larger area to count as outside: no flicker on the edge.
    private const double HoverHysteresis = 6;
    private static readonly TimeSpan SettleMargin = TimeSpan.FromMilliseconds(40);

    // Tab bar: a band along the top edge of the open notch, above the modules' content.
    private const double TabBarHeight = 34; // The tabs (5 + 22) and a little air before the content.
    private const double TabTop = 5;
    private const double TabMinWidth = 72; // Each tab hugs its label (so five fit beside a search field), within these.
    private const double TabPadding = 12;
    private const double TabIconRoom = 18; // The glyph (12) and the spacing before the label (6).
    private const double TabHeight = 22;

    // The mascot: its size closed (in the pill's right end, or its nest) and open (in its little notch), and how far an
    // announcement moves aside for it (the message starts right after it, rather than over a visualizer).
    private const double MascotClosedSize = 28;
    private const double MascotOpenSize = 28;
    private const double MascotToastShift = 40;
    private const double TabSpacing = 4;
    private const double TabInset = 16;

    // Announcements ("Texte copié"…): the closed pill grows to the right only, by just what the message needs. The
    // message takes over the right end of the pill (ToastOverlap: where a module's compact visualizer rides,
    // which steps aside with n.Toast) and runs into the extension.
    private const double ToastOverlap = 38;
    private const double ToastEndPadding = 18;
    private const double ToastMinExtra = 24;
    private static readonly TimeSpan ToastTime = TimeSpan.FromMilliseconds(1800);
    private static readonly TimeSpan AnnouncementShield = TimeSpan.FromMilliseconds(700);

    // A presented card: the pill grows down to hold it, with this much room around it, and these corners.
    private const double CardPadding = 16;
    private const double CardBottom = 14;
    private const double CardRadius = 30;

    private NotchLayout _layout; // Its handle follows the settings (see ApplyHandle).
    private readonly NotchMotion _motion;
    private readonly NotchController _controller;
    private readonly AppSettings _settings;
    private readonly DispatcherQueueTimer _idleTimer;
    private readonly DispatcherQueueTimer _settleTimer;
    private readonly DispatcherQueueTimer _toastTimer;

    // A level shown on the closed pill (the volume, see ShowLevel), in the plain pill: how long it stays, and its track,
    // after the glyph, to near the pill's right end, the mascot riding its fill's end (Head).
    private const double GaugeTrackLeft = 42;
    private const double GaugeTrackRight = 18;
    private const double GaugeTrackThickness = 6;
    private const double GaugeKnob = 10;
    private const double GaugeGlyphLeft = 14;
    private const string GaugeLeft = "((n.HostWidth - n.Width) * 0.5 + tl)";
    private const string GaugeRight = "((n.HostWidth + n.Width) * 0.5 - tr)";
    private const string GaugeHead = "Lerp(" + GaugeLeft + " + k, " + GaugeRight + " - k, Clamp(n.GaugeLevel, 0, 1))";
    private static readonly TimeSpan GaugeTime = TimeSpan.FromMilliseconds(1600);
    private static readonly Spring GaugeMove = new(0.30, 0.86);
    private readonly DispatcherQueueTimer _gaugeTimer;
    private readonly DispatcherQueueTimer _glowTimer;
    private bool _glowing;
    private CompositionColorBrush? _gaugeFill;
    private bool _gaugeShown;
    private double _gaugeLast;
    private readonly List<ModuleSlot> _modules = [];
    private readonly List<DropTarget> _dropTargets = [];
    private SpringChannel? _tabLeft;
    private SpringChannel? _tabRight;
    private SpringChannel? _tabsShown;
    private CompositionPropertySet? _tabState;
    private bool _mascotHosted;
    private Rect[] _sideHover = [];

    // The quick controls left of the open notch (see BindQuick): their services, and the levels they show.
    private const string BrightnessGlyph = "\uE706";
    private readonly QuickServices? _quick;
    private ContainerVisual? _quickPods;
    private bool _quickOpen;
    private double _volumeLevel;
    private double _brightnessLevel;
    private IDisposable? _quickHold;
    private SpringChannel? _mixerShown;
    private bool _mixerOpen;
    private bool _mixerShowing; // Open, or still fading out.
    private readonly ContainerVisual _pod;
    private readonly NotchSurface _surface;
    private int _lastTab;
    private bool _showingModule;
    private double[] _tabLefts = [];
    private double[] _tabWidths = [];
    private NotchPhase _phase = NotchPhase.Collapsed;
    private bool _peeking;

    // The input region shrinking with the closed notch (see Track).
    private static readonly TimeSpan TrackLag = TimeSpan.FromMilliseconds(34);
    private const double TrackMargin = 2;
    private bool _tracking;
    private Rect _shrinkCeiling;
    private long _bubbleGoneAt;
    private bool _idle;
    private Rect _peekBounds = Rect.Empty;
    private Rect _hoverBounds;
    private Rect _settledInputBounds;
    private bool _pointerInside;
    private IDisposable? _menuHold;
    private double _compactWidth;
    private NotchShape? _expandedShape;
    private int _selected;
    private bool _toastShown;
    private KeyboardLease? _keyboard;
    private DropTarget? _drag;
    private IDisposable? _dragHold;
    private double _toastExtra;
    private NotchActivity? _activity;

    // The modules' live activities, in the order they began: the last one shows (a course being prepared over the
    // Pomodoro's timer, which comes back once it is done).
    private readonly List<(int Module, NotchActivity Activity)> _activities = [];
    private NotchActivity? _announcement;
    private long _announcedAt;
    private Size? _presented;
    private Question? _question;
    private NotchBubble? _bubble;
    private Resize? _resize;
    private Rect _bubbleBounds;
    private TimeSpan _bubbleMotion;
    private bool _questionShown;
    private bool _pointerOnQuestion;
    private DispatcherQueueTimer? _presentTimer;
    private SpringChannel? _ringProgress;
    private CompositionColorBrush? _ringBrush;

    /// <param name="quick">The quick controls' services (volume, brightness): the notch has them; a search satellite does not.</param>
    public NotchView(
        NotchLayout layout, IEnumerable<INotchModule> modules, DispatcherQueue dispatcher, AppSettings settings, QuickServices? quick = null)
    {
        _quick = quick;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        _layout = layout;
        Width = layout.CanvasWidth;
        Height = layout.CanvasHeight;

        _motion = new NotchMotion(ElementCompositionPreview.GetElementVisual(this).Compositor, layout);
        var surface = new NotchSurface(_motion);
        _surface = surface;
        surface.Tint(MascotPalette.Find(settings.MascotColor)); // The assistant's glow, in the mascot's colour.
        _pod = surface.Pod;
        _quickPods = surface.QuickPods;
        ElementCompositionPreview.SetElementChildVisual(SurfaceHost, surface.Root);
        ElementCompositionPreview.GetElementVisual(ContentLayer).Clip = surface.CreateContentClip();
        ElementCompositionPreview.GetElementVisual(BubbleLayer).Clip = surface.CreateBubbleClip();
        foreach (var grip in new[] { LeftGrip, RightGrip })
        {
            grip.PointerPressed += OnGripPressed;
            grip.PointerMoved += OnGripMoved;
            grip.PointerReleased += OnGripReleased;
            grip.PointerCaptureLost += (_, _) => EndResize();
        }


        _controller = new NotchController(dispatcher);
        _controller.PhaseChanged += OnPhaseChanged;
        _controller.PinnedChanged += OnPinnedChanged;
        _controller.EngagedChanged += (_, _) => EngagedChanged?.Invoke(this, EventArgs.Empty);

        ModuleHost.Width = layout.Expanded.Width;
        ModuleHost.Height = layout.Expanded.Height; // Then as large as the views (see FitModuleHost).
        ModuleHost.Margin = new Thickness(0, layout.TopInset, 0, 0);
        _settleTimer = dispatcher.CreateTimer();
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick += (_, _) =>
        {
            Track(false);
            PublishInputBounds(_settledInputBounds);
            PublishSideBounds(_sideHover);
        };

        _toastTimer = dispatcher.CreateTimer();
        _toastTimer.Interval = ToastTime;
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => EndAnnouncement();
        BindToast();

        _gaugeTimer = dispatcher.CreateTimer();
        _gaugeTimer.Interval = GaugeTime;
        _gaugeTimer.IsRepeating = false;
        _gaugeTimer.Tick += (_, _) => EndGauge();
        _glowTimer = dispatcher.CreateTimer();
        _glowTimer.IsRepeating = false;
        _glowTimer.Tick += (_, _) => _surface.Shimmer(_glowing); // Faded out: its light stops turning.

        _presentTimer = dispatcher.CreateTimer();
        _presentTimer.IsRepeating = false;
        _presentTimer.Tick += (_, _) => EndPresentation(reshape: true);

        // Nothing going on (no music, no live activity): after a while, the closed notch shrinks to its handle (see
        // UpdateIdle). So from the start, until a module has something.
        _settings = settings;
        _idle = settings.HideWhenIdle || settings.Minimized;
        _idleTimer = dispatcher.CreateTimer();
        _idleTimer.IsRepeating = false;
        _idleTimer.Tick += (_, _) => SetIdle(true);

        HostModules(modules.ToList());
        BindGauge();
        BindMascot();
        BindQuick();

        // Under the pointer, the closed pill goes back into its drag handle (see SetPeeking), and the modules' compact
        // content (artwork, lyrics, visualizer…) hides meanwhile (n.Mini), as it does in the mascot's nest (n.Nest).
        _motion.Bind("Lerp(1, Clamp(n.Expansion * 2.5, 0, 1), Clamp(Max(Max(n.Mini, n.Nest), n.Gauge), 0, 1))")
            .Start(ElementCompositionPreview.GetElementVisual(ModuleHost), "Opacity");

        // The handle's width and the spot that opens the notch, as set in the settings (live).
        ApplyHandle(settings);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.HandleWidth) or nameof(AppSettings.HandleReach))
            {
                ApplyHandle(settings);
                if (_phase != NotchPhase.Expanded)
                {
                    MorphTo(_phase, Motion.Settle);
                }
            }
            else if (e.PropertyName == nameof(AppSettings.MascotColor))
            {
                Buddy.Wear(MascotPalette.Find(settings.MascotColor));
                _gaugeFill!.Color = MascotPalette.Find(settings.MascotColor).Tone;
                _surface.Tint(MascotPalette.Find(settings.MascotColor));
                TintQuick(animate: true);
            }
            else if (e.PropertyName == nameof(AppSettings.ShowMascot))
            {
                UpdateMascot();
                ShowStatus(pop: false);
                if (_phase != NotchPhase.Expanded)
                {
                    MorphTo(_phase, Motion.Settle);
                }
            }
            else if (e.PropertyName is nameof(AppSettings.HideWhenIdle) or nameof(AppSettings.IdleSeconds) or nameof(AppSettings.Minimized))
            {
                _idleTimer.Stop();
                SetIdle(false);
                UpdateIdle();
            }
        };

        // handledEventsToo: buttons inside the notch must not hide the pointer from the hover logic.
        Root.AddHandler(PointerEnteredEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        Root.AddHandler(PointerMovedEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        Root.AddHandler(PointerExitedEvent, new PointerEventHandler(OnPointerExited), handledEventsToo: true);
        Root.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), handledEventsToo: false);
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(OnPressed), handledEventsToo: true);
        Root.Tapped += OnTapped;
        Root.DoubleTapped += OnDoubleTapped;

        _hoverBounds = layout.BoundsOf(layout.Hidden);
    }

    /// <summary>Raised when the part of the window that must receive the mouse changes (logical pixels).</summary>
    public event EventHandler<Rect>? InputBoundsChanged;

    /// <summary>Raised when the window must take focus, e.g. to show the context menu.</summary>
    public event EventHandler? ActivationRequested;

    /// <summary>
    /// Raised when a shortcut opens the closed notch on a module (Ctrl + Q, Windows + V…), before it opens: the window shows
    /// first, if an app full screen had put it out of sight.
    /// </summary>
    public event EventHandler? Summoned;

    /// <summary>Raised when the user asks for the settings (context menu).</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when a module needs to type: the window must take the keyboard.</summary>
    public event EventHandler? KeyboardRequested;

    /// <summary>Raised when the module is done typing: the keyboard goes back to the app that had it.</summary>
    public event EventHandler? KeyboardReleased;

    public Rect InputBounds { get; private set; } = Rect.Empty;

    /// <summary>
    /// The other parts of the window's input region: the little notches beside the open one (the mascot's, the quick
    /// controls'; none otherwise). Apart from <see cref="InputBounds"/>, so that the empty corners between them and the
    /// notch stay the desktop's.
    /// </summary>
    public IReadOnlyList<Rect> SideInputBounds { get; private set; } = [];

    /// <summary>
    /// Closed and left alone: nothing typed, shown (a question, a card, a bubble), dragged or held, the pointer not on
    /// it. Free to go to another screen.
    /// </summary>
    public bool IsResting =>
        _phase == NotchPhase.Collapsed && !_controller.IsPinned && _keyboard is null && _bubble is null
        && _presented is null && _drag is null && _menuHold is null && !_pointerInside;

    /// <summary>Entrance: the notch drops out of the screen edge.</summary>
    public void Arrive()
    {
        if (_question is { } waiting && _presented is null)
        {
            ShowCard(waiting.Card, waiting.Size, question: true);
        }

        MorphTo(NotchPhase.Collapsed, Motion.Arrive);
    }

    /// <summary>The reverse of <see cref="Arrive"/>: the notch withdraws into the screen edge, closed and unpinned.</summary>
    /// <returns>How long the motion takes, so the window can be hidden once it is over.</returns>
    public TimeSpan Retreat()
    {
        EndTour(drawBack: false);
        EndAnnouncement();
        EndPresentation(reshape: false, resume: false);
        DrawBubbleBack();
        _controller.Reset();
        _phase = NotchPhase.Collapsed;
        _pointerInside = false;
        ForgetPeek();
        _settleTimer.Stop();
        UpdateHitTesting();
        _motion.Pin.AnimateTo(0, Motion.Toggle);
        _motion.Extra.AnimateTo(0, Motion.Collapse.Width);
        return _motion.MorphTo(_layout.Hidden, expansion: 0, Motion.Collapse);
    }

    // ── Modules and tabs ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every module view is stacked over the open notch, below the tab bar when there is more than one module.
    /// The first module is shown first.
    /// </summary>
    private void HostModules(IReadOnlyList<INotchModule> modules)
    {
        bool tabbed = modules.Count(module => module.HasTab) > 1;
        double contentTop = tabbed ? TabBarHeight : 0;
        for (int i = 0; i < modules.Count; i++)
        {
            int index = i;
            var context = new NotchContext(
                _motion,
                _controller,
                contentTop,
                selected: i == 0,
                width => SetCompactWidth(index, width),
                ShareBeat,
                ShowLevel,
                ActMascot,
                (microphone, voice) =>
                {
                    Buddy.Hear(microphone);
                    Buddy.Voice(voice);
                },
                reading => Buddy.Read(reading),
                playing => Buddy.Play(playing),
                shape => SetExpandedShape(index, shape),
                () => Reshape(index),
                Announce,
                TakeKeyboard,
                activity => SetActivity(index, activity),
                presence => SetPresence(index, presence),
                target => _dropTargets.Add(new DropTarget(index, target)),
                () => ShowModule(index),
                Present,
                Ask,
                ShowBubble);
            // Registered before the view exists: a module may already reshape the notch from its constructor.
            var slot = new ModuleSlot(modules[i], context);
            _modules.Add(slot);
            slot.View = modules[i].CreateView(context);
            ModuleHost.Children.Add(slot.View);
        }

        FitModuleHost();

        if (tabbed)
        {
            BuildTabBar();
        }

        UpdateHitTesting();
    }

    private void BuildTabBar()
    {
        var compositor = _motion.Compositor;
        _tabLefts = new double[_modules.Count];
        _tabWidths = new double[_modules.Count];
        TabBar.Height = TabHeight;
        // From the left edge of the open notch: its right end stays free for the modules' own buttons (search…).
        TabBar.Margin = new Thickness((_layout.CanvasWidth - _layout.Expanded.Width) / 2 + TabInset, _layout.TopInset + TabTop, 0, 0);
        TabStrip.Spacing = TabSpacing;

        double stripWidth = 0;
        for (int i = 0; i < _modules.Count; i++)
        {
            int index = i;
            var module = _modules[i];
            if (!module.Module.HasTab)
            {
                continue; // Reached by its shortcut only.
            }

            var title = new TextBlock
            {
                Text = module.Module.Title,
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _tabWidths[i] = Math.Max(TabMinWidth, Math.Ceiling(title.DesiredSize.Width + TabIconRoom + 2 * TabPadding));
            _tabLefts[i] = stripWidth;
            stripWidth += _tabWidths[i] + TabSpacing;

            var label = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new FontIcon { Glyph = module.Module.Glyph, FontSize = 12 }, title },
            };
            var tab = new MotionButton
            {
                Style = (Style)Application.Current.Resources["NotchTabButtonStyle"],
                Width = _tabWidths[i],
                Height = TabHeight,
                HoverScale = 0.04,
                PressScale = 0.08,
                HaloRestScale = 0.9,
                HaloOpacity = 0.08,
                Content = label,
            };
            AutomationProperties.SetName(tab, module.Module.Title);
            tab.Click += (_, _) => SelectTab(index);
            TabStrip.Children.Add(tab);

            // The chosen tab reads bright, the others recede.
            module.Context.Bind("Lerp(0.5, 1, Clamp(t.Selected, 0, 1))")
                .Start(ElementCompositionPreview.GetElementVisual(label), "Opacity");
        }

        TabBar.Width = stripWidth - TabSpacing;

        // Selection pill: one spring per edge. The edge on the way leads and the other one follows, so the pill
        // stretches toward the new tab, then gathers there.
        var state = compositor.CreatePropertySet();
        _tabLeft = new SpringChannel(state, "Left", 0);
        _tabRight = new SpringChannel(state, "Right", _tabWidths[0]);
        _tabsShown = new SpringChannel(state, "Shown", 1);
        _tabState = state;
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2((float)TabHeight / 2);
        Pill("Vector2(b.Left, 0)").Start(geometry, "Offset");
        Pill("Vector2(b.Right - b.Left, h)").With("h", TabHeight).Start(geometry, "Size");
        var pill = compositor.CreateSpriteShape(geometry);
        pill.FillBrush = compositor.CreateColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        var indicator = compositor.CreateShapeVisual();
        indicator.Size = new Vector2((float)TabBar.Width, (float)TabHeight);
        indicator.Shapes.Add(pill);
        ElementCompositionPreview.SetElementChildVisual(TabIndicatorHost, indicator);

        // The tabs bloom in with the content as the notch opens, and leave first.
        ElementCompositionPreview.SetIsTranslationEnabled(TabBar, true);
        var bar = ElementCompositionPreview.GetElementVisual(TabBar);
        // They also step aside for a module without a tab (the search).
        _motion.Bind("Clamp((n.Expansion - 0.45) * 2.4, 0, 1) * Clamp(b.Shown, 0, 1)").With("b", state).Start(bar, "Opacity");
        // A module widening the open notch (the note sheet…): the tabs keep to its left edge as it grows.
        _motion.Bind("Vector3(-(Max(n.Width, w) - w) * 0.5, (1 - n.Progress) * -6, 0)")
            .With("w", _layout.Expanded.Width)
            .Start(bar, "Translation");

        CompositionExpression Pill(string expression) => _motion.Bind(expression).With("b", state);
    }

    /// <summary>
    /// Opens the notch on a module (a shortcut…), whatever the pointer does: straight onto it if the notch was closed,
    /// rather than sliding over from the last tab.
    /// </summary>
    private void ShowModule(int index)
    {
        if (_phase != NotchPhase.Expanded)
        {
            Summoned?.Invoke(this, EventArgs.Empty); // The window shows first, if it was out of sight.
            SelectTab(index, animate: false);
        }

        _showingModule = true;
        _controller.Open();
        _showingModule = false;
        SelectTab(index);
    }

    private void SelectTab(int index, bool animate = true)
    {
        if (index == _selected || index >= _modules.Count)
        {
            return;
        }

        var before = OpenNow;
        bool forward = index > _selected;
        _selected = index;
        for (int i = 0; i < _modules.Count; i++)
        {
            _modules[i].Context.Select(i == index, side: i < index ? -1 : 1, animate);
        }

        bool tab = _modules[index].Module.HasTab;
        if (tab && _tabLeft is not null && _tabRight is not null)
        {
            _lastTab = index;
            double left = _tabLefts[index];
            if (animate)
            {
                _tabLeft.AnimateTo(left, forward ? Motion.TabTrail : Motion.TabLead);
                _tabRight.AnimateTo(left + _tabWidths[index], forward ? Motion.TabLead : Motion.TabTrail);
            }
            else
            {
                _tabLeft.Snap(left);
                _tabRight.Snap(left + _tabWidths[index]);
            }
        }

        if (animate)
        {
            _tabsShown?.AnimateTo(tab ? 1 : 0, Motion.Fade);
        }
        else
        {
            _tabsShown?.Snap(tab ? 1 : 0);
        }

        UpdateHitTesting();
        UpdateMascot();
        UpdateQuick();

        // A module with a shape of its own (the search bar): the open notch takes it, and gives it back.
        if (_phase == NotchPhase.Expanded && OpenNow != before)
        {
            MorphTo(NotchPhase.Expanded, Motion.Expand);
        }

        if (tab)
        {
            TourBackOnTab();
            TourDid(TourWait.Tab);
        }
        else if (_tour is { Away: false } tour)
        {
            // A shortcut opened the search (or the assistant) during the tour: it steps aside, and comes back to this
            // step once that is closed, rather than ending.
            StepAside(tour, advance: false);
        }
    }

    /// <summary>The open notch's shape now: the shown module's own, else the usual one (enlarged on request).</summary>
    private NotchShape OpenNow =>
        (_selected < _modules.Count ? _modules[_selected].Module.OpenShape : null) ?? _layout.OpenShape(_expandedShape);

    /// <summary>Only the open notch takes clicks, and only on the tab shown: the others are invisible, not gone.</summary>
    private void UpdateHitTesting()
    {
        bool open = _phase == NotchPhase.Expanded;
        TabBar.IsHitTestVisible = open && (_selected >= _modules.Count || _modules[_selected].Module.HasTab);
        for (int i = 0; i < _modules.Count; i++)
        {
            if (_modules[i].View is { } view)
            {
                view.IsHitTestVisible = open && i == _selected;
            }
        }
    }

    /// <summary>A module widens (or restores) the compact notch: reshape at once if it is not open.</summary>
    private void SetCompactWidth(int module, double width)
    {
        _modules[module].CompactWidth = width;
        double widest = _modules.Max(slot => slot.CompactWidth);
        if (widest == _compactWidth)
        {
            return;
        }

        _compactWidth = widest;
        if (_phase != NotchPhase.Expanded)
        {
            MorphTo(_phase, Motion.Settle);
        }
    }

    /// <summary>
    /// The mascot's nest: at rest, rather than its handle, the closed notch keeps a small round pill (as tall as the
    /// closed one) where the mascot sleeps. Under the pointer it gives way to the handle, as the pill does.
    /// </summary>
    private NotchShape NestShape(NotchPhase phase)
    {
        var collapsed = _layout.Collapsed;
        double width = collapsed.Height;
        double radius = Math.Min(collapsed.Radius, width / 2);
        return phase == NotchPhase.Primed
            ? new(width + 10, collapsed.Height + 3, radius + 1.5)
            : new(width, collapsed.Height, radius);
    }

    /// <summary>
    /// A module enlarges (or restores) the open notch. Views are laid out at the open size, so the host follows
    /// at once; the shape springs to it if the notch is open. The largest request wins.
    /// </summary>
    private void SetExpandedShape(int module, NotchShape? shape)
    {
        _modules[module].ExpandedShape = shape;
        NotchShape? largest = null;
        foreach (var slot in _modules)
        {
            if (slot.ExpandedShape is { } requested)
            {
                largest = largest is { } current
                    ? new(Math.Max(current.Width, requested.Width), Math.Max(current.Height, requested.Height), Math.Max(current.Radius, requested.Radius))
                    : requested;
            }
        }

        if (largest == _expandedShape)
        {
            return;
        }

        _expandedShape = largest;
        FitModuleHost();
        if (_phase == NotchPhase.Expanded)
        {
            MorphTo(NotchPhase.Expanded, Motion.Expand);
        }
    }

    /// <summary>
    /// Views are laid out at the open size: the usual one (enlarged on request), or a module's own when it is taller (the
    /// Révisions tab, its sections above its courses); the host as large as the largest.
    /// </summary>
    private void FitModuleHost()
    {
        var open = _layout.OpenShape(_expandedShape);
        double height = open.Height;
        foreach (var slot in _modules)
        {
            if (slot.View is not { } view)
            {
                continue;
            }

            view.Height = slot.Module.OpenShape is { } own && own.Height > open.Height ? own.Height : open.Height;
            view.VerticalAlignment = VerticalAlignment.Top;
            height = Math.Max(height, view.Height);
        }

        ModuleHost.Width = open.Width;
        ModuleHost.Height = height;
    }

    /// <summary>A module's own open shape changed (see NotchContext.Reshape): the notch follows if it shows it, and so does a bubble under it.</summary>
    private void Reshape(int module)
    {
        FitModuleHost();
        if (module != _selected || _phase != NotchPhase.Expanded)
        {
            return;
        }

        if (_bubble is { } bubble)
        {
            MoveBubble(bubble.Size); // Its top rests under the new shape (the drop itself follows n.Height).
        }

        MorphTo(NotchPhase.Expanded, Motion.Expand);
    }

    // ── Drag and drop ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Something a module takes is dragged over the notch: it opens on that module's tab, and stays open while the
    /// drag goes on. Drags started from the notch itself (a clipboard tile…) are left alone.
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (IsTourSample(e.DataView))
        {
            DragTourSample(e);
            return;
        }

        var target = _drag ?? FindDropTarget(e.DataView);
        if (target is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = target.Target.DropCaption;
        if (_drag is null)
        {
            _drag = target;
            _dragHold = _controller.Hold();
            _controller.Open();
            SelectTab(target.Module);
            target.Target.OnDragHover(true);
        }

        if (_modules[target.Module].View is { } view)
        {
            target.Target.OnDragMoved(e.GetPosition(view));
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // Leave events also come from the elements inside the notch: only leaving the notch itself counts.
        if (_drag is not null && !_hoverBounds.Contains(e.GetPosition(Root)))
        {
            EndDrag();
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (IsTourSample(e.DataView))
        {
            bool onNotch = _drag is not null;
            EndDrag();
            if (onNotch)
            {
                TourDid(TourWait.Drop);
            }

            return;
        }

        if (_drag is not { } target)
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            await target.Target.DropAsync(e.DataView);
        }
        finally
        {
            deferral.Complete();
            EndDrag();
        }
    }

    private DropTarget? FindDropTarget(DataPackageView data)
    {
        if (data.Properties.ContainsKey(NotchContext.InternalDragProperty))
        {
            return null;
        }

        // The module shown first (a course dropped on the Révisions tab), then the first that takes it (files: the Drop Zone).
        if (_dropTargets.FirstOrDefault(target => target.Module == _selected && target.Target.Accepts(data)) is { } shown)
        {
            return shown;
        }

        foreach (var target in _dropTargets)
        {
            if (target.Target.Accepts(data))
            {
                return target;
            }
        }

        return null;
    }

    private void EndDrag()
    {
        _drag?.Target.OnDragHover(false);
        _drag = null;
        _dragHold?.Dispose();
        _dragHold = null;
    }

    // ── Keyboard ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The window lost the keyboard to another app (the user clicked elsewhere): whoever was typing is told, and the
    /// notch is free to close again.
    /// </summary>
    public void OnKeyboardLost()
    {
        var lease = _keyboard;
        _keyboard = null;
        lease?.Lose();
    }

    /// <summary>One module types at a time: a new request ends the previous one.</summary>
    private IDisposable TakeKeyboard(Action lost)
    {
        _keyboard?.Lose();
        _keyboard = new KeyboardLease(this, _controller.Hold(), lost);
        KeyboardRequested?.Invoke(this, EventArgs.Empty);
        return _keyboard;
    }

    /// <summary>The notch has the keyboard (and stays open) until the lease ends.</summary>
    private sealed class KeyboardLease(NotchView owner, IDisposable hold, Action lost) : IKeyboardLease
    {
        private bool _ended;

        /// <summary>Done typing: the keyboard goes back to the app that had it.</summary>
        public void Dispose()
        {
            if (End() && owner._keyboard == this)
            {
                owner._keyboard = null;
                owner.KeyboardReleased?.Invoke(owner, EventArgs.Empty);
            }
        }

        /// <summary>Done, and something was just opened: the keyboard is left to it, not handed back.</summary>
        public void HandOff()
        {
            if (End() && owner._keyboard == this)
            {
                owner._keyboard = null;
            }
        }

        /// <summary>The keyboard went elsewhere: nothing to hand back, the module is told.</summary>
        public void Lose()
        {
            if (End())
            {
                lost();
            }
        }

        private bool End()
        {
            if (_ended)
            {
                return false;
            }

            _ended = true;
            hold.Dispose();
            return true;
        }
    }

    // ── Announcements and live activity ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The status rides the right end of the pill, on the compositor: it slides in with <c>n.Toast</c>, follows the
    /// pill as it morphs (hover swell included), and never shows in the open notch. A live activity rings its glyph
    /// with its progress (an ellipse trimmed to the fraction done).
    /// </summary>
    private void BindToast()
    {
        Toast.Height = _layout.Collapsed.Height;
        Toast.Margin = new Thickness(0, _layout.TopInset, 0, 0);
        ElementCompositionPreview.SetIsTranslationEnabled(Toast, true);
        var toast = ElementCompositionPreview.GetElementVisual(Toast);
        _motion.Bind("Clamp(n.Toast, 0, 1) * Clamp(1 - n.Expansion * 3, 0, 1) * (1 - Clamp(n.Card, 0, 1)) * (1 - Clamp(n.Mini, 0, 1)) * (1 - Clamp(n.Gauge, 0, 1))")
            .Start(toast, "Opacity");
        _motion.Bind("Vector3((n.HostWidth + n.Width) * 0.5 - o + n.Mascot * ms + (1 - n.Toast) * 10, 0, 0)")
            .With("o", ToastOverlap).With("ms", MascotToastShift)
            .Start(toast, "Translation");
        _motion.Bind("Vector3(Lerp(0.88, 1, n.Toast), Lerp(0.88, 1, n.Toast), 1)").Start(toast, "Scale");
        Toast.SizeChanged += (_, e) => toast.CenterPoint = new Vector3(0, (float)e.NewSize.Height / 2, 0);

        var compositor = _motion.Compositor;
        var state = compositor.CreatePropertySet();
        _ringProgress = new SpringChannel(state, "P", 0);
        float size = (float)ToastBadge.Width;
        var ring = compositor.CreateShapeVisual();
        ring.Size = new Vector2(size);
        // A composition ellipse's path starts at twelve o'clock and runs clockwise, as a progress ring does: no turning.
        ring.Shapes.Add(RingShape(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), out _));
        var progress = RingShape(Microsoft.UI.Colors.White, out var geometry);
        _ringBrush = (CompositionColorBrush)progress.StrokeBrush;
        new CompositionExpression(compositor, "Clamp(r.P, 0.001, 1)").With("r", state).Start(geometry, "TrimEnd");
        ring.Shapes.Add(progress);
        ElementCompositionPreview.SetElementChildVisual(ToastRing, ring);

        CompositionSpriteShape RingShape(Color color, out CompositionEllipseGeometry ellipse)
        {
            ellipse = compositor.CreateEllipseGeometry();
            ellipse.Center = new Vector2(size / 2);
            ellipse.Radius = new Vector2(size / 2 - 1.5f);
            var shape = compositor.CreateSpriteShape(ellipse);
            shape.StrokeBrush = compositor.CreateColorBrush(color);
            shape.StrokeThickness = 2;
            shape.StrokeStartCap = CompositionStrokeCap.Round;
            shape.StrokeEndCap = CompositionStrokeCap.Round;
            return shape;
        }
    }

    /// <summary>
    /// A module acknowledges something (a copy…): the closed pill grows to the right just enough for the message,
    /// shows it for a moment, then settles back (to the live activity, if one is running).
    /// </summary>
    private void Announce(NotchActivity announcement)
    {
        // Not while open, nor over a presented card (it says more).
        if (_phase == NotchPhase.Expanded || _presented is not null)
        {
            return;
        }

        // An update of the same thing (a level ringed again, the same message) replaces it at once, quietly. Anything
        // else does not replace a fresh announcement (copying a path, then the clipboard module noticing that copy):
        // the first one says it best.
        bool update = _announcement is { } current
            && (current.Glyph == announcement.Glyph || (current.Progress is not null && announcement.Progress is not null));
        if (!update && _announcement is not null && Stopwatch.GetElapsedTime(_announcedAt) < AnnouncementShield)
        {
            return;
        }

        _announcement = announcement;
        _announcedAt = Stopwatch.GetTimestamp();
        _toastTimer.Stop();
        _toastTimer.Start();
        ShowStatus(pop: !update);
        if (!update && announcement.Progress is null)
        {
            Buddy.React(NotchEvent.Announcement);
        }
    }

    /// <summary>What a module has for the closed notch changed (a track playing, paused, gone…).</summary>
    private void SetPresence(int module, NotchPresence presence)
    {
        if (_modules.Count <= module || _modules[module].Presence == presence)
        {
            return;
        }

        // Something new to show (a track, even paused): the pill comes back for it, then the delay runs again. (Back from
        // elsewhere to nothing is not something to show.)
        if (presence > _modules[module].Presence && presence > NotchPresence.Nothing)
        {
            _idleTimer.Stop();
            SetIdle(false);
        }

        _modules[module].Presence = presence;
        UpdateIdle();
    }

    /// <summary>
    /// Something going on (a module's, or a live activity): the pill is in sight. Something shown but idle: it shrinks to
    /// its handle once the settings' delay has passed (one timer, armed once: nothing runs meanwhile). Nothing to show
    /// at all (an empty pill): the handle at once. What it would show being elsewhere (the player on the taskbar), it
    /// rests at once, whatever the settings.
    /// </summary>
    private void UpdateIdle()
    {
        bool active = _activity is not null || _modules.Any(slot => slot.Presence == NotchPresence.Active);
        if (_settings.Minimized)
        {
            // Minimized: its handle, whatever goes on.
            _idleTimer.Stop();
            SetIdle(true);
        }
        else if ((!_settings.HideWhenIdle && !RestsElsewhere) || active)
        {
            _idleTimer.Stop();
            SetIdle(false);
        }
        else if (_modules.All(slot => slot.Presence <= NotchPresence.Nothing))
        {
            _idleTimer.Stop();
            SetIdle(true);
        }
        else if (!_idle && !_idleTimer.IsRunning)
        {
            _idleTimer.Interval = TimeSpan.FromSeconds(_settings.IdleSeconds);
            _idleTimer.Start();
        }
    }

    /// <summary>Nothing to show, a module's being elsewhere (see <see cref="NotchPresence.Elsewhere"/>): it rests.</summary>
    private bool RestsElsewhere =>
        _modules.Any(slot => slot.Presence == NotchPresence.Elsewhere) && _modules.All(slot => slot.Presence <= NotchPresence.Nothing);

    /// <summary>
    /// Down to its handle (which opens it, hovered) or back to the pill, with the notch's own entrance.
    /// Announcements, cards and questions still come out of hiding for their time.
    /// </summary>
    private void SetIdle(bool idle)
    {
        if (idle == _idle)
        {
            return;
        }

        _idle = idle;
        if (_phase != NotchPhase.Expanded)
        {
            MorphTo(_phase, idle ? Motion.Collapse : Motion.Arrive);
        }
    }

    /// <summary>
    /// A module starts, updates or ends its live activity. Each module has its own; the one begun last shows (an update
    /// keeps its place), and when it ends the one before comes back.
    /// </summary>
    private void SetActivity(int module, NotchActivity? activity)
    {
        int at = _activities.FindIndex(each => each.Module == module);
        if (activity is null)
        {
            if (at >= 0)
            {
                _activities.RemoveAt(at);
            }
        }
        else if (at >= 0)
        {
            _activities[at] = (module, activity);
        }
        else
        {
            _activities.Add((module, activity));
        }

        _activity = _activities.Count > 0 ? _activities[^1].Activity : null;
        UpdateIdle();
        if (_announcement is null)
        {
            ShowStatus(pop: false);
        }
    }

    private void EndAnnouncement()
    {
        _toastTimer.Stop();
        if (_announcement is not null)
        {
            _announcement = null;
            ShowStatus(pop: false);
        }
    }

    /// <summary>
    /// Shows the announcement, or else the live activity, or nothing. The pill is reshaped only when the status
    /// appears, goes, or its width really changes: a progress update ("45 %" → "46 %") never makes it twitch.
    /// </summary>
    private void ShowStatus(bool pop)
    {
        var status = _announcement ?? _activity;
        bool shown = status is not null;
        double extra = _toastExtra;
        if (status is not null)
        {
            var brush = status.Tint is { } tint
                ? new SolidColorBrush(tint)
                : (Brush)Application.Current.Resources["NotchPrimaryTextBrush"];
            ToastGlyph.Glyph = status.Glyph;
            ToastGlyph.Foreground = brush;
            ToastText.Text = status.Text;

            // A ringed glyph is smaller, inside its ring.
            bool ringed = status.Progress is not null;
            ToastRing.Visibility = ringed ? Visibility.Visible : Visibility.Collapsed;

            // With the mascot, it is the one reacting: the glyph stays only to carry a progress ring.
            ToastBadge.Visibility = MascotOn && !ringed ? Visibility.Collapsed : Visibility.Visible;
            double shift = MascotOn ? MascotToastShift : 0;
            ToastGlyph.FontSize = ringed ? 8 : 13;
            if (status.Progress is { } progress)
            {
                _ringBrush!.Color = status.Tint ?? Microsoft.UI.Colors.White;
                _ringProgress!.AnimateTo(Math.Clamp(progress, 0, 1), Motion.Fade);
            }

            // Measured from the text (never while the pill morphs), within the window's room.
            double pill = _layout.ShapeFor(NotchPhase.Primed, _compactWidth).Width;
            double room = (_layout.CanvasWidth - pill) / 2 - _layout.ShoulderExtent - _layout.OvershootPadding;
            ToastText.MaxWidth = Math.Max(room + ToastOverlap - shift - ToastEndPadding - 24, 40);
            Toast.Measure(new Size(double.PositiveInfinity, _layout.Collapsed.Height));
            extra = Math.Clamp(Toast.DesiredSize.Width + ToastEndPadding - ToastOverlap + shift, ToastMinExtra, room);
        }

        bool appeared = shown != _toastShown;
        bool resized = shown && Math.Abs(extra - _toastExtra) > 3;
        _toastShown = shown;
        _toastExtra = extra;
        if (appeared)
        {
            _motion.Toast.AnimateTo(shown ? 1 : 0, shown ? Motion.Toggle : Motion.Fade);
        }
        else if (pop && shown)
        {
            // Something new replaces what was there: a little dip and back.
            _motion.Toast.Kick(-3, Motion.Toggle);
        }

        if ((appeared || resized) && _phase != NotchPhase.Expanded)
        {
            MorphTo(_phase, _phase == NotchPhase.Primed ? Motion.Prime : Motion.Settle);
        }

        if (shown && _announcement is null)
        {
            TraceToast();
        }
    }

    private long _tracedAt;
    private DispatcherQueueTimer? _traceTimer;

    /// <summary>
    /// Diagnostic, to remove once understood (HANDOFF: the live activity's text unseen on the closed pill while music
    /// plays): a second after a live activity shows or changes, how its text was laid out and whether it drew any ink,
    /// in the error log. At most every 20 s; nothing at rest.
    /// </summary>
    private void TraceToast()
    {
        if (_tracedAt != 0 && Stopwatch.GetElapsedTime(_tracedAt) < TimeSpan.FromSeconds(20))
        {
            return;
        }

        _tracedAt = Stopwatch.GetTimestamp();
        if (_traceTimer is null)
        {
            _traceTimer = DispatcherQueue.CreateTimer();
            _traceTimer.Interval = TimeSpan.FromSeconds(1);
            _traceTimer.IsRepeating = false;
            _traceTimer.Tick += async (_, _) =>
            {
                if (!_toastShown || _announcement is not null)
                {
                    return;
                }

                int ink = -1;
                try
                {
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                    await bitmap.RenderAsync(ToastText);
                    byte[] pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(await bitmap.GetPixelsAsync());
                    ink = 0;
                    for (int i = 3; i < pixels.Length; i += 4)
                    {
                        ink += pixels[i] > 0x20 ? 1 : 0;
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
                {
                    // Not drawable now: -1.
                }

                var at = ToastText.TransformToVisual(Root).TransformPoint(default);
                string color = ToastText.Foreground is SolidColorBrush brush ? $"#{brush.Color.A:X2}{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}" : "?";
                ErrorLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[Toast] '{ToastText.Text}' text {ToastText.ActualWidth:0.#}x{ToastText.ActualHeight:0.#} (wants {ToastText.DesiredSize.Width:0.#}, max {ToastText.MaxWidth:0.#}, at {at.X:0.#},{at.Y:0.#}, {color}, ink {ink}), toast {Toast.ActualWidth:0.#}x{Toast.ActualHeight:0.#}, badge {ToastBadge.Visibility}, extra {_toastExtra:0.#}, compact {_compactWidth:0.#}, phase {_phase}, idle {_idle}, mascot {MascotOn}, card {_presented is not null}, gauge {_gaugeShown}, presence {string.Join(",", _modules.Select(slot => slot.Presence))}"));
            };
        }

        _traceTimer.Stop();
        _traceTimer.Start();
    }

    // ── Presented cards ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A module presents a card for a moment: the closed pill widens if needed and grows down to hold it, under its
    /// own top row (whatever a module shows there stays). Not while the notch is open: nothing is interrupted.
    /// </summary>
    private bool Present(FrameworkElement card, Size size, TimeSpan duration, bool withMascot)
    {
        if (_phase == NotchPhase.Expanded)
        {
            return false;
        }

        ShowCard(card, size, question: false);
        MorphTo(_phase, Motion.Arrive); // The lively drop of the notch's own entrance.
        _presentTimer!.Stop();
        _presentTimer.Interval = duration;
        _presentTimer.Start();
        if (!withMascot || !MascotOn)
        {
            Buddy.React(NotchEvent.Card);
        }
        else
        {
            // Once there, it says it.
            _heroTimer ??= DispatcherQueue.CreateTimer();
            _heroTimer.IsRepeating = false;
            _heroTimer.Interval = TimeSpan.FromMilliseconds(380);
            _heroTimer.Tick -= OnHeroArrived;
            _heroTimer.Tick += OnHeroArrived;
            _heroTimer.Start();

            // Out of its place, into the card's left, large: its top left there, centred in its room.
            double size2 = HeroSize;
            _motion.Properties.InsertScalar("HeroX", (float)((_layout.CanvasWidth - size.Width) / 2 + (NotchContext.HeroRoom - size2) / 2));
            _motion.Properties.InsertScalar("HeroY", (float)(_layout.TopInset + _layout.Collapsed.Height + (size.Height - size2) / 2));
            _motion.Properties.InsertScalar("HeroScale", (float)(size2 / MascotOpenSize));
            _motion.Hero.AnimateTo(1, Motion.Arrive.Width);
        }

        return true;
    }

    /// <summary>The mascot's size as a card's hero.</summary>
    private const double HeroSize = 60;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _heroTimer;

    private void OnHeroArrived(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => Buddy.Exclaim();

    /// <summary>
    /// A module asks something (an incoming file…): a card like <see cref="Present"/>'s, but one that takes clicks
    /// and stays until answered. It steps aside while the notch is open or another card shows, and comes back after.
    /// </summary>
    private IDisposable Ask(FrameworkElement card, Size size)
    {
        var question = new Question(this, card, size);
        _question = question;
        if (_phase != NotchPhase.Expanded && (_presented is null || _questionShown))
        {
            ShowCard(card, size, question: true);
            MorphTo(_phase, Motion.Arrive);
        }

        Buddy.React(NotchEvent.Question);
        return question;
    }

    // ── Bubble ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A module hangs a bubble under the open notch (the note sheet…): it falls from the notch like a drop and rests
    /// a little below it, <paramref name="content"/> inside. With a <paramref name="minimum"/> size, its bottom
    /// corners can be pulled to resize it, between that and <paramref name="maximum"/> (all the room under the notch,
    /// by default). One at a time: a new one replaces it.
    /// </summary>
    private NotchBubble ShowBubble(FrameworkElement content, Size size, Size? minimum, Size? maximum)
    {
        if (!_showingTourCard && _tour is { Away: false })
        {
            EndTour(); // A module needs the bubble: the user is trying things out, the tour is over.
        }

        double restTop = BubbleTop();
        var room = new Size(_layout.ExpandedMax.Width, _layout.TopInset + _layout.ExpandedMax.Height - restTop);
        var largest = maximum is { } max ? new Size(Math.Min(max.Width, room.Width), Math.Min(max.Height, room.Height)) : room;
        var bubble = new NotchBubble(Fit(size, minimum, largest), minimum, largest, EndBubble, ResizeBubble);
        _bubble = bubble;

        BubbleContent.Child = content;
        LeftGrip.Visibility = RightGrip.Visibility = minimum is null ? Visibility.Collapsed : Visibility.Visible;
        BubbleLayer.IsHitTestVisible = true;

        // The content rides with the drop (above its rest while it falls, and with the notch as it reshapes) and shows
        // once it has let go.
        PlaceBubble(bubble.Size);
        var host = ElementCompositionPreview.GetElementVisual(BubbleHost);
        ElementCompositionPreview.SetIsTranslationEnabled(BubbleHost, true);
        _motion.Bind("Vector3(0, n.Inset + n.Height + n.Gap * n.Bubble - (1 - Clamp(n.Bubble, 0, 1)) * 14 - n.BubbleRest, 0)")
            .Start(host, "Translation");
        _motion.Bind("Clamp((n.Bubble - 0.55) * 2.5, 0, 1)").Start(host, "Opacity");

        Settle(_motion.Bubble.AnimateTo(1, Motion.Drip));
        return bubble;
    }

    /// <summary>Where a bubble's top rests: <see cref="NotchLayout.BubbleGap"/> under the open notch.</summary>
    private double BubbleTop() => _layout.TopInset + OpenNow.Height + _layout.BubbleGap;

    /// <summary>
    /// The bubble (and what it holds) at <paramref name="size"/>, centred under the notch: at once, or springing to it
    /// (<paramref name="animate"/>; what it holds is laid out at the new size right away, the drop reveals or covers it).
    /// </summary>
    /// <returns>How long the bubble takes to get there.</returns>
    private TimeSpan PlaceBubble(Size size, bool animate = false)
    {
        MoveBubble(size);
        BubbleHost.Width = size.Width;
        BubbleHost.Height = size.Height;
        if (!animate)
        {
            _motion.BubbleWidth.Snap(size.Width);
            _motion.BubbleHeight.Snap(size.Height);
            return TimeSpan.Zero;
        }

        var width = _motion.BubbleWidth.AnimateTo(size.Width, Motion.Resize);
        var height = _motion.BubbleHeight.AnimateTo(size.Height, Motion.Resize);
        return width > height ? width : height;
    }

    /// <summary>
    /// The bubble (<paramref name="size"/>) rests under the notch as it is now: its mouse area, and where its content is laid
    /// out (its content rides with <c>n.BubbleRest</c>, so it does not jump). Its size, springing or not, is left alone.
    /// </summary>
    private void MoveBubble(Size size)
    {
        double top = BubbleTop();
        _bubbleBounds = new Rect((_layout.CanvasWidth - size.Width) / 2, top, size.Width, size.Height);
        BubbleHost.Margin = new Thickness(_bubbleBounds.X, top, 0, 0);
        _motion.Properties.InsertScalar("BubbleRest", (float)top);
    }

    /// <summary>A module resizes its bubble (the search's results, as they come and go): it springs to the new size.</summary>
    private void ResizeBubble(NotchBubble bubble, Size size)
    {
        var fitted = Fit(size, bubble.Minimum, bubble.Maximum);
        if (_bubble != bubble || _resize is not null || fitted == bubble.Size)
        {
            return;
        }

        bubble.Size = fitted;
        Settle(PlaceBubble(fitted, animate: true));
    }

    private static Size Fit(Size size, Size? minimum, Size maximum)
    {
        var least = minimum ?? new Size(0, 0);
        return new Size(
            Math.Round(Math.Clamp(size.Width, Math.Min(least.Width, maximum.Width), maximum.Width)),
            Math.Round(Math.Clamp(size.Height, Math.Min(least.Height, maximum.Height), maximum.Height)));
    }

    private void EndBubble(NotchBubble bubble)
    {
        if (_bubble == bubble)
        {
            DrawBubbleBack();
        }
    }

    /// <summary>The bubble goes back into the notch (its content stays until the next one).</summary>
    private void DrawBubbleBack()
    {
        if (_bubble is null)
        {
            return;
        }

        EndResize();
        _bubble = null;
        BubbleLayer.IsHitTestVisible = false;
        Settle(_motion.Bubble.AnimateTo(0, Motion.Absorb));
    }

    /// <summary>The mouse areas follow the bubble: at once when it comes, once it is back in when it goes.</summary>
    private void Settle(TimeSpan motion)
    {
        _bubbleMotion = motion;
        MorphTo(_phase, _phase == NotchPhase.Expanded ? Motion.Expand : Motion.Collapse);
        _bubbleMotion = TimeSpan.Zero;
    }

    /// <summary>
    /// A bottom corner is grabbed: the bubble follows the pointer, growing or shrinking on both sides at once (it
    /// stays centred) and downward. The notch is held open meanwhile, and the window takes all the room the bubble may
    /// need, once, rather than at each move.
    /// </summary>
    private void OnGripPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_bubble is not { Minimum: not null } bubble || sender is not ResizeGrip grip)
        {
            return;
        }

        e.Handled = true;
        grip.CapturePointer(e.Pointer);
        grip.IsActive = true;
        _resize = new Resize(grip, e.GetCurrentPoint(Root).Position, bubble.Size, _controller.Hold());
        var room = new Rect((_layout.CanvasWidth - bubble.Maximum.Width) / 2 - 8, BubbleTop(), bubble.Maximum.Width + 16, bubble.Maximum.Height + 12);
        var bounds = InputBounds;
        bounds.Union(room);
        PublishInputBounds(bounds);
    }

    private void OnGripMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resize is not { } resize || _bubble is not { } bubble)
        {
            return;
        }

        e.Handled = true;
        var point = e.GetCurrentPoint(Root).Position;
        double side = resize.Grip.IsLeft ? -1 : 1;
        // Pulled far past its smallest, the corner would ask for a negative size: none below zero, Fit does the rest.
        var size = Fit(
            new Size(
                Math.Max(0, resize.From.Width + 2 * side * (point.X - resize.Start.X)),
                Math.Max(0, resize.From.Height + point.Y - resize.Start.Y)),
            bubble.Minimum,
            bubble.Maximum);
        if (size != bubble.Size)
        {
            bubble.Size = size;
            PlaceBubble(size);
            bubble.RaiseResized();
        }
    }

    private void OnGripReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resize is not null)
        {
            e.Handled = true;
            EndResize();
        }
    }

    /// <summary>Let go: the notch may close again, the mouse areas fit the bubble, the module is told its new size.</summary>
    private void EndResize()
    {
        if (_resize is not { } resize)
        {
            return;
        }

        _resize = null;
        resize.Grip.IsActive = false;
        resize.Grip.Light(false);
        resize.Grip.ReleasePointerCaptures();
        resize.Hold.Dispose();
        Settle(TimeSpan.Zero);
        _bubble?.RaiseResized();
    }

    /// <summary>A corner being pulled: from where, and the bubble's size then.</summary>
    private sealed record Resize(ResizeGrip Grip, Point Start, Size From, IDisposable Hold);

    private void EndQuestion(Question question)
    {
        if (_question != question)
        {
            return;
        }

        _question = null;
        if (_questionShown)
        {
            EndPresentation(reshape: true);
        }
    }

    private void ShowCard(FrameworkElement card, Size size, bool question)
    {
        EndAnnouncement(); // The card says it all.
        PresentationHost.Child = card;
        PresentationHost.Width = size.Width;
        PresentationHost.Height = size.Height;
        PresentationHost.Margin = new Thickness(0, _layout.TopInset + _layout.Collapsed.Height, 0, 0);
        PresentationHost.IsHitTestVisible = question;
        _presented = size;
        _questionShown = question;
        _motion.Card.AnimateTo(1, Motion.Toggle);
    }

    /// <param name="resume">Whether a question waiting behind a card shows again (not when the notch opens or goes).</param>
    private void EndPresentation(bool reshape, bool resume = true)
    {
        _presentTimer?.Stop();
        if (_presented is null)
        {
            return;
        }

        bool wasQuestion = _questionShown;
        _presented = null;
        _questionShown = false;
        PresentationHost.IsHitTestVisible = false;
        if (resume && !wasQuestion && _question is { } waiting && _phase != NotchPhase.Expanded)
        {
            ShowCard(waiting.Card, waiting.Size, question: true);
            if (reshape)
            {
                MorphTo(_phase, Motion.Arrive);
            }

            return;
        }

        _motion.Card.AnimateTo(0, Motion.Fade);
        _motion.Hero.AnimateTo(0, Motion.Collapse.Width); // Back to its place, as the card goes.
        _heroTimer?.Stop();
        if (reshape && _phase != NotchPhase.Expanded)
        {
            MorphTo(_phase, Motion.Collapse);
        }
    }

    /// <summary>The pill as it is, widened to the card if need be, and as tall as its row and the card together.</summary>
    private NotchShape PresentedShape(NotchPhase phase, Size card)
    {
        var pill = _layout.ShapeFor(phase, _compactWidth);
        return new NotchShape(
            Math.Max(pill.Width, card.Width + 2 * CardPadding),
            _layout.Collapsed.Height + card.Height + CardBottom,
            CardRadius);
    }

    /// <summary>The card takes the mouse too (hovering it opens the notch), with room for the spring's overshoot.</summary>
    private Rect PresentedInputBounds(NotchShape shape)
    {
        var bounds = _layout.BoundsOf(shape);
        double top = _layout.IsFlush ? 0 : Math.Max(0, bounds.Y - 8);
        return new Rect(bounds.X - 8, top, bounds.Width + 16, bounds.Bottom + 12 - top);
    }

    // ── Morph ───────────────────────────────────────────────────────────────────────────────────────────────

    private void OnPhaseChanged(object? sender, EventArgs e)
    {
        if (_controller.Phase == NotchPhase.Expanded)
        {
            // Opening: the announcement has done its job (a live activity simply waits for the pill to come back).
            EndAnnouncement();
            EndPresentation(reshape: false, resume: false);

            // Hovered open after a module without a tab (the search) was shown: back on the last tab.
            if (!_showingModule && _selected < _modules.Count && !_modules[_selected].Module.HasTab)
            {
                SelectTab(_lastTab, animate: false);
            }

            // Opened by a click on a question: from now on the pointer counts as inside.
            if (_pointerOnQuestion)
            {
                _pointerOnQuestion = false;
                _controller.OnPointerEntered();
            }
        }
        else
        {
            // Closing: a bubble goes back in with the notch (the tour's card too: the tour is over, unless it stepped
            // aside for what just closed: it comes back).
            if (_tour is { Away: true } away)
            {
                DispatcherQueue.TryEnqueue(() => ResumeTour(away));
            }
            else
            {
                EndTour(drawBack: false);
            }

            if (_bubble is not null)
            {
                _bubble = null;
                BubbleLayer.IsHitTestVisible = false;
                _bubbleMotion = _motion.Bubble.AnimateTo(0, Motion.Absorb);
            }

            // Closing again: the question still waiting comes back with the pill.
            if (_presented is null && _question is { } waiting)
            {
                ShowCard(waiting.Card, waiting.Size, question: true);
            }
        }

        var springs = _controller.Phase switch
        {
            NotchPhase.Expanded => Motion.Expand,
            NotchPhase.Primed => Motion.Prime,
            _ when _phase == NotchPhase.Primed => Motion.Settle,
            _ => Motion.Collapse,
        };
        MorphTo(_controller.Phase, springs);
        _bubbleMotion = TimeSpan.Zero;
    }

    private void OnPinnedChanged(object? sender, EventArgs e)
    {
        bool pinned = _controller.IsPinned;
        _motion.Pin.AnimateTo(pinned ? 1 : 0, Motion.Toggle);
        if (pinned)
        {
            // Tactile confirmation: the notch bounces as if it clicked into place.
            _motion.Width.Kick(260, Motion.Bump);
            _motion.Height.Kick(90, Motion.Bump);
        }
    }

    private void MorphTo(NotchPhase phase, MorphSprings springs)
    {
        _phase = phase;
        UpdateHitTesting();
        bool presenting = _presented is not null && phase != NotchPhase.Expanded;
        if (phase == NotchPhase.Expanded)
        {
            HideGauge(); // Open, the module shows the level itself.
        }

        // A level shown: the pill at its plain width (a module may have widened it, the sung line), the track a fair length.
        double compactWidth = _gaugeShown ? 0 : _compactWidth;

        // Closed: the pill, which goes back into its handle under the pointer (see SetPeeking). Anything else (opening,
        // a question) ends that. Nothing going on (see UpdateIdle): closed, the notch is only its handle (it swells under the pointer, then
        // opens); announcements, cards and questions still bring the pill out for their time.
        // With the mascot, rest is its nest rather than the handle (see NestShape): a small pill, which gives way under
        // the pointer like the pill does.
        bool quiet = _idle && phase != NotchPhase.Expanded && !presenting && !_gaugeShown;
        bool idle = quiet && !_toastShown;
        bool nested = idle && MascotOn && !_settings.Minimized; // Minimized: the handle, not the nest.

        // An announcement while nothing goes on (a copy…), with the mascot: it comes out of the nest, just the mascot and
        // its text, rather than the whole pill and its empty room.
        // So does a live activity going on alone (a Pomodoro, an upload: no music…): the mascot, its ring and its text.
        bool activityAlone = _activity is not null && (_settings.HideWhenIdle || RestsElsewhere) && !_settings.Minimized
            && phase != NotchPhase.Expanded && !presenting && !_gaugeShown && _modules.All(slot => slot.Presence <= NotchPresence.Nothing);
        bool announcedFromNest = (quiet || activityAlone) && _toastShown && MascotOn && !IsHandle && !_settings.Minimized;
        bool nest = (nested && !IsHandle) || announcedFromNest;
        bool handle = IsHandle || (idle && !nested);
        bool resting = phase == NotchPhase.Collapsed && !presenting && (!idle || nested);
        if (!resting)
        {
            ForgetPeek();
        }

        var shape = presenting ? PresentedShape(phase, _presented!.Value)
            : nest ? NestShape(phase)
            : _layout.ShapeFor(phase, compactWidth, OpenNow, handle);
        var duration = _motion.MorphTo(shape, phase == NotchPhase.Expanded ? 1 : 0, springs);
        _motion.Mini.AnimateTo(handle ? 1 : 0, Motion.Fade);
        _motion.Nest.AnimateTo(nest ? 1 : 0, Motion.Fade);
        UpdateMascot();
        UpdateQuick();

        // An announcement extends the closed pill to the right: it pops out with a lively spring (not over a card, nor
        // on the handle).
        double extra = _toastShown && phase != NotchPhase.Expanded && !presenting && !handle && !_gaugeShown ? _toastExtra : 0;
        var extension = _motion.Extra.AnimateTo(extra, extra > 0 ? Motion.Prime.Width : springs.Width);
        if (extension > duration)
        {
            duration = extension;
        }

        if (_bubbleMotion > duration)
        {
            duration = _bubbleMotion;
        }

        if (_bubbleMotion > TimeSpan.Zero)
        {
            _bubbleGoneAt = Stopwatch.GetTimestamp() + (long)(_bubbleMotion.TotalSeconds * Stopwatch.Frequency);
        }

        // Closed, only the handle's spot opens the notch (see NotchLayout.HandleReach). The rest of the pill takes the
        // mouse only to give way under it (see SetPeeking).
        _hoverBounds = resting || idle
            ? _layout.InputBoundsFor(NotchPhase.Collapsed, handle: true)
            : ExtendRight(_layout.BoundsOf(shape), extra);

        // The mascot's nest opens the notch too, hovered (the mascot is the way in): it does not give way under the
        // pointer, only an announcement's text beside it does.
        if (nest)
        {
            _hoverBounds.Union(_layout.BoundsOf(shape));
        }
        if (!resting)
        {
            _peekBounds = Rect.Empty;
        }
        else if (!_peeking)
        {
            _peekBounds = ExtendRight(_layout.BoundsOf(shape), extra);
        }

        // Grow the input region immediately so nothing gets clipped; shrink it only once the motion has settled.
        _settledInputBounds = presenting ? PresentedInputBounds(shape)
            : nest ? ExtendRight(_layout.InputBoundsOf(shape), extra) // An announcement out of the nest: its text too.
            : ExtendRight(_layout.InputBoundsFor(phase, compactWidth, OpenNow, handle), extra);

        // A bubble hanging under the notch takes the mouse too (its bounds include the gap: crossing it is not leaving).
        // The little notches beside the open one take the mouse too, each on its own: a rectangle around them and the
        // notch would take the empty corners beside it, keeping it open and their clicks. They come at once, and go once
        // settled.
        _sideHover = phase == NotchPhase.Expanded ? SideBounds(OpenNow.Width, 1) : [];
        if (_sideHover.Length > 0)
        {
            PublishSideBounds(_sideHover);
        }

        if (_bubble is not null && phase == NotchPhase.Expanded)
        {
            _hoverBounds.Union(_bubbleBounds);
            _settledInputBounds.Union(new Rect(_bubbleBounds.X - 8, _bubbleBounds.Y, _bubbleBounds.Width + 16, _bubbleBounds.Height + 12));
        }

        var transitional = _settledInputBounds;
        if (!InputBounds.IsEmpty)
        {
            transitional.Union(InputBounds);
        }

        PublishInputBounds(transitional);
        _settleTimer.Stop();
        _settleTimer.Interval = duration + SettleMargin;
        _settleTimer.Start();

        // Closing, or giving way under the pointer: the region shrinks with the notch, frame by frame, rather than at
        // the end (it would take the clicks meant for what the notch no longer covers).
        _shrinkCeiling = transitional;
        // Not while a bubble goes back in: it hangs below the notch, the region keeps room for it to the end.
        bool bubbleGoing = Stopwatch.GetTimestamp() < _bubbleGoneAt;
        Track(phase != NotchPhase.Expanded && !presenting && !bubbleGoing && transitional != _settledInputBounds);
    }

    /// <summary>Follows the notch's shrinking with the input region, one update per frame, only while it shrinks.</summary>
    private void Track(bool shrinking)
    {
        if (shrinking == _tracking)
        {
            return;
        }

        _tracking = shrinking;
        if (shrinking)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnShrinking;
        }
        else
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnShrinking;
        }
    }

    private void OnShrinking(object? sender, object e)
    {
        // Where the notch is now (from its springs, a little back to be sure), never less than where it is going, never
        // more than what the region already is.
        var live = _layout.InputBoundsOf(
            _motion.Width.ValueAgo(TrackLag), _motion.Height.ValueAgo(TrackLag), _motion.Expansion.ValueAgo(TrackLag));
        live = ExtendRight(Inflate(live, TrackMargin), _motion.Extra.ValueAgo(TrackLag));
        // The little notches slide back into the pill: their own parts of the region follow them.
        PublishSideBounds(SideBounds(_motion.Width.ValueAgo(TrackLag), Math.Clamp(_motion.Expansion.ValueAgo(TrackLag), 0, 1)));

        live.Union(_settledInputBounds);
        live.Intersect(_shrinkCeiling);
        if (!live.IsEmpty)
        {
            PublishInputBounds(Snapped(live));
        }
    }

    /// <summary>Whole pixels outward: a region redone at each frame for a fraction of a pixel would be for nothing.</summary>
    private static Rect Snapped(Rect rect)
    {
        double left = Math.Floor(rect.Left);
        double top = Math.Floor(rect.Top);
        return new Rect(left, top, Math.Ceiling(rect.Right) - left, Math.Ceiling(rect.Bottom) - top);
    }

    private void PublishInputBounds(Rect bounds)
    {
        if (bounds == InputBounds)
        {
            return;
        }

        InputBounds = bounds;
        InputBoundsChanged?.Invoke(this, bounds);
    }

    private void PublishSideBounds(Rect[] bounds)
    {
        if (bounds.SequenceEqual(SideInputBounds))
        {
            return;
        }

        SideInputBounds = bounds;
        InputBoundsChanged?.Invoke(this, InputBounds);
    }

    // ── Pointer ─────────────────────────────────────────────────────────────────────────────────────────────

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(Root).Position;
        var bounds = _pointerInside ? Inflate(_hoverBounds, HoverHysteresis) : _hoverBounds;
        bool inside = bounds.Contains(position) || _sideHover.Any(side => side.Contains(position));
        if (!inside && _peekBounds.Contains(position))
        {
            SetPeeking(true);
        }

        SetPointerInside(inside);
        if (Buddy.IsAttentive)
        {
            Buddy.LookAt(e.GetCurrentPoint(Buddy).Position);
        }
    }

    // While peeking, the window lets go of the mouse over the pill: leaving it is the window's to tell (OnPeekLeft).
    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetPointerInside(false);
        Buddy.LookAt(null);
    }

    /// <summary>
    /// Raised when the closed notch gives way under the pointer and lets the clicks through to what is behind it
    /// (in these bounds, logical pixels): the window must call <see cref="OnPeekLeft"/> once the pointer leaves.
    /// </summary>
    public event EventHandler<Rect>? PeekStarted;

    /// <summary>Raised when the pill is back (or the notch opens): the pointer no longer needs watching.</summary>
    public event EventHandler? PeekEnded;

    /// <summary>The pointer left where the closed notch's pill was: it drops back from the handle.</summary>
    public void OnPeekLeft() => SetPeeking(false);

    /// <summary>
    /// Raised when the open notch is engaged (the user acted in it: it stays open, see NotchController.Engage) or no
    /// longer is: the window watches for a click outside meanwhile, and calls <see cref="OnClickedOutside"/>.
    /// </summary>
    public event EventHandler? EngagedChanged;

    public bool IsEngaged => _controller.IsEngaged;

    /// <summary>A click outside the engaged notch (and its bubble, its menus): it closes.</summary>
    public void OnClickedOutside() => _controller.Disengage();


    /// <summary>
    /// The closed notch's pill, under the pointer, goes back into its handle (which stays, at the top),
    /// and lets the clicks through to what is behind it. It drops back once the pointer leaves where it was; reaching
    /// the handle's spot opens the notch instead.
    /// </summary>
    private void SetPeeking(bool peeking)
    {
        if (peeking == _peeking)
        {
            return;
        }

        if (peeking)
        {
            _peeking = true;
            PeekStarted?.Invoke(this, _peekBounds);
            MorphTo(_phase, Motion.Collapse);
        }
        else
        {
            ForgetPeek();
            MorphTo(_phase, Motion.Settle);
        }
    }

    /// <summary>Peeking ends, without a morph of its own (one is under way).</summary>
    private void ForgetPeek()
    {
        if (_peeking)
        {
            _peeking = false;
            PeekEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetPointerInside(bool inside)
    {
        if (inside == _pointerInside)
        {
            return;
        }

        _pointerInside = inside;

        // A question is read and answered where it is: hovering it does not open the notch (a click on it does).
        if (inside && _questionShown)
        {
            _pointerOnQuestion = true;
            return;
        }

        if (!inside && _pointerOnQuestion)
        {
            _pointerOnQuestion = false;
            return;
        }

        if (inside)
        {
            _controller.OnPointerEntered();
        }
        else
        {
            _controller.OnPointerExited();
        }
    }

    /// <summary>
    /// A wheel turn the content did not use goes to the module shown, or, over the closed pill, to the first module
    /// (the media player turns it into the volume).
    /// </summary>
    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // The closed pill gives way under the pointer: no volume under it.
        if (_phase != NotchPhase.Expanded)
        {
            return;
        }

        var wheel = e.GetCurrentPoint(Root).Properties;
        int index = _phase == NotchPhase.Expanded ? _selected : 0;
        if (!wheel.IsHorizontalMouseWheel && index < _modules.Count && _modules[index].Context.RaiseWheel(wheel.MouseWheelDelta))
        {
            e.Handled = true;
        }
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        // Only the handle's spot opens the closed notch: a click on its pill, as it gives way, does nothing. Opened by a
        // click, it is engaged at once (see OnPressed).
        if (!IsInsideButton(e.OriginalSource) && !_peeking)
        {
            _controller.OnTapped();
            if (_settings.StayOpenAfterClick)
            {
                _controller.Engage();
            }
        }
    }

    /// <summary>
    /// Any press in the open notch (a tab, a button, its bubble, the little notches beside it): it is engaged, and stays
    /// open when the pointer leaves, until a click outside (see the settings: « Rester ouverte après un clic »).
    /// </summary>
    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_settings.StayOpenAfterClick)
        {
            _controller.Engage();
        }
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Double-clicking "next" skips twice, it must not pin the notch; nor does a double click on the pill giving way.
        if (IsInsideButton(e.OriginalSource) || _peeking)
        {
            return;
        }

        e.Handled = true;
        _controller.OnDoubleTapped();
    }

    private void OnMenuOpening(object sender, object e)
    {
        TourDid(TourWait.Menu);
        PinMenuItem.IsChecked = _controller.IsPinned;
        MinimizedMenuItem.IsChecked = _settings.Minimized;
        _menuHold ??= _controller.Hold();

        // The menu needs focus for keyboard navigation and to light-dismiss when the user clicks elsewhere.
        ActivationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnMenuClosed(object sender, object e)
    {
        _menuHold?.Dispose();
        _menuHold = null;
    }

    private void OnPinMenuItemClick(object sender, RoutedEventArgs e) => _controller.SetPinned(PinMenuItem.IsChecked);

    private void OnMinimizedMenuItemClick(object sender, RoutedEventArgs e) => _settings.Minimized = MinimizedMenuItem.IsChecked;

    // ── Level ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The level on the closed pill (see ShowLevel), on the compositor: the glyph at the pill's left, a faint track, and
    /// its fill in the mascot's colour, up to the mascot riding its end (see BindMascot).
    /// </summary>
    private void BindGauge()
    {
        var compositor = _motion.Compositor;
        var layer = compositor.CreateShapeVisual();
        layer.Size = new Vector2((float)_layout.CanvasWidth, (float)_layout.CanvasHeight);
        const string Top = "(n.Inset + (ch - th) * 0.5)";

        var track = compositor.CreateRoundedRectangleGeometry();
        track.CornerRadius = new Vector2((float)GaugeTrackThickness / 2);
        Gauge($"Vector2({GaugeRight} - {GaugeLeft}, th)").Start(track, "Size");
        Gauge($"Vector2({GaugeLeft}, {Top})").Start(track, "Offset");
        var trackShape = compositor.CreateSpriteShape(track);
        trackShape.FillBrush = compositor.CreateColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        layer.Shapes.Add(trackShape);

        var fill = compositor.CreateRoundedRectangleGeometry();
        fill.CornerRadius = new Vector2((float)GaugeTrackThickness / 2);
        Gauge($"Vector2(Max({GaugeHead} - {GaugeLeft}, 0), th)").Start(fill, "Size");
        Gauge($"Vector2({GaugeLeft}, {Top})").Start(fill, "Offset");
        var fillShape = compositor.CreateSpriteShape(fill);
        _gaugeFill = compositor.CreateColorBrush(MascotPalette.Find(_settings.MascotColor).Tone);
        fillShape.FillBrush = _gaugeFill;
        layer.Shapes.Add(fillShape);
        ElementCompositionPreview.SetElementChildVisual(GaugeTrackHost, layer);

        // Shown on the closed pill only, and not while it gives way under the pointer.
        const string Shown = "Clamp(n.Gauge, 0, 1) * Clamp(1 - n.Expansion * 3, 0, 1) * (1 - Clamp(n.Mini, 0, 1))";
        _motion.Bind(Shown).Start(layer, "Opacity");
        ElementCompositionPreview.SetIsTranslationEnabled(GaugeGlyph, true);
        var glyph = ElementCompositionPreview.GetElementVisual(GaugeGlyph);
        _motion.Bind(Shown).Start(glyph, "Opacity");
        Gauge("Vector3((n.HostWidth - n.Width) * 0.5 + gl, n.Inset + (ch - 18) * 0.5, 0)").Start(glyph, "Translation");
    }

    private CompositionExpression Gauge(string expression) => _motion.Bind(expression)
        .With("tl", GaugeTrackLeft).With("tr", GaugeTrackRight).With("k", GaugeKnob).With("th", GaugeTrackThickness)
        .With("gl", GaugeGlyphLeft).With("ch", _layout.Collapsed.Height);

    /// <summary>
    /// A level changed (the volume, see NotchContext.ShowLevel): the closed pill widens for a moment to show it, the
    /// mascot sliding along its track as its cursor, glancing the way it went (eyes shut when muted). Without the mascot,
    /// the ringed announcement of old. Open, the module shows it itself.
    /// </summary>
    private void ShowLevel(string glyph, string text, double level, bool muted)
    {
        if (!MascotOn)
        {
            Announce(new NotchActivity(glyph, text, muted ? 0 : level, null));
            return;
        }

        if (_phase == NotchPhase.Expanded)
        {
            return;
        }

        level = Math.Clamp(muted ? 0 : level, 0, 1);
        GaugeGlyph.Glyph = glyph;
        int direction = _gaugeShown ? Math.Sign(level - _gaugeLast) : 0;
        _gaugeLast = level;
        if (!_gaugeShown)
        {
            _gaugeShown = true;
            _motion.GaugeLevel.Snap(level);
            _motion.Gauge.AnimateTo(1, Motion.Toggle);
            MorphTo(_phase, _phase == NotchPhase.Primed ? Motion.Prime : Motion.Settle);
        }
        else
        {
            _motion.GaugeLevel.AnimateTo(level, GaugeMove);
        }

        Buddy.ShowLevel(muted, level >= 0.995, direction);
        _gaugeTimer.Stop();
        _gaugeTimer.Start();
    }

    /// <summary>The level has stayed a moment: the pill goes back to what it showed.</summary>
    private void EndGauge()
    {
        if (_gaugeShown)
        {
            HideGauge();
            if (_phase != NotchPhase.Expanded)
            {
                MorphTo(_phase, Motion.Settle);
            }
        }
    }

    /// <summary>The level goes away, without a morph of its own (one is under way, or follows).</summary>
    private void HideGauge()
    {
        _gaugeTimer.Stop();
        if (_gaugeShown)
        {
            _gaugeShown = false;
            _motion.Gauge.AnimateTo(0, Motion.Fade);
            Buddy.EndLevel();
        }
    }

    // ── Mascot ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The mascot is there: the notch with tabs (not a search satellite), and the setting on.</summary>
    private bool MascotOn => _mascotHosted && _settings.ShowMascot;

    /// <summary>
    /// The mascot lives in the notch, whatever it shows: closed, at the pill's right end (where a visualizer would ride)
    /// or alone in its nest at rest; open, in its own little notch beside the open one (the pod, see NotchSurface),
    /// which comes out of the pill's right end with it. It is laid out at its open place, at its open size, and carried to
    /// the pill by the morph itself (scale and translation from n.Progress), so it glides between both and can never lag
    /// behind the shape.
    /// </summary>
    private void BindMascot()
    {
        _mascotHosted = _tabState is not null;
        if (!_mascotHosted)
        {
            Buddy.Visibility = Visibility.Collapsed;
            PodBackHost.Visibility = Visibility.Collapsed;
            _pod.Opacity = 0;
            return;
        }

        double left = (_layout.CanvasWidth + _layout.Expanded.Width) / 2 + _layout.PodGap + (_layout.PodSize - MascotOpenSize) / 2;
        double top = _layout.TopInset + (_layout.PodSize - MascotOpenSize) / 2;
        Buddy.Width = MascotOpenSize;
        Buddy.Height = MascotOpenSize;
        Buddy.Margin = new Thickness(left, top, 0, 0);

        Buddy.Wear(MascotPalette.Find(_settings.MascotColor), animate: false);
        ElementCompositionPreview.SetIsTranslationEnabled(Buddy, true);
        var visual = ElementCompositionPreview.GetElementVisual(Buddy);
        double inset = (_layout.Collapsed.Height - MascotClosedSize) / 2;
        _motion.Bind("Vector3(Lerp(cs, 1, n.Progress), Lerp(cs, 1, n.Progress), 1) * Lerp(1, 0.5, Clamp(n.Away, 0, 1)) * Lerp(1, n.HeroScale, Clamp(n.Hero, 0, 1))")
            .With("cs", MascotClosedSize / MascotOpenSize)
            .Start(visual, "Scale");
        // Closed, at the pill's right end; as the pill gives way to the handle (n.Mini), it slides to the handle's middle
        // and squashes flat into its grabber line (see MascotRig.Flatten). Open, it follows the pod, which keeps to the
        // open notch's right side (wider when a module widens it).
        // A level shown (n.Gauge): it rides the end of its fill, as its cursor (see BindGauge).
        const string Resting = "Lerp((n.HostWidth + n.Width) * 0.5 - i - s, " + GaugeHead + " - s * 0.5, Clamp(n.Gauge, 0, 1))";
        const string ClosedX = "Lerp(" + Resting + ", (n.HostWidth - s) * 0.5, Clamp(n.Mini, 0, 1))";
        const string ClosedY = "Lerp(n.Inset + i, n.Inset + (n.Height - s) * 0.5, Clamp(n.Mini, 0, 1))";
        // A card's hero (n.Hero): out of its place into the card (see Present).
        _motion.Bind($"Lerp(Vector3(Lerp({ClosedX} - x, (n.Width - ew) * 0.5, n.Progress), Lerp({ClosedY} - y, 0, n.Progress), 0), Vector3(n.HeroX - x, n.HeroY - y, 0), Clamp(n.Hero, 0, 1))")
            .With("i", inset).With("s", MascotClosedSize).With("x", left).With("y", top).With("ew", _layout.Expanded.Width)
            .With("tl", GaugeTrackLeft).With("tr", GaugeTrackRight).With("k", GaugeKnob)
            .Start(visual, "Translation");
        Buddy.Flatten(_motion.Properties, (float)(MascotClosedSize / MascotRig.Box));

        // Clicked, it opens the module that answers to it (the assistant).
        Buddy.Clicked += (_, _) =>
        {
            if (TourDid(TourWait.Mascot))
            {
                return; // The tour's step: it only says hello.
            }

            EndTour(); // Trying the assistant: the tour has done its job.
            foreach (var slot in _modules)
            {
                if (slot.Module.OpenFromMascot())
                {
                    break;
                }
            }
        };

        // Always there, beside the search bar too, and as the handle's line. Its pod shows with it, but in the handle.
        // Away while the module shown hosts it (the assistant): it fades, shrinking a little into its pod, which stays
        // (with or without the mascot) for the way back to the tabs (see BindPodBack).
        _motion.Bind("n.Mascot * (1 - Clamp(n.Away, 0, 1))").Start(visual, "Opacity");
        _motion.Bind("Max(n.Mascot * (1 - Clamp(n.Away, 0, 1)), Clamp(n.Away, 0, 1)) * (1 - Clamp(n.Mini, 0, 1))").Start(_pod, "Opacity");
        BindPodBack();
        UpdateMascot();
    }

    /// <summary>
    /// While the module shown hosts the mascot (the assistant: it is in the drop, large), its pod holds « Retour aux
    /// onglets »: centred in the pod as it is out (it follows the open notch's right side), showing once it is.
    /// </summary>
    private void BindPodBack()
    {
        const double Inset = 4;
        double size = _layout.PodSize - 2 * Inset;
        PodBack.Width = size;
        PodBack.Height = size;
        PodBackHost.Margin = new Thickness(0, _layout.TopInset + Inset, 0, 0);
        ElementCompositionPreview.SetIsTranslationEnabled(PodBackHost, true);
        var visual = ElementCompositionPreview.GetElementVisual(PodBackHost);
        _motion.Bind("Vector3((n.HostWidth + n.Width) * 0.5 + gap + i, 0, 0)")
            .With("gap", _layout.PodGap).With("i", Inset)
            .Start(visual, "Translation");
        _motion.Bind("Clamp(n.Away, 0, 1) * Clamp((n.Progress - 0.8) * 5, 0, 1)").Start(visual, "Opacity");
    }

    /// <summary>« Retour aux onglets » (the pod, while the assistant shows): back on the last tab, the notch staying open.</summary>
    private void OnPodBackClick(object sender, RoutedEventArgs e)
    {
        if (MascotAway)
        {
            SelectTab(_lastTab); // The assistant lets go (its drop, the keyboard): see AssistantView, on SelectionChanged.
        }
    }

    /// <summary>
    /// The mascot's pod beside the open notch (or the search bar) is there: the mascot is on; or the module shown hosts it
    /// (see <see cref="MascotAway"/>), the pod holding the way back to the tabs.
    /// </summary>
    private bool PodShown => MascotOn || (_mascotHosted && MascotAway);

    /// <summary>Open on a module that hosts the mascot itself (the assistant, see INotchModule.HostsMascot).</summary>
    private bool MascotAway => _phase == NotchPhase.Expanded && _selected < _modules.Count && _modules[_selected].Module.HostsMascot;

    /// <summary>The parts of the window's region beside the notch (<paramref name="progress"/> of the way out): its little notches.</summary>
    private Rect[] SideBounds(double width, double progress)
    {
        var sides = new List<Rect>(3);
        if (PodShown)
        {
            sides.Add(PodBounds(width, progress));
        }

        if (QuickShown)
        {
            sides.Add(QuickBounds(width, progress, below: false));
            if (_quick!.Brightness.IsAvailable)
            {
                sides.Add(QuickBounds(width, progress, below: true));
            }

            if (_mixerShowing && progress >= 0.8) // It fades out with the first fifth of the closing (see BindMixer).
            {
                sides.Add(MixerBounds(width));
            }
        }

        // The tour's ring round them: the region clips what is drawn, it must have room to show whole.
        return _tour is null ? [.. sides] : [.. sides.Select(side => Inflate(side, TourRoom))];
    }

    /// <summary>The quick controls are there: this notch has them, and it is on a tab (the search has none).</summary>
    private bool QuickShown => _quick is not null && _selected < _modules.Count && _modules[_selected].Module.HasTab;

    /// <summary>
    /// Where a quick control's pod is (fillets and rim included) left of a notch <paramref name="width"/> wide,
    /// <paramref name="progress"/> of the way out: hanging from the screen edge, or <paramref name="below"/> it (see
    /// NotchSurface's QuickLeft).
    /// </summary>
    private Rect QuickBounds(double width, double progress, bool below)
    {
        double closed = _layout.Collapsed.Height;
        double size = closed + (_layout.QuickSize - closed) * progress;
        double left = (_layout.CanvasWidth - width) / 2 - (_layout.PodGap + _layout.QuickSize) * progress;
        if (below)
        {
            double top = _layout.TopInset + (_layout.QuickSize + _layout.QuickGap) * progress;
            return new Rect(left - 2, top - 2, size + 4, size + 4);
        }

        double fillet = _layout.ShoulderExtent;
        return new Rect(left - fillet - 2, 0, size + 2 * fillet + 4, _layout.TopInset + size + 4);
    }

    // ── Quick controls ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The quick controls, left of the open notch, each in its little notch (see NotchSurface.QuickPods): the system
    /// volume above (a click mutes), the screen's brightness below (when the computer has a built-in screen). Laid out
    /// at their open place, carried into the pill's left end by the morph (as the mascot is to its right end).
    /// </summary>
    private void BindQuick()
    {
        if (_quick is null || _tabState is null || _quickPods is null)
        {
            VolumeBubble.Visibility = Visibility.Collapsed;
            BrightnessBubble.Visibility = Visibility.Collapsed;
            return;
        }

        double left = (_layout.CanvasWidth - _layout.Expanded.Width) / 2 - _layout.PodGap - _layout.QuickSize;
        double lower = _layout.QuickSize + _layout.QuickGap;
        Place(VolumeBubble, 0);
        Place(BrightnessBubble, lower);

        // Not beside a bar (the search, the assistant), not even half way as it opens, and not while the pill gives way
        // to the handle. Closed, they are inside the pill: hidden or not, nothing shows.
        _motion.Bind("(1 - Clamp(n.Mini, 0, 1)) * Clamp(b.Shown, 0, 1)").With("b", _tabState).Start(_quickPods, "Opacity");
        if (!_quick.Brightness.IsAvailable)
        {
            BrightnessBubble.Visibility = Visibility.Collapsed;
            _quickPods.Children.Last().Opacity = 0;
        }

        VolumeBubble.Adjusted += (_, delta) =>
        {
            ShowVolume(_quick.Audio.SetLevel(Math.Clamp(_volumeLevel + delta, 0, 1)));
            TourDid(TourWait.Quick);
        };
        VolumeBubble.Clicked += (_, _) =>
        {
            ShowVolume(_quick.Audio.ToggleMute());
            TourDid(TourWait.Quick);
        };
        BrightnessBubble.Adjusted += (_, delta) =>
        {
            TourDid(TourWait.Quick);
            _brightnessLevel = Math.Clamp(_brightnessLevel + delta, 0, 1);
            _quick.Brightness.Set(_brightnessLevel);
            BrightnessBubble.Show(_brightnessLevel, BrightnessGlyph);
        };
        _quick.Volume.Changed += (_, volume) =>
        {
            // While the bubble is held, Windows only echoes the finger, a beat late: it would pull the ring back.
            if (!VolumeBubble.IsHeld)
            {
                ShowVolume(volume);
            }
        };
        BindMixer(left);
        TintQuick(animate: false);
        UpdateQuick();

        void Place(QuickBubble bubble, double below)
        {
            bubble.Margin = new Thickness(left, _layout.TopInset + below, 0, 0);

            // Dragged like a slider, the pointer may leave the notch: it stays open until the bubble is let go.
            bubble.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _quickHold ??= _controller.Hold()), handledEventsToo: true);
            bubble.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => ReleaseQuickHold()), handledEventsToo: true);
            bubble.PointerCaptureLost += (_, _) => ReleaseQuickHold();
            ElementCompositionPreview.SetIsTranslationEnabled(bubble, true);
            var visual = ElementCompositionPreview.GetElementVisual(bubble);
            // Exactly on its pod (see NotchSurface's QuickLeft): centred on it and as big, so it never spills out of it as the
            // pod comes out of the notch or goes back in; and showing only once the pod is (nearly) out.
            visual.CenterPoint = new Vector3((float)_layout.QuickSize / 2, (float)_layout.QuickSize / 2, 0);
            const string Width = "Lerp(cw, qw, n.Progress)";
            const string Left = "Lerp((n.HostWidth - n.Width) * 0.5, (n.HostWidth - n.Width) * 0.5 - gap - qw, n.Progress)";
            _motion.Bind($"Vector3({Left} + {Width} * 0.5 - x - qw * 0.5, d * (n.Progress - 1) + ({Width} - qw) * 0.5, 0)")
                .With("x", left).With("d", below).With("cw", _layout.Collapsed.Height).With("qw", _layout.QuickSize)
                .With("gap", _layout.PodGap)
                .Start(visual, "Translation");
            _motion.Bind($"Vector3({Width} / qw, {Width} / qw, 1)")
                .With("cw", _layout.Collapsed.Height).With("qw", _layout.QuickSize)
                .Start(visual, "Scale");
            _motion.Bind("Clamp((n.Progress - 0.8) * 5, 0, 1) * Clamp(b.Shown, 0, 1)").With("b", _tabState).Start(visual, "Opacity");
        }
    }

    /// <summary>Clickable only open on a tab; their levels read each time the notch opens (changed elsewhere meanwhile).</summary>
    private void UpdateQuick()
    {
        if (_quick is null)
        {
            return;
        }

        bool open = _phase == NotchPhase.Expanded && QuickShown;
        VolumeBubble.IsHitTestVisible = open;
        BrightnessBubble.IsHitTestVisible = open;
        if (!open)
        {
            CloseMixer(); // With the notch, or for a bar (the search).
        }

        if (open && !_quickOpen)
        {
            ShowVolume(_quick.Audio.Read());
            if (_quick.Brightness.Read() is { } brightness)
            {
                _brightnessLevel = brightness;
                BrightnessBubble.Show(brightness, BrightnessGlyph);
            }
        }

        _quickOpen = open;
    }

    /// <summary>The quick controls' rings and the mixer's sliders, in the mascot's colour (see MascotColor.Accent).</summary>
    private void TintQuick(bool animate)
    {
        var accent = MascotPalette.Find(_settings.MascotColor).Accent;
        VolumeBubble.Tint(accent, animate);
        BrightnessBubble.Tint(accent, animate);
        Mixer.Tint(accent);
    }

    // ── Volume mixer ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The volume mixer: a right click on the volume opens it left of the quick controls, at the top, as wide as the room
    /// there allows (it never goes under them, nor past the window's edge), following them when a module widens the open
    /// notch; it grows out of its corner beside them with a spring (never past its own edge: nothing of it shows over
    /// them), and its mouse area stays until it has faded out (the window's region would cut it while it fades).
    /// </summary>
    private void BindMixer(double quickLeft)
    {
        double right = quickLeft - MixerGap;
        Mixer.Width = Math.Clamp(right - MixerEdge, MixerPanel.SmallestWidth, MixerPanel.PanelWidth);
        double left = right - Mixer.Width;
        Mixer.Margin = new Thickness(left, _layout.TopInset + MixerTop, 0, 0);

        var state = _motion.Compositor.CreatePropertySet();
        _mixerShown = new SpringChannel(state, "Open", 0);
        ElementCompositionPreview.SetIsTranslationEnabled(Mixer, true);
        var visual = ElementCompositionPreview.GetElementVisual(Mixer);
        visual.CenterPoint = new Vector3((float)Mixer.Width, 0, 0);
        _motion.Bind("Clamp(m.Open, 0, 1) * Clamp((n.Progress - 0.8) * 5, 0, 1)").With("m", state).Start(visual, "Opacity");
        // Only when a module widens the notch: as it closes and narrows, it does not slide over it while it fades.
        _motion.Bind("Vector3(Max((ew - Max(n.Width, ew)) * 0.5, e - x), 0, 0)")
            .With("m", state).With("ew", _layout.Expanded.Width).With("e", MixerEdge).With("x", left).Start(visual, "Translation");
        _motion.Bind("Vector3(1, 1, 1) * Lerp(0.9, 1, Clamp(m.Open, 0, 1))").With("m", state).Start(visual, "Scale");

        // A right click on the volume, rather than the notch's menu.
        VolumeBubble.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (_mixerOpen)
            {
                CloseMixer();
            }
            else
            {
                OpenMixer();
            }
        };
    }

    // The mixer's room: its gap to the quick controls, its top below the screen edge, and the least it keeps from the
    // window's edge.
    private const double MixerGap = 10;
    private const double MixerTop = 4;
    private const double MixerEdge = 6;

    private void OpenMixer()
    {
        if (_quick is null || _mixerShown is null || _mixerOpen || _phase != NotchPhase.Expanded || !QuickShown)
        {
            return;
        }

        _mixerOpen = true;
        _mixerShowing = true;
        Mixer.Show(_quick.Mixer.Read(), _quick.Icons);
        Mixer.Visibility = Visibility.Visible;
        Mixer.IsHitTestVisible = true;
        _mixerShown.AnimateTo(1, Motion.Toggle);
        UpdateSideBounds();
    }

    /// <summary>The mixer closes (its apps' sessions let go of); it fades, then leaves the layout.</summary>
    private void CloseMixer()
    {
        if (!_mixerOpen || _mixerShown is null)
        {
            return;
        }

        _mixerOpen = false;
        Mixer.IsHitTestVisible = false;
        _quick?.Mixer.Release();
        var gone = _mixerShown.AnimateTo(0, Motion.Fade);
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = gone;
        timer.Tick += (_, _) =>
        {
            if (!_mixerOpen)
            {
                Mixer.Visibility = Visibility.Collapsed;
                _mixerShowing = false;
                UpdateSideBounds(); // Faded out: its area goes.
            }
        };
        timer.Start();
    }

    /// <summary>The mixer, as it is laid out under the open notch <paramref name="width"/> wide (rim included).</summary>
    private Rect MixerBounds(double width)
    {
        Mixer.Measure(new Size(Mixer.Width, double.PositiveInfinity));
        double left = Math.Max(Mixer.Margin.Left + Math.Min(_layout.Expanded.Width - width, 0) / 2, MixerEdge);
        return new Rect(left - 4, 0, Mixer.Width + MixerGap + 4, Mixer.Margin.Top + Mixer.DesiredSize.Height + 6);
    }

    /// <summary>The little notches beside the open one changed (the mixer came or went): the mouse areas follow.</summary>
    private void UpdateSideBounds()
    {
        if (_phase == NotchPhase.Expanded)
        {
            _sideHover = SideBounds(OpenNow.Width, 1);
            PublishSideBounds(_sideHover);
        }
    }

    private void ReleaseQuickHold()
    {
        _quickHold?.Dispose();
        _quickHold = null;
    }

    private void ShowVolume(VolumeState volume)
    {
        if (volume.IsAvailable)
        {
            _volumeLevel = volume.IsMuted ? 0 : volume.Level;
            VolumeBubble.Show(_volumeLevel, volume.Glyph);
        }
    }

    /// <summary>
    /// Where the pod is (fillets and rim included) beside a notch <paramref name="width"/> wide, <paramref name="progress"/>
    /// of the way out (see NotchSurface's PodLeft).
    /// </summary>
    private Rect PodBounds(double width, double progress)
    {
        double closed = _layout.Collapsed.Height;
        double size = closed + (_layout.PodSize - closed) * progress;
        double right = (_layout.CanvasWidth + width) / 2;
        double left = right - closed + (closed + _layout.PodGap) * progress;
        double fillet = _layout.ShoulderExtent;
        return new Rect(left - fillet - 2, 0, size + 2 * fillet + 4, _layout.TopInset + size + 4);
    }

    /// <summary>On or off (the setting), and paying attention while the notch is open (it follows the pointer).</summary>
    private void UpdateMascot()
    {
        if (!_mascotHosted)
        {
            return;
        }

        _motion.Properties.InsertScalar("Mascot", MascotOn ? 1 : 0);
        _motion.Away.AnimateTo(MascotAway ? 1 : 0, Motion.Fade);
        bool attentive = MascotOn && _phase == NotchPhase.Expanded;
        Buddy.IsShown = MascotOn;
        Buddy.IsAttentive = attentive;

        // Laid out at its open place: closed, that spot is the handle's, which must stay the notch's.
        Buddy.IsHitTestVisible = attentive && !MascotAway;
        PodBack.IsHitTestVisible = MascotAway;
    }

    /// <summary>A module lends the mascot the music's beat (see <see cref="NotchContext.ShareBeat"/>).</summary>
    private void ShareBeat(CompositionPropertySet? beat) => Buddy.Groove(beat);

    /// <summary>
    /// A module has the mascot act something out (the assistant, see NotchContext.ActMascot): it does, and the notch's
    /// edges glow while it thinks, and only then. Once the glow has faded out, nothing of it turns any more.
    /// </summary>
    private void ActMascot(MascotAct act)
    {
        Buddy.Act(act);
        bool glowing = act == MascotAct.Think;
        if (glowing == _glowing)
        {
            return;
        }

        _glowing = glowing;
        var fade = _motion.Glow.AnimateTo(glowing ? 1 : 0, Motion.Fade);
        _glowTimer.Stop();
        if (glowing)
        {
            _surface.Shimmer(true);
        }
        else
        {
            _glowTimer.Interval = fade + TimeSpan.FromMilliseconds(100);
            _glowTimer.Start();
        }
    }

    private void ApplyHandle(AppSettings settings) => _layout = _layout with
    {
        Handle = _layout.Handle with { Width = settings.HandleWidth },
        HandleReach = settings.HandleReach,
    };

    /// <summary>The closed notch, under the pointer: only its handle shows (see <see cref="SetPeeking"/>).</summary>
    private bool IsHandle => _peeking;

    private void OnSettingsMenuItemClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnQuitMenuItemClick(object sender, RoutedEventArgs e) => Application.Current.Exit();

    /// <summary>
    /// Buttons and text fields (the notes' editor too: double-clicking a word selects it) handle their own clicks:
    /// tapping them neither opens nor pins the notch.
    /// </summary>
    private static bool IsInsideButton(object source)
    {
        for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase or TextBox or RichEditBox)
            {
                return true;
            }
        }

        return false;
    }

    private static Rect ExtendRight(Rect rect, double amount) => new(rect.X, rect.Y, rect.Width + amount, rect.Height);

    private static Rect Inflate(Rect rect, double amount) =>
        new(rect.X - amount, rect.Y - amount, rect.Width + 2 * amount, rect.Height + 2 * amount);

    /// <summary>A question asked by a module (<see cref="Ask"/>): answered, it is disposed.</summary>
    private sealed class Question(NotchView owner, FrameworkElement card, Size size) : IDisposable
    {
        public FrameworkElement Card { get; } = card;

        public Size Size { get; } = size;

        public void Dispose() => owner.EndQuestion(this);
    }

    /// <summary>A module that takes drops, and where its tab is.</summary>
    private sealed record DropTarget(int Module, INotchDropTarget Target);

    /// <summary>A hosted module: its view, its context and what it asks of the notch's shape.</summary>
    private sealed class ModuleSlot(INotchModule module, NotchContext context)
    {
        public INotchModule Module { get; } = module;

        public NotchContext Context { get; } = context;

        public FrameworkElement? View { get; set; }

        public double CompactWidth { get; set; }

        public NotchShape? ExpandedShape { get; set; }

        /// <summary>What it has for the closed notch (see <see cref="NotchContext.SetPresence"/>).</summary>
        public NotchPresence Presence { get; set; }
    }
}
