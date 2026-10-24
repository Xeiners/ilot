using System.Numerics;
using Microsoft.UI.Composition;
using Ilot.Composition;
using Ilot.Mascot;
using Windows.UI;

namespace Ilot.Notch;

/// <summary>
/// Renders the notch body on the compositor: an anti-aliased morphing shape, concave shoulders that melt it into
/// the screen edge, a hairline rim light and a soft shadow that appears as the notch lifts off the screen.
/// </summary>
/// <remarks>
/// Every visual is bound to <see cref="NotchMotion"/> through expressions: the morph never runs layout and costs
/// nothing on the UI thread, which is what keeps it perfectly fluid.
/// </remarks>
public sealed class NotchSurface
{
    private static readonly Color BodyColor = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color Clear = Color.FromArgb(0, 0, 0, 0);

    private readonly NotchMotion _motion;
    private readonly Compositor _compositor;

    public NotchSurface(NotchMotion motion)
    {
        _motion = motion;
        _compositor = motion.Compositor;
        var size = new Vector2((float)motion.Layout.CanvasWidth, (float)motion.Layout.CanvasHeight);

        Root = _compositor.CreateContainerVisual();
        Root.Size = size;

        // Lift shadow: only once the notch opens. Collapsed, the notch must look like part of the screen.
        Root.Children.InsertAtTop(CreateLiftShadow(size));

        var bodyVisual = _compositor.CreateShapeVisual();
        bodyVisual.Size = size;
        var body = _compositor.CreateSpriteShape(CreateBodyGeometry(inset: 0));
        body.FillBrush = _compositor.CreateColorBrush(BodyColor);
        bodyVisual.Shapes.Add(body);
        Root.Children.InsertAtTop(bodyVisual);

        if (motion.Layout.IsFlush)
        {
            Root.Children.InsertAtTop(CreateShoulder(isLeft: true));
            Root.Children.InsertAtTop(CreateShoulder(isLeft: false));
        }

        // Rim light: a hairline along the lower edge that catches the light and defines the silhouette
        // on dark wallpapers, while the top stays fused with the screen edge.
        var rimVisual = _compositor.CreateShapeVisual();
        rimVisual.Size = size;
        var rim = _compositor.CreateSpriteShape(CreateBodyGeometry(inset: 0.5f));
        rim.StrokeThickness = 1;
        rim.StrokeBrush = CreateRimBrush();
        rimVisual.Shapes.Add(rim);
        Root.Children.InsertAtTop(rimVisual);
        Root.Children.InsertAtTop(CreateGlow(size));
        Pod = CreatePod(size);
        Root.Children.InsertAtTop(Pod);
        QuickPods = CreateQuickPods(size);
        QuickPods.Opacity = 0; // Shown only by a notch that has the quick controls (see NotchView.BindQuick).
        Root.Children.InsertAtTop(QuickPods);
        Root.Children.InsertAtTop(CreateGrabber(size));

        Root.Children.InsertAtTop(CreateBubble(size));
    }

    /// <summary>Root visual, to host with <c>ElementCompositionPreview.SetElementChildVisual</c>.</summary>
    public ContainerVisual Root { get; }

    /// <summary>
    /// The mascot's little notch (see <see cref="CreatePod"/>); its owner decides when it shows, through its opacity.
    /// </summary>
    public ContainerVisual Pod { get; }

    /// <summary>
    /// The quick controls' two pods, left of the open notch (volume, brightness; see <see cref="CreateQuickPods"/>); their
    /// owner decides when they show, through their opacity.
    /// </summary>
    public ContainerVisual QuickPods { get; }

    // The assistant's glow (see NotchGlow): how long its light takes to go round, and to breathe.
    private static readonly TimeSpan GlowTurnTime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan GlowBreathTime = TimeSpan.FromSeconds(2.6);

