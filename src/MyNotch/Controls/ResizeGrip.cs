using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.UI;

namespace MyNotch.Controls;

/// <summary>
/// A corner to pull, at the bottom of a rounded panel (the bubble under the notch): a short arc that follows the
/// corner, faint until the pointer is on it, and the resize cursor. The dragging itself is up to the host.
/// </summary>
public sealed partial class ResizeGrip : Grid
{
    public const double Size = 30;

    // An arc 6 px inside the panel's corner (radius 32), over its middle: from 15° to 75° of it (105° to 165° on the
    // left), screen angles, y down.
    private const double Radius = 26;

    private readonly Path _arc;

    public ResizeGrip()
    {
        Width = Size;
        Height = Size;
        VerticalAlignment = VerticalAlignment.Bottom;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // Takes the pointer all over, not just on the arc.
        _arc = new Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0.28,
            IsHitTestVisible = false,
        };
        Children.Add(_arc);
        PointerEntered += (_, _) => Light(true);
        PointerExited += (_, _) => Light(false);
        IsLeft = false;
    }

    /// <summary>The bottom-left corner (else the bottom-right one).</summary>
    public bool IsLeft
    {
        get => HorizontalAlignment == HorizontalAlignment.Left;
        set
        {
            HorizontalAlignment = value ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            _arc.Data = value ? Arc(new(25.3, 23.1), new(6.9, 4.7)) : Arc(new(23.1, 4.7), new(4.7, 23.1));
            ProtectedCursor = InputSystemCursor.Create(value ? InputSystemCursorShape.SizeNortheastSouthwest : InputSystemCursorShape.SizeNorthwestSoutheast);
        }
    }

    /// <summary>Bright while pointed at or pulled.</summary>
    public bool IsActive { get; set; }

    public void Light(bool on) => _arc.Opacity = on || IsActive ? 0.85 : 0.28;

    private static PathGeometry Arc(Windows.Foundation.Point from, Windows.Foundation.Point to)
    {
        var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment { Point = to, Size = new(Radius, Radius), SweepDirection = SweepDirection.Clockwise });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
