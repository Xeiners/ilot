using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Ilot.Composition;
using Ilot.Modules.Media.Audio;
using Ilot.Modules.Media.Lyrics;
using Ilot.Notch;
using Ilot.Settings;
using Windows.UI;
using Ilot.Localization;

namespace Ilot.Modules.Media;

/// <summary>
/// Media module view, choreographed by the notch morph.
/// </summary>
/// <remarks>
/// Collapsed, the artwork and the live visualizer ride the edges of the pill. As the notch opens they glide to
/// their expanded place (a connected animation driven by the morph itself, so it stays perfect when the motion is
/// interrupted) while the title, the controls and the timeline bloom in. Expressions reference the notch state as
/// <c>n</c> (see <see cref="NotchMotion"/>), the tab selection as <c>t</c> (see <see cref="NotchContext"/>) and this
/// view's own state as <c>m</c>.
/// </remarks>
public sealed partial class MediaView : UserControl
{
    // Compact layout, inside the collapsed pill.
    private const float CompactInset = 6;
    private const float CompactEqualizerInset = 12;
    private const float CompactArtworkRadius = 6;
    private const float CompactProgressThickness = 3;
    private const float CompactProgressGap = 12;

    // In lyrics mode the closed pill widens like a live activity, to give the sung line room. The open notch keeps
    // its size: lyrics only take the place of the title and the artist.
    private const double CompactLyricsWidth = 380;

    // Smallest artwork corner radius, should the notch corners ever be tighter than the artwork margin.
    private const float MinArtworkRadius = 4;

    // Ambient light, in pixels from the live left edge of the notch: an ellipse that ends 8 px inside the edge
    // and reaches under the title, radiating from the centre of the artwork.
    private const float GlowCenterX = 150;
    private const float GlowRadiusX = 140;
    private const float GlowFocusX = 56;
    private static readonly float[] GlowStopOffsets = [0, 0.4f, 1];
    private static readonly double[] GlowStopStrengths = [1, 0.45, 0];

    // Visualizers: the small one rides the pill and the title line, the large one fills the right column when
    // the track has no lyrics. Bars jump up with the music and settle back more slowly, like a VU meter.
    private const int CompactBarCount = 6;
    private const double BarWidth = 3;
    private const double SpectrumBarWidth = 4;
    private const double AttackSeconds = 0.035;
    private const double ReleaseSeconds = 0.2;

    private static readonly TimeSpan ClockInterval = TimeSpan.FromMilliseconds(250);

    private const int WheelNotch = 120;

    // How long the "connected" / "disconnected" card shows.
    private static readonly TimeSpan DeviceCardTime = TimeSpan.FromMilliseconds(2500);

    private readonly NotchContext _context;
    private readonly AppSettings _settings;
    private readonly NotchMotion _motion;
    private readonly IAudioSpectrumService _spectrum;
    private readonly IAudioService _audio;
    private readonly AudioDeviceMonitor _devices;
    private readonly AudioPopupView _deviceCard = new();
    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _state;
    private readonly SpringChannel _present;
    private readonly SpringChannel _pill; // 1: the player on the closed pill; 0: on the taskbar instead (see PillShown).
    private readonly SpringChannel _playing;
    private readonly SpringChannel _swap;
    private readonly SpringChannel _artworkFade;
    private readonly SpringChannel _timelineShown;
    private readonly SpringChannel _lyricsShown;
    private readonly SpringChannel _pillLyrics; // The sung line on the closed pill (not when it goes on the taskbar).
    private TaskbarLyrics? _taskbarLyrics;
    private int _wheel;
    private readonly SolidColorBrush _accentBrush;
    private readonly Rectangle[] _bars;
    private readonly Rectangle[] _spectrumBars;
    private readonly string[] _barNames;
    private readonly string[] _bandNames;
    private readonly float[] _targets;
    private readonly float[] _levels;
    private readonly SpriteVisual _glow;
    private readonly CompositionRadialGradientBrush _glowBrush;
    private readonly CompositionColorGradientStop[] _glowStops;
    private readonly ShapeVisual _compactProgress;
    private readonly CompositionColorBrush _compactProgressBrush;
    private readonly DispatcherQueueTimer _clock;
    private readonly Stopwatch _frameClock = new();
    private IDisposable? _scrubHold;
    private bool _visualizerRunning;
    private double _bass;

