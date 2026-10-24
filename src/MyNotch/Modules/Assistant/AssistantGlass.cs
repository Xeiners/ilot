using System.Numerics;
using Microsoft.UI.Composition;
using MyNotch.Composition;
using MyNotch.Mascot;
using MyNotch.Notch;
using Windows.UI;

namespace MyNotch.Modules.Assistant;

/// <summary>
/// The light of the assistant's drop (see <see cref="AssistantPanel"/>), on the compositor, over the notch's plain black:
/// the mascot's own glow behind it, and the notch's soft glow along the edge while it thinks; where the mascot's stage
/// rides, and the dots of an answer awaited. All in the mascot's light (see <see cref="MascotColor.Glow"/>), and all as
/// big as the drop (<c>n.BubbleWidth</c>, <c>n.BubbleHeight</c>), so it follows it as it springs or is resized.
/// Expressions refer to the notch (<c>n</c>), the panel's state (<c>a</c>: <c>Thinking</c>, <c>Greet</c> while it greets,
/// <c>Laid</c> the height the panel is laid out at), the stage's width and height (<c>s</c>, <c>h</c>) and where the
/// mascot's middle is on it (<c>fc</c>), given by the binder.
/// </summary>
internal sealed class AssistantGlass
{
    /// <summary>Where the mascot's stage is, from the drop's top: in the middle of the drop as it is now, never above its top.</summary>
    public const string StageTop = "Max((n.BubbleHeight - h) * 0.5, 0)";

    /// <summary>The same, in the panel as it is laid out (the stage's layout place: see AssistantPanel.PlaceStage).</summary>
    public static double StageTopFor(double height, double stageHeight) => Math.Max((height - stageHeight) * 0.5, 0);

    /// <summary>
    /// The stage, laid out where it rests, rides the drop's middle as the drop springs to its size (the layout jumps there
    /// at once), and floats gently while the mascot thinks.
    /// </summary>
    public const string StageTranslation = $"Vector3(0, {StageTop} - Max((a.Laid - h) * 0.5, 0) + Sin(n.GlowTurn * 18.8496) * 5 * Clamp(a.Thinking, 0, 1), 0)";

    /// <summary>The mascot, larger while it greets (before the first question).</summary>
    public const string FaceScale = "Vector3(1, 1, 1) * Lerp(1, 1.22, Clamp(a.Greet, 0, 1))";

    // The answer awaited: dot i bounces a little after the one before it, once a second (n.GlowTurn turns in six).
    private const string Bounce = "Max(Sin((n.GlowTurn * 6 - i * 0.14) * 6.2832), 0)";

    /// <summary>Dot <c>i</c> of the answer awaited, bouncing.</summary>
    public const string DotTranslation = $"Vector3(0, -5 * {Bounce}, 0)";

    /// <inheritdoc cref="DotTranslation"/>
    public const string DotOpacity = $"0.35 + 0.65 * {Bounce}";

    private const string Size = "Vector2(n.BubbleWidth, n.BubbleHeight)";

    private const float WandererSize = 230;

    /// <summary>
    /// Where the wandering light is (its top left corner; <c>r</c> its radius): round a loop low on the left of the drop,
    /// two rounds of different paces mixed so it never quite repeats a line, whatever the drop's height.
    /// </summary>
    private const string Wandering =
        "Vector3(150 + Sin(a.Wander * 6.2832) * 95 + Sin(a.Wander * 12.5664 + 1.3) * 30 - r, "
        + "n.BubbleHeight * 0.7 + Cos(a.Wander * 6.2832) * Min(n.BubbleHeight * 0.18, 45) + Sin(a.Wander * 18.8496 + 0.6) * 12 - r, 0)";

    private readonly List<(CompositionColorGradientStop Stop, Func<MascotColor, Color> Of)> _stops = [];
    private readonly CompositionLinearGradientBrush _edge;
    private readonly Compositor _compositor;

