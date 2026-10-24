using System.Numerics;
using Microsoft.UI.Composition;
using Ilot.Mascot;
using Windows.UI;

namespace Ilot.Notch;

/// <summary>
/// The assistant's light along an edge (the notch's, the drop's): a soft glow, brightest at the edge and fading inward,
/// in the mascot's colour (see <see cref="MascotColor.Glow"/>), its brightest part turning slowly round.
/// </summary>
/// <remarks>
/// Composition has no blur without Win2D: the glow is many thin lines one inside the other, each fainter than the one
/// outside it, which read as one smooth fade.
/// </remarks>
public static class NotchGlow
{
    private const int Count = 8;

    /// <summary>The lines, from the edge inward: inset from the edge, thickness, strength.</summary>
    public static IReadOnlyList<(float Inset, float Thickness, float Strength)> Lines { get; } =
        Enumerable.Range(0, Count)
            .Select(k => (0.75f + 2.1f * k, 2.6f + 0.3f * k, 0.6f * MathF.Pow(1 - k / (float)Count, 2)))
            .ToList();

    /// <summary>A line's opacity: its strength, the glow's (<c>n.Glow</c>), breathing a little (<c>n.GlowPulse</c>); <c>s</c> its strength.</summary>
    public const string Opacity = "Clamp(n.Glow, 0, 1) * s * Lerp(0.72, 1, Clamp(n.GlowPulse, 0, 1))";

    // Round the edge: bright, the colour, nearly gone on the far side, the colour, bright again (it turns, see Bind).
    private static readonly float[] Offsets = [0, 0.28f, 0.55f, 0.8f, 1];

    /// <summary>
    /// The glow's brush, <paramref name="span"/> wide around its origin, in <paramref name="color"/>'s light. The caller
    /// binds its <c>Offset</c> to the middle of what it lights, and its rotation to <c>n.GlowTurn</c>: it turns round it.
    /// </summary>
    public static CompositionLinearGradientBrush CreateBrush(Compositor compositor, float span, MascotColor color)
    {
        var brush = compositor.CreateLinearGradientBrush();
        brush.MappingMode = CompositionMappingMode.Absolute;
        brush.StartPoint = new Vector2(-span / 2, 0);
        brush.EndPoint = new Vector2(span / 2, 0);
        var colors = Colors(color);
        for (int i = 0; i < Offsets.Length; i++)
        {
            brush.ColorStops.Add(compositor.CreateColorGradientStop(Offsets[i], colors[i]));
        }

        return brush;
    }

    /// <summary>The brush from <see cref="CreateBrush"/> takes <paramref name="color"/>'s light, fading to it.</summary>
    public static void Tint(CompositionLinearGradientBrush brush, MascotColor color)
    {
        var colors = Colors(color);
        var compositor = brush.Compositor;
        for (int i = 0; i < brush.ColorStops.Count && i < colors.Length; i++)
        {
            var fade = compositor.CreateColorKeyFrameAnimation();
            fade.InsertKeyFrame(1, colors[i]);
            fade.Duration = TimeSpan.FromMilliseconds(420);
            brush.ColorStops[i].StartAnimation("Color", fade);
        }
    }

    private static Color[] Colors(MascotColor color)
    {
        var bright = color.GlowBright;
        var glow = color.Glow;
        return [bright, glow, Color.FromArgb(0x40, glow.R, glow.G, glow.B), glow, bright];
    }
}
