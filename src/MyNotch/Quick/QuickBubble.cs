using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyNotch.Composition;
using MyNotch.Notch;
using Windows.UI;

namespace MyNotch.Quick;

/// <summary>
/// A quick control in its little bubble beside the open notch (the volume, the brightness): its glyph, ringed by its
/// level with a knob at its end; hovered or being changed, the number instead. The ring is a dial: pressed and turned,
/// the level follows the pointer round (the top is nothing, clockwise up to full). The wheel moves it by 5 %; in the
/// middle, dragging up or down moves it like a slider, and a click is the control's own (mute).
/// </summary>
public sealed partial class QuickBubble : Grid
{
    /// <summary>As big as its pod (see NotchLayout.QuickSize).</summary>
    public static double Size { get; } = NotchLayout.Default.QuickSize;

    private const double Ring = 21;
    private const double RingThickness = 3.5;
    private const double Knob = 4.5;

    /// <summary>Pressed this close to the ring (pixels, either side), it is the dial; nearer the middle, the glyph.</summary>
    private const double RingReach = 9;
    private const int WheelNotch = 120;
    private const double WheelStep = 0.05;

    /// <summary>A drag this long (pixels) goes from nothing to full.</summary>
    private const double DragReach = 140;

    private static readonly Spring Fill = new(0.30, 0.86);
    private static readonly Spring Swap = new(0.28, 0.9);
    private static readonly TimeSpan NumberFor = TimeSpan.FromSeconds(1.2);

    private readonly CompositionPropertySet _state;
    private readonly SpringChannel _level;
    private readonly SpringChannel _number;
    private readonly FontIcon _glyph;
    private CompositionColorBrush? _levelBrush;
    private CompositionColorBrush? _knobBrush;
    private readonly TextBlock _value;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _numberTimer;
    private bool _hovered;
    private bool _pressed;
    private bool _dragging;
    private bool _turning;
    private double _turned;
    private double _shown;
    private double _pressY;
    private double _lastY;
    private int _wheel;

    public QuickBubble()
    {
        Width = Size;
        Height = Size;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // Takes the pointer all over.
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _state = compositor.CreatePropertySet();
        _level = new SpringChannel(_state, "Level", 0);
        _number = new SpringChannel(_state, "Number", 0);
        ElementCompositionPreview.SetElementChildVisual(this, CreateRing(compositor));

        _glyph = new FontIcon
        {
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
        };
        _value = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
        };
        Children.Add(_glyph);
        Children.Add(_value);
        Bind(compositor, "1 - Clamp(s.Number, 0, 1)").Start(ElementCompositionPreview.GetElementVisual(_glyph), "Opacity");
        Bind(compositor, "Clamp(s.Number, 0, 1)").Start(ElementCompositionPreview.GetElementVisual(_value), "Opacity");

        _numberTimer = DispatcherQueue.CreateTimer();
        _numberTimer.IsRepeating = false;
        _numberTimer.Interval = NumberFor;
        _numberTimer.Tick += (_, _) => ShowNumber(_hovered || _pressed);

