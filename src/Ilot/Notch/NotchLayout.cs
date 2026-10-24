using Windows.Foundation;

namespace Ilot.Notch;

public enum NotchStyle
{
    /// <summary>Flush with the top edge of the screen, melted into it by concave shoulders (MacBook-like notch).</summary>
    Notch,

    /// <summary>A floating capsule just below the top edge (iPhone Dynamic Island).</summary>
    Island,
}

/// <summary>Size and bottom corner radius of the notch, in logical pixels.</summary>
public readonly record struct NotchShape(double Width, double Height, double Radius);

/// <summary>
/// Geometry of the notch. Every value is in logical (DPI-independent) pixels.
/// </summary>
public sealed record NotchLayout
{
    public static NotchLayout Default { get; } = new();

    public NotchStyle Style { get; init; } = NotchStyle.Notch;

    public NotchShape Collapsed { get; init; } = new(200, 36, 19);

    public NotchShape Expanded { get; init; } = new(660, 158, 36);

    /// <summary>
    /// Largest open notch a module may ask for, and the room for a bubble under it (the note sheet…). The
    /// transparent window is sized for it; only the notch itself receives the mouse, so the extra room costs nothing.
    /// </summary>
    public NotchShape ExpandedMax { get; init; } = new(1200, 880, 36);

    /// <summary>Space between the open notch and a bubble hanging under it (see <see cref="NotchContext.ShowBubble"/>).</summary>
    public double BubbleGap { get; init; } = 14;

    /// <summary>
    /// The closed notch, under the pointer: a small tab at the top of the screen with a grabber line in
    /// it, a drag handle, while the pill gives way. Only its spot opens the whole notch.
    /// </summary>
    public NotchShape Handle { get; init; } = new(76, 9, 4.5);

    /// <summary>How far around the handle the pointer already counts as on it: the tab is small, the target is not.</summary>
    public double HandleReach { get; init; } = 6;

    /// <summary>How much wider the notch swells while the pointer rests on it, just before it opens.</summary>
    public double PrimeGrowth { get; init; } = 14;

    public double ShoulderRadius { get; init; } = 8;

    /// <summary>The mascot's own little notch, beside the open one: its size, and the gap between them.</summary>
    public double PodSize { get; init; } = 40;

    public double PodGap { get; init; } = 10;

    /// <summary>The quick controls' pods, left of the open notch, and their bubbles (see Quick.QuickBubble): their size.</summary>
    public double QuickSize { get; init; } = 52;

    /// <summary>Between the quick controls' two pods, left of the open notch (volume above, brightness below).</summary>
    public double QuickGap { get; init; } = 8;

    public double IslandTopMargin { get; init; } = 8;

    /// <summary>Room left around the expanded notch for its drop shadow.</summary>
    public double ShadowPadding { get; init; } = 28;

    /// <summary>Room left around the expanded notch for spring overshoot.</summary>
    public double OvershootPadding { get; init; } = 20;

    /// <summary>Starting point of the arrival animation: a sliver hidden in the screen edge.</summary>
    public NotchShape Hidden => new(Collapsed.Width * 0.6, 0, Collapsed.Radius);

    public bool IsFlush => Style == NotchStyle.Notch;

    public double TopInset => IsFlush ? 0 : IslandTopMargin;

    public double ShoulderExtent => IsFlush ? ShoulderRadius : 0;

    /// <summary>Width of the transparent window that hosts the notch.</summary>
    public double CanvasWidth => MaxWidth + 2 * (ShoulderExtent + OvershootPadding + ShadowPadding);

    /// <summary>Height of the transparent window that hosts the notch.</summary>
    public double CanvasHeight => TopInset + MaxHeight + OvershootPadding + ShadowPadding;

    private double MaxWidth => Math.Max(Expanded.Width, ExpandedMax.Width);

    private double MaxHeight => Math.Max(Expanded.Height, ExpandedMax.Height);