    public MediaView(
        MediaViewModel viewModel,
        LyricsViewModel lyrics,
        IAudioSpectrumService spectrum,
        IAudioService audio,
        AudioDeviceMonitor devices,
        VolumeWatcher volume,
        AppSettings settings,
        NotchContext context)
    {
        _audio = audio;
        _devices = devices;
        ViewModel = viewModel;
        Lyrics = lyrics;
        _settings = settings;
        _spectrum = spectrum;
        _context = context;
        _motion = context.Motion;
        _compositor = _motion.Compositor;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        PlaceUnderTabs(context.ContentTop);

        _state = _compositor.CreatePropertySet();
        _present = new SpringChannel(_state, "Present", viewModel.HasMedia ? 1 : 0);
        _pill = new SpringChannel(_state, "Pill", 1);
        _playing = new SpringChannel(_state, "Playing", viewModel.IsPlaying ? 1 : 0);
        _swap = new SpringChannel(_state, "Swap", 1);
        _artworkFade = new SpringChannel(_state, "Art", 1);
        _timelineShown = new SpringChannel(_state, "Timeline", viewModel.Timeline.IsKnown ? 1 : 0);
        _lyricsShown = new SpringChannel(_state, "Lyrics", lyrics.IsShown ? 1 : 0);
        _pillLyrics = new SpringChannel(_state, "PillLyrics", InPill ? 1 : 0);
        _state.InsertScalar("KeepBack", 1);
        _state.InsertScalar("Bass", 0);
        _state.InsertScalar("Progress", 0);

        _accentBrush = new SolidColorBrush(viewModel.Accent);
        _bars = CreateBars(Equalizer, CompactBarCount, BarWidth);
        _spectrumBars = CreateBars(Spectrum, spectrum.BandCount, SpectrumBarWidth);
        _barNames = Names("L", _bars.Length);
        _bandNames = Names("B", _spectrumBars.Length);
        _targets = new float[spectrum.BandCount];
        _levels = new float[spectrum.BandCount];
        PinIndicator.Background = _accentBrush;
        ArtworkFront.Source = viewModel.Artwork;

        _glowBrush = _compositor.CreateRadialGradientBrush();
        _glowBrush.MappingMode = CompositionMappingMode.Absolute;
        _glowBrush.ExtendMode = CompositionGradientExtendMode.Clamp;
        _glowBrush.GradientOriginOffset = new Vector2(GlowFocusX - GlowCenterX, 0);
        _glowStops = new CompositionColorGradientStop[GlowStopOffsets.Length];
        for (int i = 0; i < _glowStops.Length; i++)
        {
            _glowStops[i] = _compositor.CreateColorGradientStop(GlowStopOffsets[i], default);
            _glowBrush.ColorStops.Add(_glowStops[i]);
        }

        SetGlowColor(viewModel.Accent, animate: false);
        _glow = _compositor.CreateSpriteVisual();
        _glow.Brush = _glowBrush;
        ElementCompositionPreview.SetElementChildVisual(GlowHost, _glow);

        _compactProgressBrush = _compositor.CreateColorBrush(viewModel.Accent);
        _compactProgress = _compositor.CreateShapeVisual();
        ElementCompositionPreview.SetElementChildVisual(CompactProgressHost, _compactProgress);

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = ClockInterval;
        _clock.Tick += (_, _) => UpdateTimes();
        TimelineScrubber.Scrubbing += OnScrubbing;
        TimelineScrubber.SeekRequested += OnSeekRequested;

        // Hidden until the layout-dependent choreography is bound: the expanded layout must never flash
        // inside the pill.
        VisualOf(LayoutRoot).Opacity = 0;
        BindStaticChoreography();
        LayoutRoot.SizeChanged += (_, _) => BindLayoutChoreography();

        ShowPresence();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Lyrics.PropertyChanged += OnLyricsPropertyChanged;
        Lyrics.Synced += (_, position) =>
        {
            LyricsLines.Sync(position);
            CompactLyrics.Sync(position);
        };
        LyricsLines.SetLyrics(lyrics.Content);
        CompactLyrics.SetLyrics(lyrics.Content);
        ShowLyricsMode();
        ApplyCompactLyricsSize();
        PlaceLyrics();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.CompactLyricsFontSize))
            {
                ApplyCompactLyricsSize();
            }
            else if (e.PropertyName == nameof(AppSettings.LyricsPlace))
            {
                ShowLyricsMode();
                PlaceLyrics();
            }
            else if (e.PropertyName is nameof(AppSettings.HidePlayerInNotch) or nameof(AppSettings.HideWhenIdle))
            {
                ShowPill();
            }
        };
        _context.ExpandedChanged += (_, _) =>
        {
            UpdateClock();
            UpdateLyricsTracking();
        };
        _context.SelectionChanged += (_, _) => UpdateClock();

        // The system volume: the wheel anywhere on the player (the notch's own bubble shows and sets it too).
        Bind("Clamp((n.Expansion - 0.5) * 2.4, 0, 1)").Start(VisualOf(LyricsButton), "Opacity");
        _context.Wheel += (_, delta) => OnWheel(delta);

        // An audio device connected or disconnected: the closed notch grows down for a moment to say so.
        _deviceCard.Attach(_context);
        _devices.Switched += (_, change) => OnDeviceSwitched(change);

        // The system volume changed, however (the keyboard's keys, the taskbar…): the notch shows it.
        volume.Changed += (_, state) => ShowVolume(state);
        Loaded += (_, _) =>
        {
            UpdateVisualizer();
            UpdateTimeline();
            UpdateLyricsTracking();
        };
        Unloaded += (_, _) =>
        {
            UpdateVisualizer();
            UpdateClock();
            UpdateLyricsTracking();
        };
    }

    public MediaViewModel ViewModel { get; }

    public LyricsViewModel Lyrics { get; }

    private Rectangle[] CreateBars(Panel panel, int count, double width)
    {
        var bars = new Rectangle[count];
        for (int i = 0; i < count; i++)
        {
            bars[i] = new Rectangle
            {
                Width = width,
                RadiusX = width / 2,
                RadiusY = width / 2,
                Fill = _accentBrush,
            };
            panel.Children.Add(bars[i]);
        }

        return bars;
    }

    private string[] Names(string prefix, int count)
    {
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = prefix + i;
            _state.InsertScalar(names[i], 0);
        }

        return names;
    }

    /// <summary>The open layout starts below the tab bar: both columns move down by its height.</summary>
    private void PlaceUnderTabs(double top)
    {
        PlayerColumn.Margin = new Thickness(0, top, 0, 0);
        SideColumn.Margin = new Thickness(SideColumn.Margin.Left, top, SideColumn.Margin.Right, 0);
        ColumnDivider.Margin = new Thickness(ColumnDivider.Margin.Left, ColumnDivider.Margin.Top + top, 0, ColumnDivider.Margin.Bottom);
    }

    /// <summary>Bindings that do not depend on where layout puts things.</summary>
    private void BindStaticChoreography()
    {
        // Tabs: the open player slides away and fades when another tab is chosen. Closed, the notch always shows
        // the pill (artwork, visualizer, sung line), whatever the tab: the selection weighs only as it opens.
        var open = VisualOf(OpenContent);
        Bind($"Lerp(1, {NotchContext.TabOpacity}, n.Progress)").Start(open, "Opacity");
        Bind($"Vector3({NotchContext.TabOffset} * n.Progress, 0, 0)").Start(open, "Translation");

        // Visualizers: each bar follows its frequency band(s); paused, the bars rest as dots.
        BindBars(_bars, _barNames, Equalizer.Height, BarWidth, rest: 0.2);
        BindBars(_spectrumBars, _bandNames, Spectrum.Height, SpectrumBarWidth, rest: 0.07);

        // Play ⇄ pause: one glyph shrinks away while the other springs in.
        var play = VisualOf(PlayGlyph);
        var pause = VisualOf(PauseGlyph);
        play.CenterPoint = pause.CenterPoint = new Vector3(10, 10, 0);
        Bind("Clamp(1 - m.Playing, 0, 1)").Start(play, "Opacity");
        Bind("Vector3(Lerp(1, 0.35, m.Playing), Lerp(1, 0.35, m.Playing), 1)").Start(play, "Scale");
        Bind("Clamp(m.Playing, 0, 1)").Start(pause, "Opacity");
        Bind("Vector3(Lerp(0.35, 1, m.Playing), Lerp(0.35, 1, m.Playing), 1)").Start(pause, "Scale");

        // Track change: the new title slides up into place.
        var details = VisualOf(DetailsContent);
        Bind("Clamp(m.Swap, 0, 1)").Start(details, "Opacity");
        Bind("Vector3(0, (1 - m.Swap) * 8, 0)").Start(details, "Translation");

        // Artwork crossfade: the new cover fades in over the previous one.
        Bind("Clamp(m.Art, 0, 1)").Start(VisualOf(ArtworkFront), "Opacity");
        Bind("Max(m.KeepBack, 1 - m.Art)").Start(VisualOf(ArtworkBack), "Opacity");

        // Ambient light: blooms with the opening, only when something plays, and breathes with the bass. It fades
        // with the player's tab but never slides: it must always end inside the shape.
        Bind($"Clamp(n.Progress * m.Present * (0.75 + 0.6 * m.Bass), 0, 1) * Lerp(1, {NotchContext.TabOpacity}, n.Progress)")
            .Start(_glow, "Opacity");

        // Right column: lyrics when the track has some, the large visualizer otherwise; both bloom in with the
        // opening, slightly after the player, and cross-fade when lyrics arrive.
        const string SideReveal = "Clamp((n.Expansion - 0.5) * 2.4, 0, 1)";
        var lyrics = VisualOf(LyricsLines);
        Bind($"{SideReveal} * Clamp(m.Lyrics, 0, 1)").Start(lyrics, "Opacity");
        Bind("Vector3(0, (1 - n.Progress) * -8 + (1 - Clamp(m.Lyrics, 0, 1)) * 6, 0)").Start(lyrics, "Translation");
        var spectrum = VisualOf(Spectrum);
        Bind($"{SideReveal} * (1 - Clamp(m.Lyrics, 0, 1))").Start(spectrum, "Opacity");
        Bind("Vector3(0, (1 - n.Progress) * -8, 0)").Start(spectrum, "Translation");
        Bind(SideReveal).Start(VisualOf(ColumnDivider), "Opacity");

        // The little bar shown while pinned.
        var pin = VisualOf(PinIndicator);
        pin.CenterPoint = new Vector3(11, 1.5f, 0);
        Bind("Clamp(n.Pin, 0, 1) * n.Progress").Start(pin, "Opacity");
        Bind("Vector3(Lerp(0.3, 1, n.Pin), 1, 1)").Start(pin, "Scale");
    }

    private void BindBars(Rectangle[] bars, string[] names, double height, double width, double rest)
    {
        for (int i = 0; i < bars.Length; i++)
        {
            var bar = VisualOf(bars[i]);
            bar.CenterPoint = new Vector3((float)width / 2, (float)height / 2, 0);
            Bind($"Vector3(1, Lerp(r, r + (1 - r) * m.{names[i]}, Clamp(m.Playing, 0, 1)), 1)").With("r", rest).Start(bar, "Scale");
        }
    }

    /// <summary>Bindings that connect measured layout positions to their compact counterparts.</summary>
    private void BindLayoutChoreography()
    {
        float width = (float)LayoutRoot.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        float compactHeight = (float)_motion.Layout.Collapsed.Height;

        // Ambient light: anchored to the live left edge like the artwork, and always ending inside the shape,
        // so the silhouette stays pure black (seamless with the shoulders and the screen edge), mid-morph too.
        _glow.Size = new Vector2(width, (float)LayoutRoot.ActualHeight);
        Bind("Vector2((w - n.Width) * 0.5 + gx, n.Height * 0.5 + 2)")
            .With("w", width).With("gx", GlowCenterX)
            .Start(_glowBrush, "EllipseCenter");
        Bind("Vector2(gr, Max(n.Height * 0.5 - 8, 1))")
            .With("gr", GlowRadiusX)
            .Start(_glowBrush, "EllipseRadius");

        // Artwork: rides the left edge of the pill when compact and glides to its slot as the notch opens.
        // It is placed from the live left edge, so it can never run ahead of the shape and get cut off.
        // Positions come from the declared layout (top-left aligned, fixed margins and sizes), never from
        // measurements: this runs again whenever the open notch is resized (lyrics mode), and measuring elements
        // mid-layout or carrying a composition transform is unreliable.
        var artwork = new Vector2((float)ArtworkHost.Margin.Left, (float)(PlayerColumn.Margin.Top + ArtworkHost.Margin.Top));
        float artworkSize = (float)ArtworkHost.Width;
        float compactScale = (compactHeight - 2 * CompactInset) / artworkSize;
        const string ArtworkScale = "Lerp(cs, 1, n.Progress)";
        var artworkVisual = VisualOf(ArtworkHost);
        artworkVisual.CenterPoint = Vector3.Zero;
        Bind($"Vector3({ArtworkScale}, {ArtworkScale}, 1)")
            .With("cs", compactScale)
            .Start(artworkVisual, "Scale");
        Bind("Vector3((w - n.Width) * 0.5 + Lerp(ci, ax, n.Progress) - ax, Lerp(ci, ay, n.Progress) - ay, 0)")
            .With("w", width).With("ci", CompactInset).With("ax", artwork.X).With("ay", artwork.Y)
            .Start(artworkVisual, "Translation");
        Bind("Max(Clamp(m.Present, 0, 1) * Clamp(m.Pill, 0, 1), Clamp(n.Expansion * 2.2 - 0.6, 0, 1))").Start(artworkVisual, "Opacity");

        // Corners that hug the notch. The corner facing the notch's own rounded corner is concentric with it
        // (notch radius minus the artwork margin: the gap between both curves stays even), the others keep a
        // discreet rounding; all meet at the same radius once the notch is open. Per-corner radii come from two
        // intersecting clips: a softly rounded square on the surface, and on the host a huge rounded rectangle
        // whose only corner reaching the artwork is the one facing the notch corner. Radii are expressed in the
        // artwork's own (scaled) space.
        const string Corner = "Lerp(cr, er, n.Progress)";
        const string Hug = "Max(n.Radius - Lerp(ci, ax, n.Progress), " + Corner + ")";
        const string CornerLocal = "Min(" + Corner + " / " + ArtworkScale + ", s * 0.5)";
        const string HugLocal = "Min(" + Hug + " / " + ArtworkScale + ", s)";
        float expandedRadius = Math.Max((float)_motion.Layout.Expanded.Radius - artwork.X, MinArtworkRadius);

        var surfaceGeometry = _compositor.CreateRoundedRectangleGeometry();
        surfaceGeometry.Size = new Vector2(artworkSize, artworkSize);
        Bind($"Vector2({CornerLocal}, {CornerLocal})")
            .With("cr", CompactArtworkRadius).With("er", expandedRadius).With("cs", compactScale).With("s", artworkSize)
            .Start(surfaceGeometry, "CornerRadius");
        VisualOf(ArtworkSurface).Clip = _compositor.CreateGeometricClip(surfaceGeometry);

        // Flush notch: only the bottom-left corner is rounded, so the hug shape reaches far above the artwork.
        // Island: both left corners are rounded.
        float reach = artworkSize * 4;
        float above = _motion.Layout.IsFlush ? reach : 0;
        var hugGeometry = _compositor.CreateRoundedRectangleGeometry();
        hugGeometry.Offset = new Vector2(0, -above);
        hugGeometry.Size = new Vector2(reach, artworkSize + above);
        Bind($"Vector2({HugLocal}, {HugLocal})")
            .With("cr", CompactArtworkRadius).With("er", expandedRadius).With("ci", CompactInset).With("ax", artwork.X)
            .With("cs", compactScale).With("s", artworkSize)
            .Start(hugGeometry, "CornerRadius");
        artworkVisual.Clip = _compositor.CreateGeometricClip(hugGeometry);

        // Visualizer: rides the right edge of the pill, then settles at the end of the title line.
        float equalizerWidth = (float)(_bars.Length * BarWidth + (_bars.Length - 1) * Equalizer.Spacing);
        float equalizerHeight = (float)Equalizer.Height;
        float titleRight = (float)(PlayerColumn.Width - Details.Margin.Right);
        float titleHeight = TitleText.ActualHeight > 0 ? (float)TitleText.ActualHeight : 20;
        Bind("Vector3(Lerp((w + n.Width) * 0.5 - ce - ew, tx, n.Progress), Lerp((ch - eh) * 0.5, ty, n.Progress), 0)")
            .With("w", width).With("ce", CompactEqualizerInset).With("ch", compactHeight)
            .With("ew", equalizerWidth).With("eh", equalizerHeight)
            .With("tx", titleRight - equalizerWidth)
            .With("ty", (float)(PlayerColumn.Margin.Top + Details.Margin.Top) + (titleHeight - equalizerHeight) / 2)
            .Start(VisualOf(Equalizer), "Translation");
        // On the pill, it gives way to announcements (n.Toast), which take over the right end.
        // With the mascot (n.Mascot), the pill's right end is its: the bars show only on the title line.
        // Off the pill when the taskbar shows the track (m.Pill).
        Bind("Clamp(m.Present, 0, 1) * Lerp(Clamp(m.Pill, 0, 1), 1, n.Progress) * (1 - Clamp(n.Toast, 0, 1) * (1 - n.Progress)) * Lerp(1 - n.Mascot, 1, n.Progress)")
            .Start(VisualOf(Equalizer), "Opacity");

        BindCompactProgress(width, compactHeight, equalizerWidth);
        BindCompactLyrics(compactHeight, equalizerWidth);

        // Title, controls and timeline bloom in during the second half of the opening, and leave first.
        var detailsVisual = VisualOf(Details);
        detailsVisual.CenterPoint = new Vector3(0, (float)Details.ActualHeight / 2, 0);
        Bind("Clamp((n.Expansion - 0.4) * 2.4, 0, 1)").Start(detailsVisual, "Opacity");
        Bind("Vector3(Lerp(0.94, 1, n.Progress), Lerp(0.94, 1, n.Progress), 1)").Start(detailsVisual, "Scale");
        Bind("Vector3(0, (1 - n.Progress) * -10, 0)").Start(detailsVisual, "Translation");

        var controls = VisualOf(TransportControls);
        controls.CenterPoint = new Vector3((float)TransportControls.ActualWidth / 2, (float)TransportControls.ActualHeight / 2, 0);
        Bind("Clamp((n.Expansion - 0.5) * 2.6, 0, 1)").Start(controls, "Opacity");
        Bind("Vector3(Lerp(0.7, 1, n.Progress), Lerp(0.7, 1, n.Progress), 1)").Start(controls, "Scale");

        var timeline = VisualOf(TimelineRow);
        Bind("Clamp((n.Expansion - 0.55) * 2.6, 0, 1) * Clamp(m.Timeline, 0, 1)").Start(timeline, "Opacity");
        Bind("Vector3(0, (1 - n.Progress) * -8, 0)").Start(timeline, "Translation");

        VisualOf(LayoutRoot).Opacity = 1;
    }

    /// <summary>
    /// Progress line of the collapsed pill: spans the space between the artwork and the visualizer, stretches with
    /// the pill (hover swell included) and fades away as the notch opens, where the scrubber takes over.
    /// </summary>
    private void BindCompactProgress(float width, float compactHeight, float equalizerWidth)
    {
        const string Left = "((w - n.Width) * 0.5 + ch - ci + g)";
        const string Length = "Max((w + n.Width) * 0.5 - ce - ew - g - " + Left + ", 0)";
        const string Filled = "(" + Length + " * Clamp(m.Progress, 0, 1))";

        _compactProgress.Size = new Vector2(width, (float)LayoutRoot.ActualHeight);
        _compactProgress.Shapes.Clear();
        _compactProgress.Shapes.Add(CreateLine(Length, _compositor.CreateColorBrush(Color.FromArgb(56, 255, 255, 255))));
        _compactProgress.Shapes.Add(CreateLine(Filled, _compactProgressBrush));
        // In lyrics mode the sung line takes its place (unless it goes on the taskbar).
        Bind("Clamp(1 - n.Expansion * 3, 0, 1) * Clamp(m.Present, 0, 1) * Clamp(m.Pill, 0, 1) * Clamp(m.Timeline, 0, 1) * (1 - Clamp(m.PillLyrics, 0, 1))")
            .Start(_compactProgress, "Opacity");

        CompositionSpriteShape CreateLine(string length, CompositionBrush brush)
        {
            var line = _compositor.CreateRoundedRectangleGeometry();
            Measure($"Vector2({length}, t)").Start(line, "Size");
            Measure($"Vector2({Left}, (ch - t) * 0.5)").Start(line, "Offset");
            Measure($"Vector2(Min(t * 0.5, {length} * 0.5), t * 0.5)").Start(line, "CornerRadius");
            var shape = _compositor.CreateSpriteShape(line);
            shape.FillBrush = brush;
            return shape;
        }

        CompositionExpression Measure(string expression) => Bind(expression)
            .With("w", width).With("ch", compactHeight).With("ci", CompactInset)
            .With("ce", CompactEqualizerInset).With("ew", equalizerWidth)
            .With("g", CompactProgressGap).With("t", CompactProgressThickness);
    }

    /// <summary>
    /// The sung line inside the widened pill: it fills the space between the artwork and the visualizer, sits a
    /// little above the centre (the progress line runs underneath) and fades away as the notch opens.
    /// </summary>
    private void BindCompactLyrics(float compactHeight, float equalizerWidth)
    {
        double left = compactHeight - CompactInset + CompactProgressGap;
        double right = CompactEqualizerInset + equalizerWidth + CompactProgressGap;
        CompactLyrics.Width = Math.Max(CompactLyricsWidth - left - right, 0);

        var visual = VisualOf(CompactLyrics);
        visual.Properties.InsertVector3("Translation", new Vector3((float)(left - right) / 2, 0, 0));
        Bind("Clamp(1 - n.Expansion * 3, 0, 1) * Clamp(m.Present, 0, 1) * Clamp(m.Pill, 0, 1) * Clamp(m.PillLyrics, 0, 1)").Start(visual, "Opacity");
    }

    /// <summary>The compact line glides on the compositor, like the scrubber: one animation per player update.</summary>
    private void GlideCompactProgress(double progress, TimeSpan remaining, bool playing)
    {
        _state.StopAnimation("Progress");
        _state.InsertScalar("Progress", (float)progress);
        if (!playing || progress >= 1 || remaining <= TimeSpan.Zero)
        {
            return;
        }

        var linear = _compositor.CreateLinearEasingFunction();
        var glide = _compositor.CreateScalarKeyFrameAnimation();
        glide.InsertKeyFrame(0, (float)progress, linear);
        glide.InsertKeyFrame(1, 1, linear);
        glide.Duration = remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining;
        _state.StartAnimation("Progress", glide);
    }

    /// <summary>
    /// What the closed notch has from the player: music playing (the pill stays), a track paused (the notch shrinks to its
    /// handle after a while), or nothing at all (the handle at once). While the taskbar shows the track instead (see
    /// <see cref="PillShown"/>), it is elsewhere: the closed notch rests in its neutral state, the mascot in its nest.
    /// </summary>
    private void ShowPresence() => _context.SetPresence(
        !ViewModel.HasMedia ? NotchPresence.Nothing
        : !PillShown ? NotchPresence.Elsewhere
        : ViewModel.IsPlaying ? NotchPresence.Active : NotchPresence.Idle);

    /// <summary>
    /// The player on the closed pill (its artwork, progress, visualizer): unless the user wants it off the notch with the
    /// line on the taskbar (<see cref="AppSettings.HidePlayerInNotch"/>), and the taskbar shows the track (or is about to:
    /// it plays, with lyrics or while they are looked for, and the taskbar had room: resuming, or a new track, does not
    /// bring the pill back for a moment). Paused, the
    /// notch does as « Se cacher au repos » says: it rests (and the player stays away), or keeps the player in sight. A
    /// track without lyrics, or no room on the taskbar: the player is on the pill, the music always to be seen somewhere.
    /// </summary>
    private bool PillShown
    {
        get
        {
            if (!_settings.HidePlayerInNotch || _taskbarLyrics is not { } taskbar)
            {
                return true;
            }

            bool onTaskbar = taskbar.IsShown || (ViewModel.IsPlaying && (Lyrics.IsShown || (Lyrics.IsEnabled && Lyrics.IsLooking)) && taskbar.HasRoom);
            bool resting = !ViewModel.IsPlaying && _settings.HideWhenIdle;
            return !onTaskbar && !resting;
        }
    }

    private void ShowPill()
    {
        _pill.AnimateTo(PillShown ? 1 : 0, Motion.Fade);
        ShowPresence();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MediaViewModel.HasMedia):
                _present.AnimateTo(ViewModel.HasMedia ? 1 : 0, Motion.Fade);
                ShowPresence();
                break;

            case nameof(MediaViewModel.IsPlaying):
                _playing.AnimateTo(ViewModel.IsPlaying ? 1 : 0, Motion.Toggle);
                ShowPill();
                UpdateVisualizer();
                UpdateTimeline();
                break;

            case nameof(MediaViewModel.Timeline):
                UpdateTimeline();
                break;

            case nameof(MediaViewModel.Artwork):
                ShowArtwork(ViewModel.Artwork);
                break;

            case nameof(MediaViewModel.Accent):
                AnimateAccent(ViewModel.Accent);
                break;

            case nameof(MediaViewModel.TrackId):
                _swap.Snap(0);
                _swap.AnimateTo(1, Motion.Swap);
                break;
        }
    }

    private void ShowArtwork(ImageSource? artwork)
    {
        ArtworkBack.Source = ArtworkFront.Source;
        ArtworkFront.Source = artwork;

        // A new cover fades in over the old one; no cover fades the old one out to the placeholder.
        _state.InsertScalar("KeepBack", artwork is null ? 0 : 1);
        _artworkFade.Snap(0);
        _artworkFade.AnimateTo(1, Motion.Fade);
    }

    // ── Lyrics ──────────────────────────────────────────────────────────────────────────────────────────────

    private void OnLyricsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LyricsViewModel.Content):
                LyricsLines.SetLyrics(Lyrics.Content);
                CompactLyrics.SetLyrics(Lyrics.Content);
                break;

            case nameof(LyricsViewModel.IsShown):
            case nameof(LyricsViewModel.IsEnabled):
                ShowLyricsMode();
                ShowPill();
                break;

            case nameof(LyricsViewModel.IsLooking):
                ShowPill();
                break;

        }
    }

    /// <summary>Text size of the sung line in the closed pill (setting): line height and spacing follow, centred.</summary>
    private void ApplyCompactLyricsSize()
    {
        double size = _settings.CompactLyricsFontSize;
        CompactLyrics.LineFontSize = size;
        CompactLyrics.LineSpacing = Math.Round(size * 1.3);
        CompactLyrics.Height = Math.Ceiling(size * 1.45);
        CompactLyrics.Margin = new Thickness(0, (_motion.Layout.Collapsed.Height - CompactLyrics.Height) / 2, 0, 0);
    }

    /// <summary>The sung line goes on the closed pill: there are lyrics, and they are not wanted on the taskbar.</summary>
    private bool InPill => Lyrics.IsShown && _settings.LyricsPlace != AppSettings.LyricsInTaskbar;

    private void ShowLyricsMode()
    {
        _lyricsShown.AnimateTo(Lyrics.IsShown ? 1 : 0, Motion.Fade);
        _pillLyrics.AnimateTo(InPill ? 1 : 0, Motion.Fade);
        _context.RequestCompactWidth(InPill ? CompactLyricsWidth : null);

        // The lyrics button is lit while lyrics are on, dimmed when they are off.
        LyricsGlyph.Opacity = Lyrics.IsEnabled ? 1 : 0.45;
    }

    /// <summary>The sung line on the taskbar (setting): its window made, or put away.</summary>
    private void PlaceLyrics()
    {
        bool taskbar = _settings.LyricsPlace == AppSettings.LyricsInTaskbar;
        if (taskbar && _taskbarLyrics is null)
        {
            _taskbarLyrics = new TaskbarLyrics(Lyrics, ViewModel, _settings, DispatcherQueue);
            _taskbarLyrics.Clicked += (_, _) => _context.Show();
            _taskbarLyrics.ShownChanged += (_, _) => ShowPill();
        }
        else if (!taskbar && _taskbarLyrics is not null)
        {
            _taskbarLyrics.Dispose();
            _taskbarLyrics = null;
        }

        ShowPill();
    }

    /// <summary>The sung line shows in the pill as well as in the open notch: track it whenever the view lives.</summary>
    private void UpdateLyricsTracking() => Lyrics.IsTracking = IsLoaded;

    // ── Volume ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One step of 5 % per wheel notch (a touchpad's finer turns add up to it), read and changed right then: the
    /// volume is never watched in between.
    /// </summary>
    private void OnWheel(int delta)
    {
        // Turning back cancels what was gathered the other way.
        if (Math.Sign(delta) != Math.Sign(_wheel))
        {
            _wheel = 0;
        }

        _wheel += delta;
        int steps = _wheel / WheelNotch;
        if (steps == 0)
        {
            return;
        }

        _wheel -= steps * WheelNotch;
        var volume = VolumeState.Unavailable;
        for (int i = 0; i < Math.Abs(steps); i++)
        {
            volume = _audio.Step(Math.Sign(steps));
        }

        ShowVolume(volume);
    }

    /// <summary>
    /// The volume as it now is: closed, the pill shows it (the mascot as the cursor of its track, see
    /// NotchContext.ShowLevel); open, the notch's volume bubble does.
    /// </summary>
    private void ShowVolume(VolumeState volume)
    {
        if (!volume.IsAvailable)
        {
            _context.Announce(volume.Glyph, Loc.T("Aucune sortie audio"));
        }
        else if (!_context.IsExpanded)
        {
            _context.ShowLevel(volume.Glyph, volume.Text, volume.Level, volume.IsMuted);
        }
    }

    // ── Audio devices ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AirPods connected, a headset unplugged: the closed notch grows down for a moment, under its own row (artwork,
    /// lyrics, visualizer), to say so. A switch between built-in outputs says nothing; an open notch is not disturbed.
    /// </summary>
    private void OnDeviceSwitched(AudioSwitch change)
    {
        if (change.IsConnection)
        {
            _deviceCard.ShowConnected(change.To!, _audio.Read());
        }
        else if (change.IsDisconnection)
        {
            _deviceCard.ShowDisconnected(change.From!, change.To);
        }
        else
        {
            return;
        }

        _context.Present(_deviceCard, _deviceCard.CardSize, DeviceCardTime);
    }

    // ── Visualizer ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Listens to the audio and animates the bars only while something plays.</summary>
    private void UpdateVisualizer()
    {
        bool run = IsLoaded && ViewModel.IsPlaying;
        _spectrum.SetEnabled(run);
        if (run == _visualizerRunning)
        {
            return;
        }

        _visualizerRunning = run;
        _context.ShareBeat(run ? _state : null); // The mascot dances to the bass (m.Bass) while the music plays.
        if (run)
        {
            _frameClock.Restart();
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    /// <summary>Once per frame while playing: eases every bar toward the live spectrum.</summary>
    private void OnRendering(object? sender, object e)
    {
        double elapsed = Math.Min(_frameClock.Elapsed.TotalSeconds, 0.1);
        _frameClock.Restart();

        if (!_spectrum.TryGetLevels(_targets))
        {
            if (_spectrum.IsListening)
            {
                Array.Clear(_targets);
            }
            else
            {
                Imitate(_targets);
            }
        }

        double attack = 1 - Math.Exp(-elapsed / AttackSeconds);
        double release = 1 - Math.Exp(-elapsed / ReleaseSeconds);
        for (int i = 0; i < _levels.Length; i++)
        {
            float target = _targets[i];
            _levels[i] += (float)((target - _levels[i]) * (target > _levels[i] ? attack : release));
            _state.InsertScalar(_bandNames[i], _levels[i]);
        }

        // The small visualizer groups neighbouring bands; each bar shows the loudest of its group.
        int bands = _levels.Length;
        for (int bar = 0; bar < _bars.Length; bar++)
        {
            float loudest = 0;
            for (int band = bar * bands / _bars.Length; band < (bar + 1) * bands / _bars.Length; band++)
            {
                loudest = Math.Max(loudest, _levels[band]);
            }

            _state.InsertScalar(_barNames[bar], loudest);
        }

        // The glow breathes with the low end: kicks light it up, it fades back gently.
        int lowBands = Math.Max(1, bands / _bars.Length);
        double bass = 0;
        for (int band = 0; band < lowBands; band++)
        {
            bass += _levels[band] / lowBands;
        }

        _bass += (bass - _bass) * (bass > _bass ? attack : release * 0.5);
        _state.InsertScalar("Bass", (float)_bass);
    }

    /// <summary>No audio device to listen to: keep the bars alive with an organic motion.</summary>
    private static void Imitate(Span<float> levels)
    {
        double t = Environment.TickCount64 / 1000.0;
        for (int i = 0; i < levels.Length; i++)
        {
            levels[i] = (float)Math.Clamp(
                0.45 + 0.3 * Math.Sin(t * (5.1 + i * 1.7) + i) + 0.2 * Math.Sin(t * (8.3 - i * 0.9) + i * 2.1), 0, 1);
        }
    }

    // ── Timeline ────────────────────────────────────────────────────────────────────────────────────────────

    private void UpdateTimeline()
    {
        var timeline = ViewModel.Timeline;
        _timelineShown.AnimateTo(timeline.IsKnown ? 1 : 0, Motion.Fade);
        if (!timeline.IsKnown)
        {
            TimelineScrubber.ShowProgress(0, TimeSpan.Zero, isPlaying: false);
            GlideCompactProgress(0, TimeSpan.Zero, playing: false);
            UpdateClock();
            return;
        }

        bool playing = ViewModel.IsPlaying;
        var position = timeline.PositionAt(DateTimeOffset.Now, playing);
        double rate = Math.Max(timeline.PlaybackRate, 0.01);
        var remaining = TimeSpan.FromSeconds((timeline.Duration - position).TotalSeconds / rate);
        TimelineScrubber.ShowProgress(position / timeline.Duration, remaining, playing);
        GlideCompactProgress(position / timeline.Duration, remaining, playing);

        bool hours = timeline.Duration.TotalHours >= 1;
        ElapsedColumn.Width = new GridLength(hours ? 40 : 28);
        RemainingColumn.Width = new GridLength(hours ? 44 : 32);
        if (!TimelineScrubber.IsScrubbing)
        {
            ShowTimes(position, timeline.Duration);
        }

        UpdateClock();
    }

    /// <summary>The times tick only while they can be seen moving: notch open on the player, media playing.</summary>
    private void UpdateClock()
    {
        if (IsLoaded && _context.IsVisible && ViewModel.IsPlaying && ViewModel.Timeline.IsKnown)
        {
            UpdateTimes();
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }
    }

    private void UpdateTimes()
    {
        if (!TimelineScrubber.IsScrubbing)
        {
            var timeline = ViewModel.Timeline;
            ShowTimes(timeline.PositionAt(DateTimeOffset.Now, ViewModel.IsPlaying), timeline.Duration);
        }
    }

    private void ShowTimes(TimeSpan position, TimeSpan duration)
    {
        // Elapsed rounds down and remaining rounds up, so both always add up to the duration.
        var remaining = TimeSpan.FromSeconds(Math.Ceiling(Math.Max((duration - position).TotalSeconds, 0)));
        SetText(ElapsedText, FormatTime(position));
        SetText(RemainingText, "−" + FormatTime(remaining));
    }

    private void OnScrubbing(object? sender, double fraction)
    {
        // Dragging may wander outside of the notch: keep it open until the user lets go.
        _scrubHold ??= _context.HoldOpen();
        var duration = ViewModel.Timeline.Duration;
        ShowTimes(duration * fraction, duration);
    }

    private async void OnSeekRequested(object? sender, double fraction)
    {
        _scrubHold?.Dispose();
        _scrubHold = null;
        await ViewModel.SeekAsync(fraction);
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private static void SetText(TextBlock block, string text)
    {
        if (block.Text != text)
        {
            block.Text = text;
        }
    }

    // ── Colour ──────────────────────────────────────────────────────────────────────────────────────────────

    private void AnimateAccent(Color accent)
    {
        var storyboard = new Storyboard();
        storyboard.Children.Add(ColorTo(_accentBrush, accent));
        storyboard.Begin();
        SetGlowColor(accent, animate: true);

        var fade = _compositor.CreateColorKeyFrameAnimation();
        fade.InsertKeyFrame(1, accent, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1)));
        fade.Duration = TimeSpan.FromMilliseconds(700);
        _compactProgressBrush.StartAnimation("Color", fade);
    }

    /// <summary>
    /// The light takes the colour of the cover. Its strength follows the colour's saturation: a vivid cover
    /// bathes the notch in colour, a black and white one barely lifts the black.
    /// </summary>
    private void SetGlowColor(Color accent, bool animate)
    {
        int max = Math.Max(accent.R, Math.Max(accent.G, accent.B));
        int min = Math.Min(accent.R, Math.Min(accent.G, accent.B));
        double saturation = max == 0 ? 0 : (max - min) / (double)max;
        double alpha = 20 + 90 * Math.Clamp(saturation * 1.6, 0, 1);

        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1));
        for (int i = 0; i < _glowStops.Length; i++)
        {
            var color = Color.FromArgb((byte)Math.Round(alpha * GlowStopStrengths[i]), accent.R, accent.G, accent.B);
            if (!animate)
            {
                _glowStops[i].Color = color;
                continue;
            }

            var animation = _compositor.CreateColorKeyFrameAnimation();
            animation.InsertKeyFrame(1, color, ease);
            animation.Duration = TimeSpan.FromMilliseconds(700);
            _glowStops[i].StartAnimation("Color", animation);
        }
    }

    private static ColorAnimation ColorTo(DependencyObject target, Color to)
    {
        var animation = new ColorAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(700)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Color");
        return animation;
    }

    private CompositionExpression Bind(string expression) => _context.Bind(expression).With("m", _state);

    private static Visual VisualOf(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        return ElementCompositionPreview.GetElementVisual(element);
    }
}
