using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Ilot.Composition;
using Ilot.Localization;
using Ilot.Notch;
using Ilot.Settings;
using Windows.UI;

namespace Ilot.Modules.Pomodoro;

/// <summary>
/// The Pomodoro tab: the ring of the phase (the mascot's colour, lighter for a break; emptying smoothly on the compositor as the time goes, the time
/// left in it), the phase, the sessions as dots, today's focus, and the controls. The timer goes on with the notch
/// closed: it stays at the top as a live activity (its glyph ringed by the time gone), the mascot reads its book while a
/// focus runs, and each phase ends with a chime, a word from the notch and a little bounce of the ring.
/// </summary>
/// <remarks>
/// Nothing runs while the timer does not: the clock is read twice a second only while it runs, the ring is one linear
/// animation per run (no frame by frame), and its glow stays still.
/// </remarks>
public sealed partial class PomodoroView : UserControl
{
    private const float RingSize = 104;
    private const float RingRadius = 46;
    private const float RingThickness = 6;

    private static readonly Color Paused = Color.FromArgb(0xFF, 0xB8, 0xBC, 0xC6);

    private static readonly TimeSpan ReadEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a phase's end stays told, the mascot out in its card.</summary>
    private static readonly TimeSpan CardFor = TimeSpan.FromSeconds(4.5);

    private readonly PomodoroTimer _timer;
    private readonly PomodoroStats _stats;
    private readonly AppSettings _settings;
    private readonly NotchContext _context;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clock;
    private readonly CompositionPropertySet _ring;
    private readonly SpringChannel _pop;
    private CompositionColorBrush? _arc;
    private CompositionColorGradientStop? _glowCenter;
    private CompositionColorGradientStop? _glowMiddle;
    private bool _reading;
    private bool _playing;
    private PomodoroCard? _card;
    private PomodoroSettingsPanel? _settingsPanel;
    private NotchBubble? _settingsDrop;
    private string _shownTime = "";