    /// <summary>
    /// Shape at rest in <paramref name="phase"/>. A module may widen the compact notch (live lyrics…) with
    /// <paramref name="compactWidth"/> (never narrower than the layout's own); <paramref name="expanded"/> is the open
    /// shape as it is now (see <see cref="OpenShape"/>, or a module's own, like the search bar). <paramref name="handle"/>:
    /// the closed notch is the <see cref="Handle"/> (and nothing widens it); its input bounds, the handle's spot.
    /// </summary>
    public NotchShape ShapeFor(NotchPhase phase, double compactWidth = 0, NotchShape? expanded = null, bool handle = false)
    {
        if (handle && phase != NotchPhase.Expanded)
        {
            return phase == NotchPhase.Primed
                ? new(Handle.Width + 24, Handle.Height + 4, Handle.Radius + 2)
                : Handle;
        }

        var collapsed = Collapsed with { Width = Math.Clamp(compactWidth, Collapsed.Width, MaxWidth) };
        return phase switch
        {
            NotchPhase.Primed => new(collapsed.Width + PrimeGrowth, collapsed.Height + 3, collapsed.Radius + 1.5),
            NotchPhase.Expanded => expanded ?? Expanded,
            _ => collapsed,
        };
    }

    /// <summary>The open shape, enlarged by <paramref name="requested"/> if any, within the window's room.</summary>
    public NotchShape OpenShape(NotchShape? requested) => requested is not { } shape
        ? Expanded
        : new(Math.Clamp(shape.Width, Expanded.Width, MaxWidth), Math.Clamp(shape.Height, Expanded.Height, MaxHeight), shape.Radius);

    /// <summary>Visible bounds of <paramref name="shape"/> in canvas coordinates, shoulders included.</summary>
    public Rect BoundsOf(NotchShape shape)
    {
        double width = shape.Width + 2 * ShoulderExtent;
        return new Rect((CanvasWidth - width) / 2, TopInset, width, shape.Height);
    }

    /// <summary>
    /// Part of the window that receives the mouse in <paramref name="phase"/>; everything else clicks through.
    /// The window region also clips rendering, so the expanded phase keeps room for the shadow.
    /// </summary>
    public Rect InputBoundsFor(NotchPhase phase, double compactWidth = 0, NotchShape? openShape = null, bool handle = false)
    {
        var bounds = BoundsOf(ShapeFor(phase, compactWidth, openShape, handle));
        if (handle && phase != NotchPhase.Expanded)
        {
            // The handle reaches around itself, and up to the screen edge (where the pointer comes to rest).
            double reachLeft = Math.Max(0, bounds.X - HandleReach);
            double reachRight = Math.Min(CanvasWidth, bounds.Right + HandleReach);
            return new Rect(reachLeft, 0, reachRight - reachLeft, bounds.Bottom + HandleReach / 2);
        }

        return Around(bounds, phase == NotchPhase.Expanded ? 1 : 0);
    }

    /// <summary>What the window must keep of itself around <paramref name="shape"/>, closed.</summary>
    public Rect InputBoundsOf(NotchShape shape) => Around(BoundsOf(shape), 0);

    /// <summary>
    /// What the window must keep of itself around a notch caught mid-motion (its springs' values now): it, and its
    /// shadow, which grows with <paramref name="expansion"/>.
    /// </summary>
    public Rect InputBoundsOf(double width, double height, double expansion) =>
        Around(BoundsOf(new NotchShape(Math.Max(0, width), Math.Max(0, height), 0)), Math.Clamp(expansion, 0, 1));

    private Rect Around(Rect bounds, double expansion)
    {
        double side = 3 + (ShadowPadding * 0.75 - 3) * expansion;
        double bottom = 3 + (ShadowPadding + 5) * expansion;

        double left = Math.Max(0, bounds.X - side);
        double right = Math.Min(CanvasWidth, bounds.Right + side);
        double top = IsFlush ? 0 : Math.Max(0, bounds.Y - side);
        double height = Math.Min(CanvasHeight, bounds.Bottom + bottom) - top;
        return new Rect(left, top, right - left, height);
    }
}
