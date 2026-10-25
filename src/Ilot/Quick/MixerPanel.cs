using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Ilot.Localization;
using Ilot.Modules.Search;
using Windows.UI;

namespace Ilot.Quick;

/// <summary>
/// The volume mixer, left of the quick controls (a right click on the volume): each app making sound on the default output,
/// its icon (a click mutes it), its name, its level as a slim slider in the mascot's colour, and the number. The notch's
/// black, a faint rim; no scroll bar past a handful of apps. At its bottom, the booster's switch (see <see cref="SoundBooster"/>):
/// on, the apps' sliders go up to 200 %, a mark at 100 %.
/// </summary>
public sealed partial class MixerPanel : Grid
{
    /// <summary>Its width, when there is room for it left of the quick controls (the notch makes it narrower, down to <see cref="SmallestWidth"/>).</summary>
    public const double PanelWidth = 272;

    public const double SmallestWidth = 220;

    private static readonly SolidColorBrush Text = new(Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Faint = new(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));

    private readonly StackPanel _rows;
    private readonly ScrollViewer _scroller;
    private readonly Rectangle _topFade;
    private readonly Rectangle _bottomFade;
    private readonly TextBlock _empty;
    private readonly List<MixerSlider> _sliders = [];
    private readonly Border _boostHost = new() { Margin = new Thickness(0, 2, 0, 0) };
    private SoundBooster? _booster;
    private IReadOnlyList<MixerApp> _apps = [];
    private ShellIcons? _icons;
    private Color _accent = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    public MixerPanel()
    {
        var rim = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        rim.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF) });
        rim.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) });
        var frame = new Border
        {
            Padding = new Thickness(16, 13, 16, 14),
            CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            BorderBrush = rim,
        };
        Children.Add(frame);

        _rows = new StackPanel { Spacing = 12 };
        _empty = new TextBlock
        {
            Text = Loc.T("Aucune application ne joue de son."),
            FontSize = 12.5,
            Foreground = Faint,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel { Spacing = 11 };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("Mélangeur de volume"),
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Faint,
        });
        _scroller = new ScrollViewer
        {
            MaxHeight = 380,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _rows,
        };
        _topFade = Fade(top: true);
        _bottomFade = Fade(top: false);
        var list = new Grid();
        list.Children.Add(_scroller);
        list.Children.Add(_topFade);
        list.Children.Add(_bottomFade);
        content.Children.Add(list);
        _scroller.ViewChanged += (_, _) => UpdateFades();
        _scroller.SizeChanged += (_, _) => UpdateFades();
        content.Children.Add(_empty);
        content.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)) });
        content.Children.Add(_boostHost);
        frame.Child = content;

        // Its own: the notch neither pins nor closes for a click in it, nor shows its menu.
        Tapped += (_, e) => e.Handled = true;
        DoubleTapped += (_, e) => e.Handled = true;
        ContextRequested += (_, e) => e.Handled = true;
        PointerWheelChanged += (_, e) => e.Handled = true;
    }

    /// <summary>An end of the list fading into the black, where there are more apps past it (no scroll bar).</summary>
    private static Rectangle Fade(bool top)
    {
        var fill = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        fill.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(top ? (byte)0xFF : (byte)0, 0, 0, 0) });
        fill.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(top ? (byte)0 : (byte)0xFF, 0, 0, 0) });
        return new Rectangle
        {
            Height = 26,
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Opacity = 0,
            Fill = fill,
        };
    }

    private void UpdateFades()
    {
        _topFade.Opacity = _scroller.VerticalOffset > 1 ? 1 : 0;
        _bottomFade.Opacity = _scroller.VerticalOffset < _scroller.ScrollableHeight - 1 ? 1 : 0;
    }

    /// <summary>The sliders take <paramref name="accent"/> (the mascot's colour, see MascotColor.Accent).</summary>
    public void Tint(Color accent)
    {
        _accent = accent;
        foreach (var slider in _sliders)
        {
            slider.Tint(accent);
        }
    }

    /// <summary>The booster, its switch at the bottom.</summary>
    public void Attach(SoundBooster booster)
    {
        _booster = booster;
        ShowBooster();
    }

    /// <summary>
    /// The booster's row: its switch, and what it does (« Jusqu'à 200 % par application »). Before Windows 11 (no capture of
    /// an app's sound), it says so.
    /// </summary>
    private void ShowBooster()
    {
        if (_booster is not { } booster)
        {
            return;
        }

        var row = new Grid { ColumnSpacing = 11 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new FontIcon { Glyph = "\uE995", FontSize = 15, Foreground = Text, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(glyph);
        var title = new TextBlock
        {
            Text = Loc.T("Booster"),
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12.5,
            Foreground = Text,
        };
        var detail = new TextBlock { FontSize = 11.5, Foreground = Faint, TextTrimming = TextTrimming.CharacterEllipsis };
        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        middle.Children.Add(title);
        middle.Children.Add(detail);
        Grid.SetColumn(middle, 1);
        row.Children.Add(middle);
        _boostHost.Child = row;

        if (!SoundBooster.IsSupported)
        {
            detail.Text = Loc.T("Il faut Windows 11.");
            title.Opacity = glyph.Opacity = 0.7;
            return;
        }

        var toggle = new ToggleSwitch
        {
            IsOn = booster.IsOn,
            MinWidth = 0,
            OnContent = "",
            OffContent = "",
            RequestedTheme = ElementTheme.Dark,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, -8, 0),
        };
        AutomationProperties.SetName(toggle, Loc.T("Booster"));
        Grid.SetColumn(toggle, 2);
        row.Children.Add(toggle);

        Refresh();
        toggle.Toggled += (_, _) =>
        {
            booster.IsOn = toggle.IsOn;
            Refresh();
            ShowRows(); // Their sliders go up to 200 %, or back to 100 %.
        };

        void Refresh()
        {
            detail.Text = booster.IsOn ? Loc.T("Jusqu'à 200 % par application") : Loc.T("Désactivé : 100 % au plus");
            title.Opacity = glyph.Opacity = booster.IsOn ? 1 : 0.7;
        }
    }

    /// <summary>Shows <paramref name="apps"/>, their icons drawn by <paramref name="icons"/>.</summary>
    public void Show(IReadOnlyList<MixerApp> apps, ShellIcons? icons)
    {
        _apps = apps;
        _icons = icons;
        ShowRows();
        _empty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _scroller.ChangeView(null, 0, null, disableAnimation: true);
        UpdateFades();
        ShowBooster();
    }

    private void ShowRows()
    {
        _rows.Children.Clear();
        _sliders.Clear();
        foreach (var app in _apps)
        {
            _rows.Children.Add(Row(app, _icons));
        }
    }

    private Grid Row(MixerApp app, ShellIcons? icons)
    {
        var row = new Grid { ColumnSpacing = 11 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });

        // The icon: a click mutes the app, or gives it its sound back.
        var picture = new Grid { Width = 30, Height = 30 };
        var glyph = new FontIcon { Glyph = app.IsSystem ? "" : "", FontSize = 15, Foreground = Text };
        var image = new Image { Width = 24, Height = 24, Visibility = Visibility.Collapsed };
        var muted = new FontIcon { Glyph = "", FontSize = 11, Foreground = Text, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        picture.Children.Add(glyph);
        picture.Children.Add(image);
        picture.Children.Add(muted);
        var mute = new Button
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-2, 0, -2, 0),
            Content = picture,
        };
        AutomationProperties.SetName(mute, Loc.F("Couper le son de {0}", app.Name));
        if (!app.IsSystem && app.Program is { } program && icons is not null)
        {
            _ = ShowIcon(icons, program);
        }

        var name = new TextBlock
        {
            Text = app.Name,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12.5,
            Foreground = Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Its level: Windows' own, or the booster's (past 100 %, or about to be given back).
        string? key = app.IsSystem ? null : app.Key;
        bool boostable = key is not null && _booster is { IsOn: true } && SoundBooster.IsSupported;
        double level = key is not null && _booster?.Level(key) is { } held ? held : app.Volume;
        var slider = new MixerSlider(_accent) { Margin = new Thickness(0, 4, 0, 0), Most = boostable ? SoundBooster.Most : 1 };
        _sliders.Add(slider);
        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        middle.Children.Add(name);
        middle.Children.Add(slider);

        var number = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 11.5,
            Foreground = Faint,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1),
        };

        Grid.SetColumn(middle, 1);
        Grid.SetColumn(number, 2);
        row.Children.Add(mute);
        row.Children.Add(middle);
        row.Children.Add(number);

        Refresh();
        slider.Changed += (_, value) =>
        {
            level = boostable && Math.Abs(value - 1) < 0.04 ? 1 : value; // 100 %: a notch, easy to come back to.
            if (key is not null && _booster is { } booster && (level > 1 || booster.Holds(key)))
            {
                booster.Set(key, level);
            }
            else
            {
                app.Volume = level;
            }

            if (app.IsMuted && level > 0)
            {
                app.IsMuted = false; // Turned up: heard again, as Windows' mixer does.
            }

            Refresh();
        };
        mute.Click += (_, _) =>
        {
            app.IsMuted = !app.IsMuted;
            Refresh();
        };
        return row;

        void Refresh()
        {
            bool silent = app.IsMuted;
            slider.Show(level, silent);
            number.Text = (level * 100).ToString("0", CultureInfo.CurrentCulture);
            number.Foreground = level > 1 ? new SolidColorBrush(_accent) : Faint; // Boosted.
            muted.Visibility = silent ? Visibility.Visible : Visibility.Collapsed;
            glyph.Opacity = image.Opacity = silent ? 0.35 : 1;
            name.Opacity = silent ? 0.5 : 1;
        }

        async Task ShowIcon(ShellIcons shell, string program)
        {
            if (await shell.GetAsync(program, program.ToLowerInvariant()) is { } source)
            {
                image.Source = source;
                image.Visibility = Visibility.Visible;
                glyph.Visibility = Visibility.Collapsed;
            }
        }
    }
}

