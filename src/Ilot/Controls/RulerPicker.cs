using System.Globalization;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Ilot.Composition;
using Windows.UI;

namespace Ilot.Controls;

/// <summary>
/// A value picked on a ruler (a length of time…): graduations sliding under a fixed mark in the middle, a short one at
/// each step, a long one with its number every so many, fading out at both ends. Dragged sideways (it follows the
/// finger, then settles on the nearest step with a spring), the wheel moves it a step, a click on a graduation goes
/// there.
/// </summary>
/// <remarks>The graduations are laid out once; only their row moves, on the compositor.</remarks>
public sealed partial class RulerPicker : Grid
{
    private const double Spacing = 13;
    private const double MinorHeight = 9;
    private const double MajorHeight = 17;
    private static readonly Spring Settle = new(0.32, 0.86);

    private readonly double _minimum;
    private readonly double _maximum;
    private readonly double _step;
    private readonly Canvas _ticks = new() { IsHitTestVisible = false };
    private readonly Border _mark;
    private readonly CompositionPropertySet _state;
    private readonly SpringChannel _slide;
    private double _value;
    private bool _dragging;
    private double _dragFrom;
    private double _dragValue;

    /// <param name="major">A long graduation, with its number, every this much (a multiple of <paramref name="step"/>).</param>
    public RulerPicker(double minimum, double maximum, double step, double major, double value, Color accent)
    {
        _minimum = minimum;
        _maximum = maximum;
        _step = step;
        _value = Snap(value);
        Height = 46;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // Takes the pointer all over.
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        // The graduations, in a row from the minimum (its translation brings the value under the mark).
        var faint = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));
        var bright = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        int count = (int)Math.Round((maximum - minimum) / step);
        for (int i = 0; i <= count; i++)
        {
            double at = minimum + i * step;
            bool isMajor = Math.Abs(Math.IEEERemainder(at, major)) < step / 2;
            var tick = new Rectangle
            {
                Width = isMajor ? 2 : 1.5,
                Height = isMajor ? MajorHeight : MinorHeight,
                RadiusX = 1,
                RadiusY = 1,
                Fill = isMajor ? bright : faint,
            };
            Canvas.SetLeft(tick, i * Spacing - tick.Width / 2);
            Canvas.SetTop(tick, 4 + (MajorHeight - tick.Height) / 2);
            _ticks.Children.Add(tick);
            if (isMajor)
            {
                var label = new TextBlock
                {
                    Text = at.ToString("0", CultureInfo.CurrentCulture),
                    FontSize = 10,
                    Foreground = bright,
                    Width = 30,
                    TextAlignment = TextAlignment.Center,
                };
                Canvas.SetLeft(label, i * Spacing - 15);
                Canvas.SetTop(label, MajorHeight + 8);
                _ticks.Children.Add(label);
            }
        }

        Children.Add(_ticks);

        // The ends fade into the black (the notch's), and the mark stays in the middle, in the accent.
        Children.Add(Fade(left: true));
        Children.Add(Fade(left: false));
        _mark = new Border
        {
            Width = 3,
            Height = MajorHeight + 8,
            CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(accent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        Children.Add(_mark);

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _state = compositor.CreatePropertySet();
        _slide = new SpringChannel(_state, "Along", Along(_value));
        _state.InsertScalar("Middle", 0);
        ElementCompositionPreview.SetIsTranslationEnabled(_ticks, true);
        new CompositionExpression(compositor, "Vector3(r.Middle - r.Along, 0, 0)").With("r", _state)
            .Start(ElementCompositionPreview.GetElementVisual(_ticks), "Translation");
        SizeChanged += (_, e) =>
        {
            _state.InsertScalar("Middle", (float)(e.NewSize.Width / 2));
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) }; // The row, cut at its ends.
        };

        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => EndDrag();
        PointerWheelChanged += (_, e) =>
        {
            e.Handled = true;
            int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            if (delta != 0)
            {
                Set(_value + Math.Sign(delta) * _step, animate: true);
            }
        };
    }

    /// <summary>The value picked (on a step).</summary>
    public double Value => _value;

    /// <summary>The user moved it to another step.</summary>
    public event EventHandler<double>? ValueChanged;

    /// <summary>Shows <paramref name="value"/> (changed elsewhere), without telling.</summary>
    public void Show(double value)
    {
        if (_dragging)
        {
            return;
        }

        _value = Snap(value);
        _slide.AnimateTo(Along(_value), Settle);
    }

    /// <summary>The mark takes <paramref name="accent"/>.</summary>
    public void Tint(Color accent) => _mark.Background = new SolidColorBrush(accent);

    private double Along(double value) => (value - _minimum) / _step * Spacing;

    private double Snap(double value) =>
        Math.Clamp(_minimum + Math.Round((value - _minimum) / _step) * _step, _minimum, _maximum);

    private void Set(double value, bool animate)
    {
        double snapped = Snap(value);
        if (animate)
        {
            _slide.AnimateTo(Along(snapped), Settle);
        }

        if (snapped != _value)
        {
            _value = snapped;
            ValueChanged?.Invoke(this, snapped);
        }
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        _dragging = CapturePointer(e.Pointer);
        _dragFrom = e.GetCurrentPoint(this).Position.X;
        _dragValue = _value;
    }

    /// <summary>Dragged: the row follows the finger exactly (no spring trailing it), the value the nearest step.</summary>
    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        double moved = e.GetCurrentPoint(this).Position.X - _dragFrom;
        double along = Math.Clamp(Along(_dragValue) - moved, 0, Along(_maximum));
        _slide.Snap(along);
        Set(_minimum + along / Spacing * _step, animate: false);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        double moved = e.GetCurrentPoint(this).Position.X - _dragFrom;
        ReleasePointerCapture(e.Pointer);
        if (Math.Abs(moved) < 3)
        {
            // A click: to the graduation clicked.
            double clicked = e.GetCurrentPoint(this).Position.X - ActualWidth / 2;
            Set(_value + clicked / Spacing * _step, animate: true);
        }

        EndDrag();
    }

    /// <summary>Let go: the row settles on the step picked.</summary>
    private void EndDrag()
    {
        if (_dragging)
        {
            _dragging = false;
            _slide.AnimateTo(Along(_value), Settle);
        }
    }

    private static Rectangle Fade(bool left)
    {
        var fill = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        fill.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(left ? (byte)0xFF : (byte)0, 0, 0, 0) });
        fill.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(left ? (byte)0 : (byte)0xFF, 0, 0, 0) });
        return new Rectangle
        {
            Width = 46,
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            IsHitTestVisible = false,
            Fill = fill,
        };
    }
}
