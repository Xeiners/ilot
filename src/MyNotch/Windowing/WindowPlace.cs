using System.Globalization;
using Windows.Graphics;

namespace MyNotch.Windowing;

/// <summary>
/// Where a detached window was last (« x,y,width,height » in the screen's pixels), and whether it stays above the other
/// windows, as the settings keep them.
/// </summary>
public sealed class WindowPlace(Func<string> bounds, Action<string> keepBounds, Func<bool> onTop, Action<bool> keepOnTop)
{
    public bool OnTop
    {
        get => onTop();
        set => keepOnTop(value);
    }

    /// <summary>Where it was last; <c>null</c> if never kept (or unreadable).</summary>
    public RectInt32? Bounds
    {
        get
        {
            string[] parts = bounds().Split(',');
            if (parts.Length != 4)
            {
                return null;
            }

            var numbers = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i]))
                {
                    return null;
                }
            }

            return numbers[2] > 0 && numbers[3] > 0 ? new RectInt32(numbers[0], numbers[1], numbers[2], numbers[3]) : null;
        }

        set => keepBounds(value is { } rect
            ? string.Join(',', new[] { rect.X, rect.Y, rect.Width, rect.Height }.Select(each => each.ToString(CultureInfo.InvariantCulture)))
            : "");
    }
}