        PointerEntered += (_, _) =>
        {
            _hovered = true;
            ShowNumber(true);
        };
        PointerExited += (_, _) =>
        {
            _hovered = false;
            if (!_pressed)
            {
                ShowNumber(false);
            }
        };
        PointerWheelChanged += OnWheel;
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => _pressed = false;
        Tapped += (_, e) => e.Handled = true; // Its own: the notch neither pins nor closes for it.
        DoubleTapped += (_, e) => e.Handled = true;
    }

    /// <summary>The user moves it: by this much (a fraction of the whole, up is more).</summary>
    public event EventHandler<double>? Adjusted;

    /// <summary>A click (not a drag).</summary>
    public event EventHandler? Clicked;

    /// <summary>Held by the pointer (turned, dragged): it shows what the finger says, not what comes back meanwhile.</summary>
    public bool IsHeld => _pressed;

    /// <summary>
    /// Shows <paramref name="level"/> (0 to 1) with <paramref name="glyph"/> (Segoe Fluent Icons): springing to it, or
    /// right under the finger while held (a spring there would trail it, frame after frame).
    /// </summary>
    public void Show(double level, string glyph)
    {
        level = Math.Clamp(level, 0, 1);
        _shown = level;
        if (_pressed)
        {
            _level.Snap(level);
        }
        else
        {
            _level.AnimateTo(level, Fill);
        }

        _glyph.Glyph = glyph;
        _value.Text = (level * 100).ToString("0", CultureInfo.CurrentCulture);
    }

    /// <summary>Its level and knob take <paramref name="accent"/> (the mascot's colour, see MascotColor.Accent), fading to it.</summary>
    public void Tint(Color accent, bool animate = true)
    {
        foreach (var brush in new[] { _levelBrush, _knobBrush })
        {
            if (brush is null)
            {
                continue;
            }

            if (!animate)
            {
                brush.Color = accent;
                continue;
            }

            var fade = brush.Compositor.CreateColorKeyFrameAnimation();
            fade.InsertKeyFrame(1, accent);
            fade.Duration = TimeSpan.FromMilliseconds(420);
            brush.StartAnimation("Color", fade);
        }
    }

    /// <summary>The ring: a faint track, and its level from the top, clockwise.</summary>
    private ShapeVisual CreateRing(Compositor compositor)
    {
        var ring = compositor.CreateShapeVisual();
        ring.Size = new Vector2((float)Size);
        var center = new Vector2((float)Size / 2);

        var track = compositor.CreateEllipseGeometry();
        track.Center = center;
        track.Radius = new Vector2((float)Ring);
        var trackShape = compositor.CreateSpriteShape(track);
        trackShape.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        trackShape.StrokeThickness = (float)RingThickness;
        ring.Shapes.Add(trackShape);

        var level = compositor.CreateEllipseGeometry();
        level.Center = center;
        level.Radius = new Vector2((float)Ring);
        // A composition ellipse's path starts at its top and runs clockwise (measured on screen): its first part is the arc
        // to the knob, as it is.
        Bind(compositor, "Clamp(s.Level, 0, 1)").Start(level, "TrimEnd");
        var levelShape = compositor.CreateSpriteShape(level);
        _levelBrush = compositor.CreateColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        levelShape.StrokeBrush = _levelBrush;
        levelShape.StrokeThickness = (float)RingThickness;
        levelShape.StrokeStartCap = CompositionStrokeCap.Round;
        levelShape.StrokeEndCap = CompositionStrokeCap.Round;
        ring.Shapes.Add(levelShape);

        // The knob at the level's end, what the finger takes to turn the dial.
        var knob = compositor.CreateEllipseGeometry();
        knob.Radius = new Vector2((float)Knob);
        Bind(compositor, "Vector2(c + r * Sin(Clamp(s.Level, 0, 1) * 6.2831853), c - r * Cos(Clamp(s.Level, 0, 1) * 6.2831853))")
            .With("c", Size / 2).With("r", Ring)
            .Start(knob, "Center");
        var knobShape = compositor.CreateSpriteShape(knob);
        _knobBrush = compositor.CreateColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        knobShape.FillBrush = _knobBrush;
        ring.Shapes.Add(knobShape);
        return ring;
    }

    private CompositionExpression Bind(Compositor compositor, string expression) =>
        new CompositionExpression(compositor, expression).With("s", _state);

    /// <summary>The number instead of the glyph (hovered, being changed), or back.</summary>
    private void ShowNumber(bool number) => _number.AnimateTo(number ? 1 : 0, Swap);

    /// <summary>Being changed: the number, for a moment after the last change.</summary>
    private void Changed(double delta)
    {
        ShowNumber(true);
        _numberTimer.Stop();
        _numberTimer.Start();
        Adjusted?.Invoke(this, delta);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true; // Not the notch's (the media player's volume): this control's.
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (Math.Sign(delta) != Math.Sign(_wheel))
        {
            _wheel = 0;
        }

        _wheel += delta;
        int steps = _wheel / WheelNotch;
        if (steps != 0)
        {
            _wheel -= steps * WheelNotch;
            Changed(steps * WheelStep);
        }
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
        {
            return; // A right click is its menu's (the volume's mixer), not a click: it neither mutes nor turns.
        }

        e.Handled = true;
        _pressed = CapturePointer(e.Pointer);
        _dragging = false;
        var at = point.Position;
        _pressY = _lastY = at.Y;

        // On the ring: the dial, the level where the pointer is at once.
        double distance = Math.Sqrt(Math.Pow(at.X - Size / 2, 2) + Math.Pow(at.Y - Size / 2, 2));
        _turning = Math.Abs(distance - Ring) <= RingReach;
        if (_turning)
        {
            _turned = Angle(at);
            Changed(_turned - _shown);
        }
    }

    /// <summary>Where <paramref name="at"/> is round the ring: 0 at the top, clockwise to 1.</summary>
    private static double Angle(Windows.Foundation.Point at)
    {
        double turn = Math.Atan2(at.X - Size / 2, Size / 2 - at.Y) / (2 * Math.PI);
        return turn < 0 ? turn + 1 : turn;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        if (_turning)
        {
            // Round the ring; across the top it stops at nothing or full, rather than jumping to the other end.
            double angle = Angle(e.GetCurrentPoint(this).Position);
            double level = Math.Abs(angle - _turned) > 0.5 ? (_turned > 0.5 ? 1 : 0) : angle;
            if (level != _turned)
            {
                _turned = level;
                Changed(level - _shown);
            }

            return;
        }

        double y = e.GetCurrentPoint(this).Position.Y;
        if (!_dragging && Math.Abs(y - _pressY) > 3)
        {
            _dragging = true;
        }

        if (_dragging)
        {
            Changed((_lastY - y) / DragReach); // Up is more.
        }

        _lastY = y;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        e.Handled = true;
        _pressed = false;
        ReleasePointerCapture(e.Pointer);
        if (_turning)
        {
            _turning = false;
            return;
        }

        if (!_dragging)
        {
            Clicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