/// <summary>
/// A slim slider for the mixer: a faint track, its level in the accent, a round knob. Pressed anywhere on it, the level
/// jumps there and follows the pointer; the wheel moves it by 5 %. Its levels go from 0 to <see cref="Most"/> (1; 2 for an
/// app the booster can make louder, a mark at 1).
/// </summary>
public sealed partial class MixerSlider : Grid
{
    private const double KnobSize = 12;
    private const double WheelStep = 0.05;

    private readonly Border _fill;
    private readonly Border _mark;
    private readonly TranslateTransform _markAt = new();
    private readonly Ellipse _knob;
    private readonly TranslateTransform _knobAt = new();
    private double _level;
    private double _most = 1;
    private bool _pressed;

    public MixerSlider(Color accent)
    {
        Height = 16;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // Takes the pointer all over.
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        Children.Add(new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(KnobSize / 2, 0, KnobSize / 2, 0),
        });
        _fill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(KnobSize / 2, 0, 0, 0),
        };
        _knob = new Ellipse
        {
            Width = KnobSize,
            Height = KnobSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _knobAt,
        };
        _mark = new Border
        {
            Width = 2,
            Height = 8,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _markAt,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        Children.Add(_fill);
        Children.Add(_mark);
        Children.Add(_knob);
        Tint(accent);

        SizeChanged += (_, _) => Place();
        PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _pressed = CapturePointer(e.Pointer);
            Follow(e);
        };
        PointerMoved += (_, e) =>
        {
            if (_pressed)
            {
                Follow(e);
            }
        };
        PointerReleased += (_, e) =>
        {
            _pressed = false;
            ReleasePointerCapture(e.Pointer);
        };
        PointerCaptureLost += (_, _) => _pressed = false;
        PointerWheelChanged += (_, e) =>
        {
            e.Handled = true;
            int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            Changed?.Invoke(this, Math.Clamp(_level + Math.Sign(delta) * WheelStep, 0, _most));
        };
    }

    /// <summary>Its highest level: 1, or more (a mark at 1 then).</summary>
    public double Most
    {
        get => _most;
        set
        {
            _most = Math.Max(value, 1);
            _mark.Visibility = _most > 1 ? Visibility.Visible : Visibility.Collapsed;
            Place();
        }
    }

    /// <summary>The user moved it: to this level (0 to <see cref="Most"/>).</summary>
    public event EventHandler<double>? Changed;

    /// <summary>Shows <paramref name="level"/> (0 to <see cref="Most"/>), dimmed when <paramref name="silent"/>.</summary>
    public void Show(double level, bool silent)
    {
        _level = Math.Clamp(level, 0, _most);
        Opacity = silent ? 0.45 : 1;
        Place();
    }

    public void Tint(Color accent)
    {
        _fill.Background = new SolidColorBrush(accent);
        _knob.Fill = new SolidColorBrush(accent);
    }

    private void Place()
    {
        double track = Math.Max(ActualWidth - KnobSize, 0);
        _fill.Width = track * _level / _most;
        _knobAt.X = track * _level / _most;
        _markAt.X = KnobSize / 2 + track / _most - 1;
    }

    private void Follow(PointerRoutedEventArgs e)
    {
        double track = Math.Max(ActualWidth - KnobSize, 1);
        double x = e.GetCurrentPoint(this).Position.X - KnobSize / 2;
        Changed?.Invoke(this, Math.Clamp(x / track, 0, 1) * _most);
    }
}