    public PomodoroView(PomodoroTimer timer, PomodoroStats stats, AppSettings settings, NotchContext context)
    {
        _timer = timer;
        _stats = stats;
        _settings = settings;
        _context = context;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.

        LayoutRoot.Margin = new Thickness(0, context.ContentTop + 2, 0, 0);

        // Opens with the notch, after the tab bar; slides and fades with the tab selection.
        ElementCompositionPreview.SetIsTranslationEnabled(LayoutRoot, true);
        var root = ElementCompositionPreview.GetElementVisual(LayoutRoot);
        context.Bind($"Clamp((n.Expansion - 0.45) * 2.4, 0, 1) * {NotchContext.TabOpacity}").Start(root, "Opacity");
        context.Bind($"Vector3({NotchContext.TabOffset}, (1 - n.Progress) * -8, 0)").Start(root, "Translation");

        var compositor = context.Motion.Compositor;
        _ring = compositor.CreatePropertySet();
        _ring.InsertScalar("Left", 1);
        _pop = new SpringChannel(_ring, "Pop", 0);
        ElementCompositionPreview.SetElementChildVisual(RingHost, CreateRing(compositor));
        var dial = ElementCompositionPreview.GetElementVisual(Dial);
        dial.CenterPoint = new Vector3(RingSize / 2, RingSize / 2, 0);
        new CompositionExpression(compositor, "Vector3(1, 1, 1) * (1 + r.Pop * 0.08)").With("r", _ring).Start(dial, "Scale");

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = ReadEvery;
        _clock.Tick += (_, _) =>
        {
            _timer.Tick();
            ShowTime();
        };

        PlayButton.Click += (_, _) => _timer.Toggle();
        RestartButton.Click += (_, _) => _timer.Restart();
        SkipButton.Click += (_, _) => _timer.Skip();
        NewSeriesButton.Click += (_, _) =>
        {
            // The whole series over: a focus, session 1, not running (today's focus stays counted).
            _timer.Reset();
            _shownTime = "";
            ShowTime(); // The timer at the top goes too.
        };
        SettingsButton.Click += (_, _) => ToggleSettings();

        // The notch closed (drawn back in with it), or another tab: the settings' drop goes.
        context.ExpandedChanged += (_, _) =>
        {
            if (!context.IsExpanded)
            {
                _settingsDrop = null;
                SettingsButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        };
        context.SelectionChanged += (_, _) =>
        {
            if (!context.IsSelected)
            {
                CloseSettings();
            }
        };
        _timer.Changed += (_, _) => Show();
        _timer.Finished += (_, phase) => OnFinished(phase);
        _stats.Changed += (_, _) => ShowToday();
        settings.PropertyChanged += OnSettingsChanged;

        _timer.Use(PlanOf(settings));
        Show();
        ShowToday();
    }

    /// <summary>The plan the settings make (lengths, sessions, what starts on its own).</summary>
    private static PomodoroPlan PlanOf(AppSettings settings) => new(
        TimeSpan.FromMinutes(settings.PomodoroFocusMinutes),
        TimeSpan.FromMinutes(settings.PomodoroShortBreakMinutes),
        TimeSpan.FromMinutes(settings.PomodoroLongBreakMinutes),
        (int)settings.PomodoroSessions,
        settings.PomodoroAutoStartBreaks,
        settings.PomodoroAutoStartFocus);

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName?.StartsWith("Pomodoro", StringComparison.Ordinal) == true && e.PropertyName != nameof(AppSettings.PomodoroSound))
        {
            _timer.Use(PlanOf(_settings));
            Show();
        }
        else if (e.PropertyName == nameof(AppSettings.MascotColor))
        {
            Show(); // Its colour is the mascot's.
            _shownTime = "";
            ShowTime();
        }
    }

    /// <summary>The mascot's colour (see MascotColor.Accent): the focus's.</summary>
    private Color FocusColor => Mascot.MascotPalette.Find(_settings.MascotColor).Accent;

    /// <summary>The phase's colour, the mascot's: as it is for the focus, lighter for a break (the long one a little less).</summary>
    private Color PhaseColor
    {
        get
        {
            var mascot = Mascot.MascotPalette.Find(_settings.MascotColor);
            return _timer.Phase switch
            {
                PomodoroPhase.Focus => mascot.Accent,
                PomodoroPhase.ShortBreak => mascot.Light(0.8),
                _ => mascot.Light(0.7),
            };
        }
    }

    // ── Its settings ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The gear: the settings fall under the notch in a drop, as tall as they are; again, they go back in.</summary>
    private void ToggleSettings()
    {
        if (_settingsDrop is not null)
        {
            CloseSettings();
            return;
        }

        _settingsPanel ??= new PomodoroSettingsPanel(_settings);
        _settingsPanel.Measure(new Windows.Foundation.Size(PomodoroSettingsPanel.Size.Width, double.PositiveInfinity));
        double height = Math.Ceiling(_settingsPanel.DesiredSize.Height);
        _settingsDrop = _context.ShowBubble(_settingsPanel, new Windows.Foundation.Size(PomodoroSettingsPanel.Size.Width, height > 0 ? height : PomodoroSettingsPanel.Size.Height));
        SettingsButton.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    }

    private void CloseSettings()
    {
        var drop = _settingsDrop;
        _settingsDrop = null;
        drop?.Dispose();
        SettingsButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    // ── Showing it ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>All of it, as the timer is now (started, paused, a new phase…): the clock runs only while it does.</summary>
    private void Show()
    {
        bool running = _timer.State == PomodoroState.Running;
        if (running)
        {
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }

        var color = PhaseColor;
        PhaseText.Text = _timer.Phase switch
        {
            PomodoroPhase.Focus => Loc.T("Focus"),
            PomodoroPhase.ShortBreak => Loc.T("Petite pause"),
            _ => Loc.T("Grande pause"),
        };
        StateText.Text = _timer.State switch
        {
            PomodoroState.Running => _timer.Phase == PomodoroPhase.Focus ? Loc.T("Concentration") : Loc.T("Respirez"),
            PomodoroState.Paused => Loc.T("En pause"),
            _ => Loc.T("Prêt"),
        };
        SessionText.Text = _timer.Phase switch
        {
            PomodoroPhase.Focus => Loc.F("Session {0} sur {1}", _timer.Done + 1, _timer.Plan.SessionsBeforeLongBreak),
            PomodoroPhase.ShortBreak => Loc.F("{0} sur {1} faites", _timer.Done, _timer.Plan.SessionsBeforeLongBreak),
            _ => Loc.T("Cycle terminé, bravo !"),
        };
        ShowDots(color);

        PlayGlyph.Glyph = running ? "" : "";
        string play = running ? Loc.T("Pause") : _timer.State == PomodoroState.Paused ? Loc.T("Reprendre") : Loc.T("Démarrer");
        ToolTipService.SetToolTip(PlayButton, play);
        AutomationProperties.SetName(PlayButton, play);
        PlayButton.Background = new SolidColorBrush(running || _timer.State == PomodoroState.Ready ? color : Paused);
        RestartButton.IsEnabled = _timer.State != PomodoroState.Ready || _timer.LeftShare < 1;
        bool begun = _timer.Done > 0 || _timer.Phase != PomodoroPhase.Focus || RestartButton.IsEnabled;
        NewSeriesButton.Visibility = begun ? Visibility.Visible : Visibility.Collapsed;

        Tint(running || _timer.State == PomodoroState.Ready ? color : Paused);
        ShowRing();
        ShowTime();

        // The mascot reads its book while a focus runs, and plays with its gamepad while a break does.
        bool reading = running && _timer.Phase == PomodoroPhase.Focus;
        if (reading != _reading)
        {
            _reading = reading;
            _context.MascotReads(reading);
        }

        bool playing = running && _timer.Phase != PomodoroPhase.Focus;
        if (playing != _playing)
        {
            _playing = playing;
            _context.MascotPlays(playing);
        }
    }

    /// <summary>The time left (and, with the notch closed, at the top), when its second changes.</summary>
    private void ShowTime()
    {
        string time = Format(_timer.Left);
        if (time != _shownTime)
        {
            _shownTime = time;
            TimeText.Text = time;
            ShowActivity();
        }
    }

    /// <summary>
    /// The timer at the top of the closed notch (a live activity) while a phase is going or paused: the phase's glyph
    /// ringed by the time gone, and the time left. Nothing while nothing has started.
    /// </summary>
    private void ShowActivity()
    {
        if (_timer.State == PomodoroState.Ready)
        {
            _context.SetActivity(null);
            return;
        }

        bool paused = _timer.State == PomodoroState.Paused;
        string glyph = paused ? "" : _timer.Phase == PomodoroPhase.Focus ? "" : "";
        _context.SetActivity(new NotchActivity(glyph, Format(_timer.Left), 1 - _timer.LeftShare, paused ? Paused : PhaseColor));
    }

    /// <summary>The sessions before the long break: done ones in the focus colour, the one going outlined, the rest faint.</summary>
    private void ShowDots(Color color)
    {
        Dots.Children.Clear();
        int count = _timer.Plan.SessionsBeforeLongBreak;
        for (int i = 0; i < count; i++)
        {
            bool done = i < _timer.Done;
            bool current = i == _timer.Done && _timer.Phase == PomodoroPhase.Focus;
            Dots.Children.Add(new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(done ? FocusColor : current ? Color.FromArgb(0x40, FocusColor.R, FocusColor.G, FocusColor.B) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(current ? 1.5 : 0),
                BorderBrush = new SolidColorBrush(FocusColor),
            });
        }
    }

    private void ShowToday()
    {
        var today = _stats.Today;
        TodayText.Text = today.Sessions == 0
            ? Loc.T("Aujourd'hui : pas encore de focus")
            : Loc.F("Aujourd'hui : {0} · {1} de focus", Loc.Count(today.Sessions, "session", "sessions"), Minutes(today.Minutes));
    }

    // ── The ring ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ring: a soft glow of the phase's colour behind it, a faint track, and the time left as an arc from the top,
    /// clockwise (a composition ellipse's path starts at its top and runs clockwise), its ends round.
    /// </summary>
    private ContainerVisual CreateRing(Compositor compositor)
    {
        var ring = compositor.CreateContainerVisual();
        ring.Size = new Vector2(RingSize);

        var glow = compositor.CreateSpriteVisual();
        var light = compositor.CreateRadialGradientBrush();
        _glowCenter = compositor.CreateColorGradientStop(0, Color.FromArgb(0x38, FocusColor.R, FocusColor.G, FocusColor.B));
        _glowMiddle = compositor.CreateColorGradientStop(0.55f, Color.FromArgb(0x12, FocusColor.R, FocusColor.G, FocusColor.B));
        light.ColorStops.Add(_glowCenter);
        light.ColorStops.Add(_glowMiddle);
        light.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0, 0, 0, 0)));
        glow.Brush = light;
        glow.Size = new Vector2(RingSize * 1.6f);
        glow.Offset = new Vector3(-RingSize * 0.3f, -RingSize * 0.3f, 0);
        ring.Children.InsertAtTop(glow);

        var shapes = compositor.CreateShapeVisual();
        shapes.Size = new Vector2(RingSize);
        var center = new Vector2(RingSize / 2);
        var track = compositor.CreateEllipseGeometry();
        track.Center = center;
        track.Radius = new Vector2(RingRadius);
        var trackShape = compositor.CreateSpriteShape(track);
        trackShape.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        trackShape.StrokeThickness = RingThickness;
        shapes.Shapes.Add(trackShape);

        var arc = compositor.CreateEllipseGeometry();
        arc.Center = center;
        arc.Radius = new Vector2(RingRadius);
        new CompositionExpression(compositor, "Clamp(r.Left, 0, 1)").With("r", _ring).Start(arc, "TrimEnd");
        var arcShape = compositor.CreateSpriteShape(arc);
        _arc = compositor.CreateColorBrush(FocusColor);
        arcShape.StrokeBrush = _arc;
        arcShape.StrokeThickness = RingThickness;
        arcShape.StrokeStartCap = CompositionStrokeCap.Round;
        arcShape.StrokeEndCap = CompositionStrokeCap.Round;
        shapes.Shapes.Add(arcShape);
        ring.Children.InsertAtTop(shapes);
        return ring;
    }

    /// <summary>
    /// The arc at the time left: running, one linear animation from now to the phase's end (the compositor draws it, no
    /// tick); otherwise still where it is.
    /// </summary>
    private void ShowRing()
    {
        _ring.StopAnimation("Left");
        float left = (float)_timer.LeftShare;
        _ring.InsertScalar("Left", left);
        if (_timer.State != PomodoroState.Running || _timer.Left <= TimeSpan.Zero)
        {
            return;
        }

        var compositor = _ring.Compositor;
        var run = compositor.CreateScalarKeyFrameAnimation();
        run.InsertKeyFrame(0, left);
        run.InsertKeyFrame(1, 0, compositor.CreateLinearEasingFunction());
        run.Duration = _timer.Left;
        _ring.StartAnimation("Left", run);
    }

    /// <summary>The arc and its glow take <paramref name="color"/>, fading to it.</summary>
    private void Tint(Color color)
    {
        Fade(_arc, color);
        Fade(_glowCenter, Color.FromArgb(0x38, color.R, color.G, color.B));
        Fade(_glowMiddle, Color.FromArgb(0x12, color.R, color.G, color.B));

        static void Fade(CompositionObject? target, Color to)
        {
            if (target is null)
            {
                return;
            }

            var fade = target.Compositor.CreateColorKeyFrameAnimation();
            fade.InsertKeyFrame(1, to);
            fade.Duration = TimeSpan.FromMilliseconds(450);
            target.StartAnimation("Color", fade);
        }
    }

    // ── A phase ends ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A phase ran to its end: a focus counts for today; the chime (if the settings say so), and the notch tells it: the
    /// mascot comes out of its place into a card and says it (« Temps écoulé ! On prend une pause »), its gamepad
    /// already in its hands if the break runs; with the notch open (no card then), a word from it. The ring bounces.
    /// </summary>
    private void OnFinished(PomodoroPhase finished)
    {
        bool focus = finished == PomodoroPhase.Focus;
        if (focus)
        {
            _stats.Count(_timer.Plan.Focus);
        }

        if (_settings.PomodoroSound)
        {
            Chime.Play(rest: focus);
        }

        string next = Minutes((int)Math.Round(_timer.Length.TotalMinutes));
        if (_card is null)
        {
            _card = new PomodoroCard();
            _card.Attach(_context);
        }

        _card.Show(
            focus ? Loc.T("Temps écoulé !") : Loc.T("C'est reparti !"),
            focus ? "" : "",
            focus ? Loc.F("On prend une pause · {0}", next) : Loc.F("Focus · {0}", next),
            PhaseColor);
        if (!_context.Present(_card, PomodoroCard.Size, CardFor, withMascot: true))
        {
            _context.Announce(
                focus ? "" : "",
                focus ? Loc.F("Focus terminé : {0} de pause", next) : Loc.T("Pause finie : on s'y remet"),
                PhaseColor);
        }

        _pop.Kick(6, new Spring(0.5, 0.45));
    }

    // ── Formats ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>« 24:59 », « 1:05:00 » past an hour.</summary>
    private static string Format(TimeSpan time)
    {
        var whole = TimeSpan.FromSeconds(Math.Ceiling(time.TotalSeconds));
        return whole.TotalHours >= 1
            ? whole.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : whole.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>« 45 min », « 1 h », « 1 h 15 ».</summary>
    private static string Minutes(int minutes) => minutes < 60
        ? Loc.F("{0} min", minutes)
        : minutes % 60 == 0 ? Loc.F("{0} h", minutes / 60) : Loc.F("{0} h {1:00}", minutes / 60, minutes % 60);
}
