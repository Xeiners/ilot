using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ilot.Controls;

/// <summary>
/// A handle to hold, at the top of a detached window (see DetachedWindow): a short bar, faint until the pointer is on it,
/// and the move cursor, over a wider area that takes the pointer. The moving itself is up to the host.
/// </summary>
public sealed partial class MoveHandle : Grid
{
    private readonly Border _bar;

    public MoveHandle()
    {
        Width = 168;
        Height = 20;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // Takes the pointer all over, not just on the bar.
        _bar = new Border
        {
            Width = 44,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            Opacity = 0.28,
            IsHitTestVisible = false,
        };
        Children.Add(_bar);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        PointerEntered += (_, _) => Light(true);
        PointerExited += (_, _) => Light(false);
    }

    /// <summary>Bright while pointed at or held.</summary>
    public bool IsActive { get; set; }

    public void Light(bool on) => _bar.Opacity = on || IsActive ? 0.75 : 0.28;
}