    /// <summary>
    /// Starts or stops the glow's life (see <see cref="CreateGlow"/>): its colours turning round the notch, and its
    /// breathing. Its strength is <c>n.Glow</c>'s; without it, nothing turns.
    /// </summary>
    public void Shimmer(bool on)
    {
        if (!on)
        {
            _motion.Properties.StopAnimation("GlowTurn");
            _motion.Properties.StopAnimation("GlowPulse");
            _shimmering = false;
            return;
        }

        if (_shimmering)
        {
            return;
        }

        _shimmering = true;
        var turn = _compositor.CreateScalarKeyFrameAnimation();
        turn.InsertExpressionKeyFrame(0, "this.StartingValue");
        turn.InsertExpressionKeyFrame(1, "this.StartingValue + 1", _compositor.CreateLinearEasingFunction());
        turn.Duration = GlowTurnTime;
        turn.IterationBehavior = AnimationIterationBehavior.Forever;
        _motion.Properties.StartAnimation("GlowTurn", turn);

        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.55f, 1));
        var breath = _compositor.CreateScalarKeyFrameAnimation();
        breath.InsertKeyFrame(0, 1);
        breath.InsertKeyFrame(0.5f, 0, ease);
        breath.InsertKeyFrame(1, 1, ease);
        breath.Duration = GlowBreathTime;
        breath.IterationBehavior = AnimationIterationBehavior.Forever;
        _motion.Properties.StartAnimation("GlowPulse", breath);
    }

    private bool _shimmering;
    private CompositionLinearGradientBrush? _glowBrush;

    /// <summary>The glow takes the mascot's colour (see <see cref="MascotColor.Glow"/>), fading to it.</summary>
    public void Tint(MascotColor color)
    {
        if (_glowBrush is not null)
        {
            NotchGlow.Tint(_glowBrush, color);
        }
    }

    /// <summary>
    /// The assistant's glow, inside the edges of the notch and of the drop under it: a soft light fading inward from the
    /// edge (see <see cref="NotchGlow"/>), in the mascot's colour, its brightest part turning round the notch
    /// (<c>n.GlowTurn</c>) and breathing (<c>n.GlowPulse</c>). Its strength is <c>n.Glow</c>'s.
    /// </summary>
    private ContainerVisual CreateGlow(Vector2 size)
    {
        var glow = _compositor.CreateContainerVisual();
        glow.Size = size;

        var brush = NotchGlow.CreateBrush(_compositor, 760, MascotPalette.Find(MascotPalette.DefaultId));
        _glowBrush = brush;
        _motion.Bind("Vector2(n.HostWidth * 0.5, n.Inset + n.Height * 0.5)").Start(brush, "Offset");
        _motion.Bind("n.GlowTurn * 360").Start(brush, "RotationAngleInDegrees");

        foreach (var (inset, thickness, strength) in NotchGlow.Lines)
        {
            glow.Children.InsertAtTop(Line(CreateBodyGeometry(inset), thickness, strength, "1"));
            glow.Children.InsertAtTop(Line(CreateBubbleGeometry(inset), thickness, strength, "Clamp(n.Bubble, 0, 1)"));
        }

        return glow;

        ShapeVisual Line(CompositionGeometry geometry, float thickness, float strength, string shown)
        {
            var line = _compositor.CreateShapeVisual();
            line.Size = size;
            var shape = _compositor.CreateSpriteShape(geometry);
            shape.StrokeBrush = brush;
            shape.StrokeThickness = thickness;
            line.Shapes.Add(shape);
            _motion.Bind($"{NotchGlow.Opacity} * {shown}").With("s", strength).Start(line, "Opacity");
            return line;
        }
    }

    // The pod's live size and left edge: hidden in the closed pill's right end, it comes out as the notch opens and
    // settles a gap away from its right side.
    private const string PodWidth = "Lerp(cw, ow, n.Progress)";
    private const string PodLeft = "Lerp((n.HostWidth + n.Width) * 0.5 - cw, (n.HostWidth + n.Width) * 0.5 + gap, n.Progress)";

    // The quick controls' pods, the mirror image on the left: hidden in the pill's left end, out a gap away; the second
    // drops below the first.
    private const string QuickWidth = "Lerp(cw, qw, n.Progress)";
    private const string QuickLeft = "Lerp((n.HostWidth - n.Width) * 0.5, (n.HostWidth - n.Width) * 0.5 - gap - qw, n.Progress)";
    private const string QuickSecondTop = "(n.Inset + (qw + vg) * n.Progress)";

    /// <summary>A clip that follows the body exactly, for the content layer.</summary>
    public CompositionClip CreateContentClip() => _compositor.CreateGeometricClip(CreateBodyGeometry(inset: 0));

    /// <summary>A clip that follows the bubble under the notch exactly, for its content.</summary>
    public CompositionClip CreateBubbleClip() => _compositor.CreateGeometricClip(CreateBubbleGeometry(inset: 0));

    /// <summary>
    /// The grabber of the handle the closed notch goes back into under the pointer: a soft light line across the middle
    /// of the small tab, which says "pull me". It follows the tab as it swells under the pointer, and is gone as soon
    /// as the notch opens.
    /// </summary>
    private ShapeVisual CreateGrabber(Vector2 size)
    {
        const float Thickness = 4;
        var geometry = _compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2(Thickness / 2);
        _motion.Bind("Vector2(Min(n.Width * 0.47, 56), t)").With("t", Thickness).Start(geometry, "Size");
        _motion.Bind("Vector2((n.HostWidth - Min(n.Width * 0.47, 56)) * 0.5, n.Inset + (n.Height - t) * 0.5)")
            .With("t", Thickness)
            .Start(geometry, "Offset");
        var line = _compositor.CreateSpriteShape(geometry);
        line.FillBrush = _compositor.CreateColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));

        var grabber = _compositor.CreateShapeVisual();
        grabber.Size = size;
        grabber.Shapes.Add(line);
        // With the mascot, it is the line: squashed flat into the handle (see MascotRig.Flatten).
        _motion.Bind("Clamp(n.Mini, 0, 1) * Clamp(1 - n.Progress * 4, 0, 1) * Clamp(n.Height / 6, 0, 1) * (1 - n.Mascot)").Start(grabber, "Opacity");
        return grabber;
    }

    // ── The bubble ──────────────────────────────────────────────────────────────────────────────────────────
    //
    // n.Bubble (b) drives it all. At 0 it is a small blob tucked inside the notch's bottom; as b grows it falls,
    // stretching a neck from the notch that thins and lets go (b ≈ 0.6) while it swells to its size, then bounces
    // to rest n.Gap below the notch. Drawn back in, the same in reverse: the neck reaches out, and the notch drinks
    // it up.

    private const string Fall = "Clamp(n.Bubble, 0, 1)";
    private const string NotchBottom = "(n.Inset + n.Height)";
    private static readonly string BubbleWidth = $"Lerp(Min(n.Width * 0.4, n.BubbleWidth), n.BubbleWidth, {Fall})";
    private static readonly string BubbleHeight = $"Lerp(14, n.BubbleHeight, {Fall})";
    private static readonly string BubbleTop = $"({NotchBottom} + n.Gap * n.Bubble - (1 - {Fall}) * 14)";
    private static readonly string BubbleRadius = $"Min(32, {BubbleHeight} * 0.5)";
    private static readonly string Neck = $"Max(Lerp({BubbleWidth} * 0.5, 0, Clamp(n.Bubble * 1.7, 0, 1)), 0)";
    private static readonly string Opening = $"({BubbleTop} - {NotchBottom})";
    private static readonly string Fillet = $"Clamp(Min({Opening} * 0.5, {Neck} * 0.5), 0, 14)";

    /// <summary>Hidden at rest, so nothing of it ever shows at the notch's bottom edge.</summary>
    private const string Shown = "Clamp(n.Bubble * 40, 0, 1)";

    private ContainerVisual CreateBubble(Vector2 size)
    {
        var container = _compositor.CreateContainerVisual();
        container.Size = size;
        _motion.Bind(Shown).Start(container, "Opacity");

        // The neck: a bridge from the notch to the bubble, its sides hollowed by four fillets.
        var neck = _compositor.CreateSpriteVisual();
        neck.Brush = _compositor.CreateColorBrush(BodyColor);
        _motion.Bind($"Vector2({Neck}, Max({Opening} + 4, 0))").Start(neck, "Size");
        _motion.Bind($"Vector3((n.HostWidth - {Neck}) * 0.5, {NotchBottom} - 2, 0)").Start(neck, "Offset");
        _motion.Bind($"Clamp({Neck} - 0.5, 0, 1)").Start(neck, "Opacity");
        container.Children.InsertAtTop(neck);
        foreach (bool left in new[] { true, false })
        {
            foreach (bool top in new[] { true, false })
            {
                container.Children.InsertAtTop(CreateFillet(left, top));
            }
        }

        var body = _compositor.CreateShapeVisual();
        body.Size = size;
        var shape = _compositor.CreateSpriteShape(CreateBubbleGeometry(inset: 0));
        shape.FillBrush = _compositor.CreateColorBrush(BodyColor);
        body.Shapes.Add(shape);
        container.Children.InsertAtTop(body);

        // Its own rim light, once it has let go of the notch.
        var rimVisual = _compositor.CreateShapeVisual();
        rimVisual.Size = size;
        var rim = _compositor.CreateSpriteShape(CreateBubbleGeometry(inset: 0.5f));
        rim.StrokeThickness = 1;
        var brush = _compositor.CreateLinearGradientBrush();
        brush.MappingMode = CompositionMappingMode.Absolute;
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0, Color.FromArgb(18, 255, 255, 255)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(1, Color.FromArgb(44, 255, 255, 255)));
        _motion.Bind($"Vector2(0, {BubbleTop})").Start(brush, "StartPoint");
        _motion.Bind($"Vector2(0, {BubbleTop} + {BubbleHeight})").Start(brush, "EndPoint");
        rim.StrokeBrush = brush;
        rimVisual.Shapes.Add(rim);
        _motion.Bind("Clamp((n.Bubble - 0.6) * 3, 0, 1)").Start(rimVisual, "Opacity");
        container.Children.InsertAtTop(rimVisual);
        return container;
    }

    private CompositionRoundedRectangleGeometry CreateBubbleGeometry(float inset)
    {
        var geometry = _compositor.CreateRoundedRectangleGeometry();
        _motion.Bind($"Vector2(Max({BubbleWidth} - 2 * i, 0), Max({BubbleHeight} - 2 * i, 0))").With("i", inset).Start(geometry, "Size");
        _motion.Bind($"Vector2((n.HostWidth - {BubbleWidth}) * 0.5 + i, {BubbleTop} + i)").With("i", inset).Start(geometry, "Offset");
        _motion.Bind($"Vector2(Max({BubbleRadius} - i, 0), Max({BubbleRadius} - i, 0))").With("i", inset).Start(geometry, "CornerRadius");
        return geometry;
    }

    /// <summary>
    /// A concave corner where the neck meets the notch (<paramref name="top"/>) or the bubble: a square, clear inside
    /// a quarter circle and black outside it, like the notch's shoulders.
    /// </summary>
    private SpriteVisual CreateFillet(bool left, bool top)
    {
        var brush = _compositor.CreateRadialGradientBrush();
        brush.MappingMode = CompositionMappingMode.Absolute;
        brush.ExtendMode = CompositionGradientExtendMode.Clamp;
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0, Clear));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.9f, Clear));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(1, BodyColor));

        // The circle's centre: the square's corner away from both the neck and the edge it joins.
        _motion.Bind($"Vector2({(left ? "0" : Fillet)}, {(top ? Fillet : "0")})").Start(brush, "EllipseCenter");
        _motion.Bind($"Vector2({Fillet}, {Fillet})").Start(brush, "EllipseRadius");

        var fillet = _compositor.CreateSpriteVisual();
        fillet.Brush = brush;
        _motion.Bind($"Vector2({Fillet}, {Fillet})").Start(fillet, "Size");
        string x = left ? $"(n.HostWidth - {Neck}) * 0.5 - {Fillet}" : $"(n.HostWidth + {Neck}) * 0.5";
        string y = top ? NotchBottom : $"{BubbleTop} - {Fillet}";
        _motion.Bind($"Vector3({x}, {y}, 0)").Start(fillet, "Offset");
        _motion.Bind($"Clamp({Fillet} - 0.5, 0, 1)").Start(fillet, "Opacity");
        return fillet;
    }

    private CompositionRoundedRectangleGeometry CreateBodyGeometry(float inset)
    {
        // Flush style pushes the top corners above the screen edge (n.Flush = 1), leaving a flat top.
        const string Radius = "Max(Min(n.Radius, Min(n.Width, n.Height + n.Radius * n.Flush) * 0.5) - i, 0)";

        var geometry = _compositor.CreateRoundedRectangleGeometry();
        // Anchored on the left edge: an extension (n.Extra) only grows the right side.
        _motion.Bind("Vector2(Max(n.Width + n.Extra - 2 * i, 0), Max(n.Height + n.Radius * n.Flush - 2 * i, 0))")
            .With("i", inset)
            .Start(geometry, "Size");
        _motion.Bind("Vector2((n.HostWidth - n.Width) * 0.5 + i, n.Inset - n.Radius * n.Flush + i)")
            .With("i", inset)
            .Start(geometry, "Offset");
        _motion.Bind($"Vector2({Radius}, {Radius})")
            .With("i", inset)
            .Start(geometry, "CornerRadius");
        return geometry;
    }

    /// <summary>
    /// The mascot's own little notch: a round drop of the notch's black, hidden in the closed pill's right end (where
    /// the mascot rides). As the notch opens it comes out of it and settles a gap away, beside the open notch, with the
    /// same finish: a rim light, and in the flush style the fillets that melt it into the screen edge.
    /// </summary>
    private ContainerVisual CreatePod(Vector2 size) => CreatePod(size, PodLeft, "n.Inset", PodWidth, hanging: true);

    /// <summary>
    /// The quick controls' two pods, left of the open notch (see <see cref="QuickPods"/>): hidden in the closed pill's left
    /// end, they come out of it as the notch opens; the first hangs from the screen edge like the mascot's, the second
    /// drops just below it, a round bubble.
    /// </summary>
    private ContainerVisual CreateQuickPods(Vector2 size)
    {
        var pods = _compositor.CreateContainerVisual();
        pods.Size = size;
        pods.Children.InsertAtTop(CreatePod(size, QuickLeft, "n.Inset", QuickWidth, hanging: true));
        pods.Children.InsertAtTop(CreatePod(size, QuickLeft, QuickSecondTop, QuickWidth, hanging: false));
        return pods;
    }

    /// <summary>
    /// A small notch of the notch's black, <paramref name="width"/> wide, from <paramref name="left"/> and
    /// <paramref name="top"/> (expressions): hanging from the screen edge (a flat top in the flush style, melted into the
    /// edge by fillets), or round. The rim lights up as it comes out of the notch: closed, it would draw a line inside it.
    /// </summary>
    private ContainerVisual CreatePod(Vector2 size, string left, string top, string width, bool hanging)
    {
        var pod = _compositor.CreateContainerVisual();
        pod.Size = size;

        var shapes = _compositor.CreateShapeVisual();
        shapes.Size = size;
        var body = _compositor.CreateSpriteShape(CreatePodGeometry(left, top, width, hanging, inset: 0));
        body.FillBrush = _compositor.CreateColorBrush(BodyColor);
        shapes.Shapes.Add(body);
        pod.Children.InsertAtTop(shapes);

        var rimBrush = _compositor.CreateLinearGradientBrush();
        rimBrush.MappingMode = CompositionMappingMode.Absolute;
        rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(0, Color.FromArgb(0, 255, 255, 255)));
        rimBrush.ColorStops.Add(_compositor.CreateColorGradientStop(1, Color.FromArgb(44, 255, 255, 255)));
        PodBind($"Vector2(0, {top} + {width} * 0.55)").Start(rimBrush, "StartPoint");
        PodBind($"Vector2(0, {top} + {width})").Start(rimBrush, "EndPoint");
        var rimVisual = _compositor.CreateShapeVisual();
        rimVisual.Size = size;
        var rim = _compositor.CreateSpriteShape(CreatePodGeometry(left, top, width, hanging, inset: 0.5f));
        rim.StrokeThickness = 1;
        rim.StrokeBrush = rimBrush;
        rimVisual.Shapes.Add(rim);
        _motion.Bind("n.Progress").Start(rimVisual, "Opacity");
        pod.Children.InsertAtTop(rimVisual);

        if (hanging && _motion.Layout.IsFlush)
        {
            var leftFillet = CreateFillet(isLeft: true);
            PodBind(left + " - n.Shoulder").Start(leftFillet, "Offset.X");
            pod.Children.InsertAtTop(leftFillet);
            var rightFillet = CreateFillet(isLeft: false);
            PodBind(left + " + " + width).Start(rightFillet, "Offset.X");
            pod.Children.InsertAtTop(rightFillet);
        }

        return pod;
    }

    private CompositionRoundedRectangleGeometry CreatePodGeometry(string left, string top, string width, bool hanging, float inset)
    {
        // Hanging in the flush style, its top corners go above the screen edge, as for the notch: a flat top.
        string lift = hanging ? $"{width} * 0.5 * n.Flush" : "0";
        var geometry = _compositor.CreateRoundedRectangleGeometry();
        PodBind($"Vector2(Max({width} - 2 * i, 0), Max({width} + {lift} - 2 * i, 0))").With("i", inset).Start(geometry, "Size");
        PodBind($"Vector2({left} + i, {top} - {lift} + i)").With("i", inset).Start(geometry, "Offset");
        PodBind($"Vector2(Max({width} * 0.5 - i, 0), Max({width} * 0.5 - i, 0))").With("i", inset).Start(geometry, "CornerRadius");
        return geometry;
    }

    private CompositionExpression PodBind(string expression) => _motion.Bind(expression)
        .With("cw", _motion.Layout.Collapsed.Height).With("ow", _motion.Layout.PodSize).With("gap", _motion.Layout.PodGap)
        .With("vg", _motion.Layout.QuickGap).With("qw", _motion.Layout.QuickSize);

    /// <summary>
    /// A concave fillet joining the side of the notch to the screen edge: a square painted with a radial gradient
    /// that is clear inside the quarter circle and opaque outside, with a one pixel anti-aliased edge.
    /// </summary>
    private SpriteVisual CreateShoulder(bool isLeft)
    {
        var shoulder = CreateFillet(isLeft);
        _motion.Bind(isLeft
                ? "Vector3((n.HostWidth - n.Width) * 0.5 - n.Shoulder, 0, 0)"
                : "Vector3((n.HostWidth + n.Width) * 0.5 + n.Extra, 0, 0)")
            .Start(shoulder, "Offset");

        // Fade in only once the straight part of the side is tall enough to meet the fillet. The handle is too
        // small a tab for them: they come back as it opens.
        _motion.Bind("Clamp((n.Height - n.Radius) / n.Shoulder, 0, 1) * Lerp(1, n.Progress, Clamp(n.Mini, 0, 1))").Start(shoulder, "Opacity");
        return shoulder;
    }

    /// <summary>The fillet itself, at the top of the screen; its owner places it.</summary>
    private SpriteVisual CreateFillet(bool isLeft)
    {
        float size = (float)_motion.Layout.ShoulderRadius;

        var brush = _compositor.CreateRadialGradientBrush();
        brush.MappingMode = CompositionMappingMode.Absolute;
        brush.EllipseCenter = new Vector2(isLeft ? 0 : size, size);
        brush.EllipseRadius = new Vector2(size, size);
        brush.ExtendMode = CompositionGradientExtendMode.Clamp;
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0, Clear));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(Math.Max(0, 1 - 1 / size), Clear));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(1, BodyColor));

        var fillet = _compositor.CreateSpriteVisual();
        fillet.Size = new Vector2(size, size);
        fillet.Brush = brush;
        return fillet;
    }

    /// <summary>
    /// A soft ambient shadow under the notch, drawn as a radial gradient that follows the morph.
    /// </summary>
    /// <remarks>
    /// Cheaper and more predictable than a <see cref="DropShadow"/>, which misbehaves on a per-pixel transparent
    /// window (it ignores its opacity and bleeds through the content layer).
    /// </remarks>
    private SpriteVisual CreateLiftShadow(Vector2 size)
    {
        var brush = _compositor.CreateRadialGradientBrush();
        brush.MappingMode = CompositionMappingMode.Absolute;
        brush.ExtendMode = CompositionGradientExtendMode.Clamp;
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0, Color.FromArgb(92, 0, 0, 0)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0.45f, Color.FromArgb(46, 0, 0, 0)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(1, Clear));
        _motion.Bind("Vector2(n.HostWidth * 0.5, n.Inset + n.Height - 6)").Start(brush, "EllipseCenter");
        // Never deeper than the room left below the notch (ShadowPadding): a tall notch would have it cut off hard.
        _motion.Bind("Vector2(n.Width * 0.56, Min(18 + n.Height * 0.2, r))")
            .With("r", _motion.Layout.ShadowPadding + 12)
            .Start(brush, "EllipseRadius");

        var shadow = _compositor.CreateSpriteVisual();
        shadow.Size = size;
        shadow.Brush = brush;
        _motion.Bind("n.Progress").Start(shadow, "Opacity");
        return shadow;
    }

    private CompositionLinearGradientBrush CreateRimBrush()
    {
        var brush = _compositor.CreateLinearGradientBrush();
        brush.MappingMode = CompositionMappingMode.Absolute;
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(0, Color.FromArgb(0, 255, 255, 255)));
        brush.ColorStops.Add(_compositor.CreateColorGradientStop(1, Color.FromArgb(44, 255, 255, 255)));
        _motion.Bind("Vector2(0, n.Inset + n.Height * 0.55)").Start(brush, "StartPoint");
        _motion.Bind("Vector2(0, n.Inset + n.Height)").Start(brush, "EndPoint");
        return brush;
    }
}