    public AssistantGlass(Compositor compositor, Func<string, CompositionExpression> bind, MascotColor color)
    {
        _compositor = compositor;
        var glass = compositor.CreateContainerVisual();
        Visual = glass;

        // The mascot's glow, behind it on its stage: always there, stronger and breathing while it thinks.
        var halo = compositor.CreateSpriteVisual();
        var light = compositor.CreateRadialGradientBrush();
        // Round the mascot, fading softly out under the conversation (the fades there end softly too: no edge on it).
        light.ColorStops.Add(Stop(0, color, c => Alpha(c.Glow, 0x5C)));
        light.ColorStops.Add(Stop(0.45f, color, c => Alpha(c.Glow, 0x28)));
        light.ColorStops.Add(Stop(0.78f, color, c => Alpha(c.Glow, 0x0C)));
        light.ColorStops.Add(Stop(1, color, c => Alpha(c.Glow, 0)));
        halo.Brush = light;
        halo.Size = new Vector2(300, 300);
        halo.CenterPoint = new Vector3(150, 150, 0);
        bind($"Vector3(n.BubbleWidth - s * 0.5 - 150, {StageTop} + fc - 150, 0)").Start(halo, "Offset");
        bind("Lerp(0.75, 1, Clamp(a.Thinking, 0, 1))").Start(halo, "Opacity");
        bind("Vector3(1, 1, 1) * Lerp(1, 1.22, Clamp(a.Greet, 0, 1)) * (1 + Clamp(a.Thinking, 0, 1) * (0.1 + (1 - Clamp(n.GlowPulse, 0, 1)) * 0.12))").Start(halo, "Scale");
        glass.Children.InsertAtTop(halo);

        // A little ball of light wandering low on the left, under the conversation: slow, faint (a.Wander turns once in
        // a while, see AssistantPanel; still, without it).
        var wanderer = compositor.CreateSpriteVisual();
        var ball = compositor.CreateRadialGradientBrush();
        ball.ColorStops.Add(Stop(0, color, c => Alpha(c.Glow, 0x30)));
        ball.ColorStops.Add(Stop(0.5f, color, c => Alpha(c.Glow, 0x12)));
        ball.ColorStops.Add(Stop(1, color, c => Alpha(c.Glow, 0)));
        wanderer.Brush = ball;
        wanderer.Size = new Vector2(WandererSize, WandererSize);
        bind(Wandering).With("r", WandererSize / 2).Start(wanderer, "Offset");
        glass.Children.InsertAtTop(wanderer);

        // The notch's soft glow along the edge (it shows while the mascot thinks, see n.Glow), turning round the drop.
        _edge = NotchGlow.CreateBrush(compositor, 760, color);
        bind("Vector2(n.BubbleWidth * 0.5, n.BubbleHeight * 0.5)").Start(_edge, "Offset");
        bind("n.GlowTurn * 360").Start(_edge, "RotationAngleInDegrees");
        foreach (var (inset, thickness, strength) in NotchGlow.Lines)
        {
            var geometry = compositor.CreateRoundedRectangleGeometry();
            bind("Vector2(Max(n.BubbleWidth - 2 * i, 0), Max(n.BubbleHeight - 2 * i, 0))").With("i", inset).Start(geometry, "Size");
            geometry.Offset = new Vector2(inset, inset);
            bind("Vector2(Max(Min(32, n.BubbleHeight * 0.5) - i, 0), Max(Min(32, n.BubbleHeight * 0.5) - i, 0))").With("i", inset).Start(geometry, "CornerRadius");
            var shape = compositor.CreateSpriteShape(geometry);
            shape.StrokeBrush = _edge;
            shape.StrokeThickness = thickness;
            var line = compositor.CreateShapeVisual();
            bind(Size).Start(line, "Size");
            line.Shapes.Add(shape);
            bind(NotchGlow.Opacity).With("s", strength).Start(line, "Opacity");
            glass.Children.InsertAtTop(line);
        }
    }

    /// <summary>The light, to host behind the drop's content.</summary>
    public ContainerVisual Visual { get; }

    /// <summary>The light takes <paramref name="color"/>'s, fading to it.</summary>
    public void Tint(MascotColor color)
    {
        NotchGlow.Tint(_edge, color);
        foreach (var (stop, of) in _stops)
        {
            var fade = _compositor.CreateColorKeyFrameAnimation();
            fade.InsertKeyFrame(1, of(color));
            fade.Duration = TimeSpan.FromMilliseconds(420);
            stop.StartAnimation("Color", fade);
        }
    }

    private CompositionColorGradientStop Stop(float offset, MascotColor color, Func<MascotColor, Color> of)
    {
        var stop = _compositor.CreateColorGradientStop(offset, of(color));
        _stops.Add((stop, of));
        return stop;
    }

    private static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
