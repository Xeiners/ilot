using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MyNotch.Localization;
using MyNotch.Modules.Search;
using Windows.UI;

namespace MyNotch.Quick;

/// <summary>
/// The volume mixer, left of the quick controls (a right click on the volume): each app making sound on the default output,
/// its icon (a click mutes it), its name, its level as a slim slider in the mascot's colour, and the number. The notch's
/// black, a faint rim; no scroll bar past a handful of apps.
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

    /// <summary>Shows <paramref name="apps"/>, their icons drawn by <paramref name="icons"/>.</summary>
    public void Show(IReadOnlyList<MixerApp> apps, ShellIcons? icons)
    {
        _rows.Children.Clear();
        _sliders.Clear();
        foreach (var app in apps)
        {
            _rows.Children.Add(Row(app, icons));
        }

        _empty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _scroller.ChangeView(null, 0, null, disableAnimation: true);
        UpdateFades();
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
        var slider = new MixerSlider(_accent) { Margin = new Thickness(0, 4, 0, 0) };
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
        slider.Changed += (_, level) =>
        {
            app.Volume = level;
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
            double level = app.Volume;
            bool silent = app.IsMuted;
            slider.Show(level, silent);
            number.Text = (level * 100).ToString("0", CultureInfo.CurrentCulture);
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
/// jumps there and follows the pointer; the wheel moves it by 5 %.
/// </summary>
public sealed partial class MixerSlider : Grid
{
    private const double KnobSize = 12;
    private const double WheelStep = 0.05;

    private readonly Border _fill;
    private readonly Ellipse _knob;
    private readonly TranslateTransform _knobAt = new();
    private double _level;
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
        Children.Add(_fill);
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
            Changed?.Invoke(this, Math.Clamp(_level + Math.Sign(delta) * WheelStep, 0, 1));
        };
    }

    /// <summary>The user moved it: to this level (0 to 1).</summary>
    public event EventHandler<double>? Changed;

    /// <summary>Shows <paramref name="level"/> (0 to 1), dimmed when <paramref name="silent"/>.</summary>
    public void Show(double level, bool silent)
    {
        _level = Math.Clamp(level, 0, 1);
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
        _fill.Width = track * _level;
        _knobAt.X = track * _level;
    }

    private void Follow(PointerRoutedEventArgs e)
    {
        double track = Math.Max(ActualWidth - KnobSize, 1);
        double x = e.GetCurrentPoint(this).Position.X - KnobSize / 2;
        Changed?.Invoke(this, Math.Clamp(x / track, 0, 1));
    }
}
