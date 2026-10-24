using System.Numerics;
using Microsoft.UI.Composition;
using MyNotch.Composition;
using Windows.UI;

namespace MyNotch.Mascot;

/// <summary>
/// The mascot, drawn by the compositor: a soft pebble of a body and two capsule eyes, in a 100 × 100 box. While music
/// plays it turns into a sound wave with eyes: the body becomes the wave's middle bar, beating to the bass, and a bar
/// on each side grows with the music's levels. Every shape is
/// bound by expressions to a property set (<c>p</c>) driven by springs: changing mood, looking around, blinking or
/// hopping never runs layout nor any code per frame.
/// </summary>
/// <remarks>
/// The eyes are drawn in the notch's own black over the body, which is clipped to the body: on the black notch they read
/// as holes in it, and an eye looking far to the side is cut by the body's edge instead of leaving it.
/// </remarks>
public sealed class MascotRig
{
    /// <summary>The box the mascot is drawn in; the host scales it.</summary>
    public const float Box = 100;

    private const float Floor = 92;
    private const float LookTravelX = 7;
    private const float LookTravelY = 5;

    private static readonly Color Cheek = Color.FromArgb(150, 0xFF, 0x8F, 0xA8);

    // How each mood's numbers move: lively, a touch of overshoot, as if the body had some give.
    private static readonly Spring Change = new(0.42, 0.72);
    private static readonly Spring Gaze = new(0.32, 0.86);
    private static readonly Spring Jump = new(0.40, 0.42);
    private static readonly Spring Give = new(0.30, 0.38);
    private static readonly Spring Morph = new(0.55, 0.78);

    /// <summary>The mouth follows the voice: quick enough for the syllables, smooth, without a quiver.</summary>
    private static readonly Spring MouthMove = new(0.13, 0.88);

    // The wave's side bars: their width, the gap between bars, and their height silent and at full level. Two on each
    // side, all of it within the box (the pill holds it).
    private const float BarWidth = 10;
    private const float BarGap = 4.5f;
    private const float BarRest = 11;
    private const float BarReach = 70;

    /// <summary>The outer bars reach this much of the inner ones: the wave rises to its middle, like a mountain.</summary>
    private const float OuterReach = 0.72f;

    /// <summary>
    /// The levels a beat lends the side bars (see <see cref="Groove"/>), inner left and right, then outer left and right:
    /// the middle of the spectrum inside, its lows and highs outside, so each bar moves on its own.
    /// </summary>
    private static readonly string[] Levels = ["L2", "L3", "L1", "L4"];

    // Thinking: three dots chasing one another round the body, the leader bigger, the others trailing.
    private const float OrbitX = 48;
    private const float OrbitY = 40;
    private const float OrbitTrail = 0.085f;
    private static readonly TimeSpan OrbitTime = TimeSpan.FromSeconds(1.15);

    // The body's live size and place, and the eyes' centre height, shared by the expressions below.
    private const string Squash = "Clamp(p.Squash, -1, 1)";
    private const string Beat = "Clamp(p.Beat, 0, 1)";

    // The wave's kick: the bass, quiet levels lifted (a curve, then a gain), so that it moves with any music.
    private const string Kick = "Clamp(Pow(" + Beat + ", 0.75) * 1.3, 0, 1)";
    private const string Wave = "Clamp(p.Wave, 0, 1)";

    // Squashed flat into the notch's handle (see Flatten): 0 is itself, 1 the handle's grabber line.
    private const string Flat = "Clamp(p.Flat, 0, 1)";

    // A pebble squashes on the kick; the wave's middle bar stretches with it. Flat, it is the grabber line.
    // How open the mouth is (see Say). Not in the body's size: its expressions are inlined everywhere, and Windows caps
    // an expression's length (the bounces stretch the body enough).
    private const string Mouth = "Clamp(p.Mouth, 0, 1)";

    private const string BodyWidth =
        "Lerp(p.BW * (1 + " + Squash + " * 0.1 + " + Beat + " * 0.07 * (1 - " + Wave + ") + " + Kick + " * 0.12 * " + Wave + "), p.FW, " + Flat + ")";
    private const string BodyHeight =
        "Lerp(p.BH * (1 - " + Squash + " * 0.1 - " + Beat + " * 0.12 * (1 - " + Wave + ") + " + Kick + " * 0.6 * " + Wave + "), p.FH, " + Flat + ")";
    private const string BodyX = "(50 + p.LX * 1.5)";

    // A pebble sits on the box's floor; a wave, or the flattened line, is centred on its middle line.
    private const string BodyBottom =
        "(Lerp(Lerp(fl, 50 + " + BodyHeight + " * 0.5, " + Wave + "), 50 + " + BodyHeight + " * 0.5, " + Flat + ") + p.Hop)";
    private const string BodyY = "(" + BodyBottom + " - " + BodyHeight + " * 0.5)";
    private const string EyeY = "(" + BodyBottom + " - " + BodyHeight + " * 0.5 + p.EY + p.LY * ly)";

    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _p;
    private readonly SpringChannel _bodyWidth;
    private readonly SpringChannel _bodyHeight;
    private readonly SpringChannel _bodyRadius;
    private readonly SpringChannel _eyeWidth;
    private readonly SpringChannel _eyeHeight;
    private readonly SpringChannel _eyeSpacing;
    private readonly SpringChannel _eyeRise;
    private readonly SpringChannel _eyeTilt;
    private readonly SpringChannel _blush;
    private readonly SpringChannel _lookX;
    private readonly SpringChannel _lookY;
    private readonly SpringChannel _hop;
    private readonly SpringChannel _squash;
    private readonly SpringChannel _wave;
    private readonly SpringChannel _dots;
    private readonly SpringChannel _orb;
    private readonly SpringChannel _book;
    private readonly SpringChannel _pad;
    private readonly SpringChannel _padTilt;
    private readonly SpringChannel _padBob;
    private int _wiggles;
    private readonly SpringChannel _mouth;
    private readonly SpringChannel _mouthShape;
    private readonly CompositionColorGradientStop _tone;
    private readonly CompositionColorGradientStop _shade;
    private readonly CompositionColorBrush _eyes;

    // The orb's colours, each from the mascot's (see MascotColor.Glow), to follow it when it changes.
    private readonly List<(CompositionColorGradientStop Stop, Func<MascotColor, Color> Of)> _orbStops = [];
    private MascotColor _color = MascotPalette.Find(MascotPalette.DefaultId);

    public MascotRig(Compositor compositor, MascotMood mood)
    {
        _compositor = compositor;
        _p = compositor.CreatePropertySet();
        var pose = MascotPose.For(mood);
        _bodyWidth = new SpringChannel(_p, "BW", pose.BodyWidth);
        _bodyHeight = new SpringChannel(_p, "BH", pose.BodyHeight);
        _bodyRadius = new SpringChannel(_p, "BR", pose.BodyRadius);
        _eyeWidth = new SpringChannel(_p, "EW", pose.EyeWidth);
        _eyeHeight = new SpringChannel(_p, "EH", pose.EyeHeight);
        _eyeSpacing = new SpringChannel(_p, "ES", pose.EyeSpacing);
        _eyeRise = new SpringChannel(_p, "EY", pose.EyeRise);
        _eyeTilt = new SpringChannel(_p, "Tilt", pose.EyeTilt);
        _blush = new SpringChannel(_p, "Blush", pose.Blush);
        _lookX = new SpringChannel(_p, "LX", 0);
        _lookY = new SpringChannel(_p, "LY", 0);
        _hop = new SpringChannel(_p, "Hop", 0);
        _squash = new SpringChannel(_p, "Squash", 0);
        _wave = new SpringChannel(_p, "Wave", 0);
        _dots = new SpringChannel(_p, "Dots", 0);
        _orb = new SpringChannel(_p, "Orb", 0);
        _book = new SpringChannel(_p, "Book", 0);
        _p.InsertScalar("Flip", 0);
        _pad = new SpringChannel(_p, "Pad", 0);
        _padTilt = new SpringChannel(_p, "PadTilt", 0);
        _padBob = new SpringChannel(_p, "PadBob", 0);
        _mouth =new SpringChannel(_p, "Mouth", 0);
        _mouthShape = new SpringChannel(_p, "MouthShape", 0.5);
        _p.InsertScalar("Orbit", 0);
        foreach (string level in Levels)
        {
            _p.InsertScalar(level, 0);
        }
        _p.InsertScalar("Blink", 0);
        _p.InsertScalar("Beat", 0);
        _p.InsertScalar("Flat", 0);
        _p.InsertScalar("FW", 40);
        _p.InsertScalar("FH", 14);
        _p.InsertScalar("WinkL", 0);
        _p.InsertScalar("WinkR", 0);

        Root = compositor.CreateContainerVisual();
        Root.Size = new Vector2(Box, Box);

        var gradient = compositor.CreateLinearGradientBrush();
        gradient.StartPoint = new Vector2(0.5f, 0);
        gradient.EndPoint = new Vector2(0.5f, 1);
        var color = MascotPalette.Find(MascotPalette.DefaultId);
        _tone = compositor.CreateColorGradientStop(0, color.Tone);
        _shade = compositor.CreateColorGradientStop(1, color.Shade);
        gradient.ColorStops.Add(_tone);
        gradient.ColorStops.Add(_shade);

        // The wave's side bars, one on each side: few and thick, it stays readable at the size of the pill.
        var bars = CreateLayer(out var barShapes);
        for (int i = 0; i < Levels.Length; i++)
        {
            barShapes.Shapes.Add(CreateBar(gradient, side: i % 2 == 0 ? -1 : 1, rank: i / 2, Levels[i]));
        }

        for (int i = 0; i < 3; i++)
        {
            barShapes.Shapes.Add(CreateDot(gradient, i));
        }

        Root.Children.InsertAtTop(bars);

        var face = CreateLayer(out var faceShapes);
        var body = compositor.CreateSpriteShape(CreateBodyGeometry());
        body.FillBrush = gradient;
        faceShapes.Shapes.Add(body);

        var cheek = compositor.CreateColorBrush(Cheek);
        faceShapes.Shapes.Add(CreateCheek(cheek, side: -1));
        faceShapes.Shapes.Add(CreateCheek(cheek, side: 1));
        face.Clip = CreateBodyClip();
        Root.Children.InsertAtTop(face);
        Root.Children.InsertAtTop(CreateRelief());

        // Whatever goes past the body's edge (an eye looking far aside) is cut by it.
        var eyes = CreateLayer(out var eyeShapes);
        _eyes = compositor.CreateColorBrush(color.Eyes);
        eyeShapes.Shapes.Add(CreateEye(_eyes, side: -1, wink: "WinkL"));
        eyeShapes.Shapes.Add(CreateEye(_eyes, side: 1, wink: "WinkR"));
        eyes.Clip = CreateBodyClip();
        Root.Children.InsertAtTop(eyes);

        // Its mouth, in the eyes' colour, only while it says something (see Exclaim): otherwise it has none.
        var talk = CreateLayer(out var talkShapes);
        talkShapes.Shapes.Add(CreateMouth(_eyes));
        Bind($"Clamp({Mouth} * 4, 0, 1) * (1 - {Flat}) * (1 - {Wave})").Start(talk, "Opacity");
        Root.Children.InsertAtTop(talk);
        Root.Children.InsertAtTop(CreateBook());
        Root.Children.InsertAtTop(CreatePad());
        Root.Children.InsertAtTop(CreateOrb());
    }

    // The gamepad's colours: a dark slate body, a light cross, four bright buttons.
    private static readonly Color PadBody = Color.FromArgb(0xFF, 0x2E, 0x31, 0x40);
    private static readonly Color PadSheen = Color.FromArgb(0xFF, 0x4A, 0x4E, 0x60);
    private static readonly Color PadCross = Color.FromArgb(0xFF, 0xC9, 0xCE, 0xDA);
    private static readonly (Vector2 At, Color Color)[] PadButtons =
    [
        (new(15, -15), Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80)),
        (new(19, -11.5f), Color.FromArgb(0xFF, 0xF8, 0x71, 0x71)),
        (new(15, -8), Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA)),
        (new(11, -11.5f), Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24)),
    ];

    /// <summary>
    /// Its gamepad (the Pomodoro's break, see <see cref="Pad"/>), held in front of its lower half like the book: a dark
    /// body with two grips, a light cross on the left, four coloured buttons on the right. It jiggles now and then as it
    /// plays (<see cref="Wiggle"/>). Not on the wave, the handle's line, nor the orb.
    /// </summary>
    private ShapeVisual CreatePad()
    {
        var layer = CreateLayer(out var shapes);
        Bind($"Clamp(p.Pad, 0, 1) * (1 - {Flat}) * (1 - {Wave}) * (1 - Clamp(p.Orb, 0, 1))").Start(layer, "Opacity");

        // Held at its middle's foot, on the body's lower edge, rising into its hands as it comes; it tilts and bobs.
        var pad = _compositor.CreateContainerShape();
        pad.CenterPoint = new Vector2(0, -10);
        Bind($"Vector2({BodyX}, {BodyBottom} - 3 + (1 - Clamp(p.Pad, 0, 1)) * 14 + p.PadBob)").Start(pad, "Offset");
        Bind("Vector2(Lerp(0.55, 1, Clamp(p.Pad, 0, 1)), Lerp(0.55, 1, Clamp(p.Pad, 0, 1)))").Start(pad, "Scale");
        Bind("p.PadTilt").Start(pad, "RotationAngleInDegrees");
        shapes.Shapes.Add(pad);

        var body = _compositor.CreateColorBrush(PadBody);
        pad.Shapes.Add(Ellipse(body, new Vector2(-16, -5), new Vector2(10, 9)));
        pad.Shapes.Add(Ellipse(body, new Vector2(16, -5), new Vector2(10, 9)));
        pad.Shapes.Add(Rounded(body, new Vector2(-24, -19), new Vector2(48, 16), 8));
        pad.Shapes.Add(Rounded(_compositor.CreateColorBrush(PadSheen), new Vector2(-20, -18.5f), new Vector2(40, 2), 1));
        var cross = _compositor.CreateColorBrush(PadCross);
        pad.Shapes.Add(Rounded(cross, new Vector2(-20.5f, -13), new Vector2(10, 3.2f), 1));
        pad.Shapes.Add(Rounded(cross, new Vector2(-17, -16.5f), new Vector2(3.2f, 10), 1));
        foreach (var (at, color) in PadButtons)
        {
            pad.Shapes.Add(Ellipse(_compositor.CreateColorBrush(color), at, new Vector2(2.1f, 2.1f)));
        }

        return layer;

        CompositionSpriteShape Ellipse(CompositionBrush brush, Vector2 center, Vector2 radius)
        {
            var geometry = _compositor.CreateEllipseGeometry();
            geometry.Center = center;
            geometry.Radius = radius;
            var shape = _compositor.CreateSpriteShape(geometry);
            shape.FillBrush = brush;
            return shape;
        }

        CompositionSpriteShape Rounded(CompositionBrush brush, Vector2 offset, Vector2 size, float radius)
        {
            var geometry = _compositor.CreateRoundedRectangleGeometry();
            geometry.Offset = offset;
            geometry.Size = size;
            geometry.CornerRadius = new Vector2(radius);
            var shape = _compositor.CreateSpriteShape(geometry);
            shape.FillBrush = brush;
            return shape;
        }
    }

    // The book's colours: a warm cover, cream pages, their lines of text.
    private static readonly Color Cover = Color.FromArgb(0xFF, 0xF0, 0x84, 0x5C);
    private static readonly Color CoverEdge = Color.FromArgb(0xFF, 0xC8, 0x5E, 0x3E);
    private static readonly Color Paper = Color.FromArgb(0xFF, 0xFF, 0xF8, 0xEC);
    private static readonly Color Ink = Color.FromArgb(0xFF, 0xD2, 0xC6, 0xB4);

    // A page: its size, and how much it leans (its outer edge up: the book open in a V, as held to read).
    private static readonly Vector2 PageSize = new(30, 21);
    private const float PageLean = -7;

    /// <summary>
    /// Its book (the Pomodoro's focus, see <see cref="Book"/>), open, held in front of its lower half: a warm cover under
    /// two cream pages leaning away from the spine, a few lines of text on each. A page turns now and then
    /// (<see cref="TurnPage"/>): it folds over the spine from the right to the left, lifting a little. Not on the wave,
    /// the handle's line, nor the orb.
    /// </summary>
    private ShapeVisual CreateBook()
    {
        var layer = CreateLayer(out var shapes);
        Bind($"Clamp(p.Book, 0, 1) * (1 - {Flat}) * (1 - {Wave}) * (1 - Clamp(p.Orb, 0, 1))").Start(layer, "Opacity");

        // Held at the spine's foot, on the body's lower edge, rising into its hands as it comes.
        var book = _compositor.CreateContainerShape();
        Bind($"Vector2({BodyX}, {BodyBottom} - 3 + (1 - Clamp(p.Book, 0, 1)) * 14)").Start(book, "Offset");
        Bind("Vector2(Lerp(0.55, 1, Clamp(p.Book, 0, 1)), Lerp(0.55, 1, Clamp(p.Book, 0, 1)))").Start(book, "Scale");
        shapes.Shapes.Add(book);

        var cover = _compositor.CreateColorBrush(Cover);
        var edge = _compositor.CreateColorBrush(CoverEdge);
        var paper = _compositor.CreateColorBrush(Paper);
        var ink = _compositor.CreateColorBrush(Ink);
        book.Shapes.Add(Leaf(cover, side: -1, grow: 2.2f));
        book.Shapes.Add(Leaf(cover, side: 1, grow: 2.2f));
        book.Shapes.Add(Spine(edge));
        book.Shapes.Add(Page(paper, ink, side: -1));
        book.Shapes.Add(Page(paper, ink, side: 1));

        // The page that turns: over the right one at rest (the same), folded over the spine onto the left one as
        // p.Flip goes to 1, rising a little half way; then back at once, where it looks the same.
        var turning = Page(paper, ink, side: 1);
        Bind("Vector2(1 - Clamp(p.Flip, 0, 1) * 2, 1)").Start(turning, "Scale");
        Bind("Vector2(0, -Sin(Clamp(p.Flip, 0, 1) * 3.1416) * 3)").Start(turning, "Offset");
        book.Shapes.Add(turning);
        return layer;

        // A page (or, grown, the cover under it) on its side of the spine, leaning away from it, its foot at the spine's.
        CompositionContainerShape Page(CompositionBrush page, CompositionBrush lines, int side)
        {
            var leaf = _compositor.CreateContainerShape();
            leaf.CenterPoint = Vector2.Zero;
            leaf.Shapes.Add(Leaf(page, side, grow: 0));
            for (int i = 0; i < 4; i++)
            {
                var line = _compositor.CreateRoundedRectangleGeometry();
                float width = i == 3 ? 13 : 20;
                line.Size = new Vector2(width, 1.6f);
                line.CornerRadius = new Vector2(0.8f);
                line.Offset = new Vector2(side < 0 ? -PageSize.X + 5 : 5, -PageSize.Y + 4.5f + i * 4.2f);
                var stroke = _compositor.CreateSpriteShape(line);
                stroke.FillBrush = lines;
                stroke.RotationAngleInDegrees = PageLean * side;
                leaf.Shapes.Add(stroke);
            }

            return leaf;
        }

        CompositionSpriteShape Leaf(CompositionBrush brush, int side, float grow)
        {
            var geometry = _compositor.CreateRoundedRectangleGeometry();
            geometry.Size = PageSize + new Vector2(grow, grow);
            geometry.CornerRadius = new Vector2(2.5f);
            geometry.Offset = new Vector2(side < 0 ? -PageSize.X - grow : 0, -PageSize.Y - grow * 0.5f);
            var leaf = _compositor.CreateSpriteShape(geometry);
            leaf.FillBrush = brush;
            leaf.RotationAngleInDegrees = PageLean * side; // Round the spine's foot: the pages open like a V.
            return leaf;
        }

        CompositionSpriteShape Spine(CompositionBrush brush)
        {
            var geometry = _compositor.CreateRoundedRectangleGeometry();
            geometry.Size = new Vector2(2.4f, PageSize.Y + 2.5f);
            geometry.CornerRadius = new Vector2(1.2f);
            geometry.Offset = new Vector2(-1.2f, -PageSize.Y - 1.5f);
            var spine = _compositor.CreateSpriteShape(geometry);
            spine.FillBrush = brush;
            return spine;
        }
    }

    /// <summary>
    /// The assistant's orb, over the body and as big (its geometry is the body's, so it follows it): a dark sphere lit to
    /// its rim, a halo round it, both in the mascot's light (see <see cref="MascotColor.Glow"/>), and the eyes, light.
    /// Shown by <c>p.Orb</c>. Nothing turns in it.
    /// </summary>
    private ContainerVisual CreateOrb()
    {
        var orb = _compositor.CreateContainerVisual();
        orb.Size = new Vector2(Box, Box);
        // Squashed into the handle's line (see Flatten), it is the mascot's colour again, not the dark sphere.
        Bind($"Clamp(p.Orb, 0, 1) * (1 - {Flat})").Start(orb, "Opacity");

        // The halo, round the sphere.
        var halo = CreateLayer(out var haloShapes);
        var haloShape = _compositor.CreateSpriteShape(CreateOrbEllipse("0.64", "0.64"));
        haloShape.FillBrush = Radial((0, c => Alpha(c.Glow, 0x88)), (0.6f, c => Alpha(c.Glow, 0x44)), (1, c => Alpha(c.Glow, 0)));
        haloShapes.Shapes.Add(haloShape);
        orb.Children.InsertAtTop(halo);

        // The sphere.
        var sphere = CreateLayer(out var sphereShapes);
        var sphereShape = _compositor.CreateSpriteShape(CreateBodyGeometry());
        var fill = Radial(
            (0, c => c.Light(0.09)), (0.55f, c => c.Light(0.16)), (0.82f, c => c.Light(0.42)),
            (0.95f, c => c.Glow), (1, c => c.GlowBright));
        fill.GradientOriginOffset = new Vector2(-0.16f, -0.2f);
        sphereShape.FillBrush = fill;
        sphereShapes.Shapes.Add(sphereShape);
        var sphereClip = _compositor.CreateGeometricClip(CreateBodyGeometry());
        sphereClip.Offset = new Vector2(Box, 0);
        sphere.Clip = sphereClip;
        orb.Children.InsertAtTop(sphere);

        // Its eyes, light on the dark sphere.
        var eyes = CreateLayer(out var eyeShapes);
        var light = _compositor.CreateColorBrush(Color.FromArgb(0xFF, 0xF2, 0xF6, 0xFF));
        eyeShapes.Shapes.Add(CreateEye(light, side: -1, wink: "WinkL"));
        eyeShapes.Shapes.Add(CreateEye(light, side: 1, wink: "WinkR"));
        orb.Children.InsertAtTop(eyes);

        // Its mouth, under them: a smile, which opens as it talks (see Say, ShapeMouth) and comes back between the syllables.
        var smile = CreateLayer(out var smileShapes);
        smileShapes.Shapes.Add(CreateSmile(light));
        Bind($"1 - Clamp({Mouth} * 4, 0, 1)").Start(smile, "Opacity");
        orb.Children.InsertAtTop(smile);
        var mouth = CreateLayer(out var mouthShapes);
        mouthShapes.Shapes.Add(CreateMouth(light));
        Bind($"Clamp({Mouth} * 4, 0, 1) * 0.92").Start(mouth, "Opacity");
        orb.Children.InsertAtTop(mouth);
        return orb;
    }

    /// <summary>An ellipse centred on the body, its radii these fractions of the body's size.</summary>
    private CompositionEllipseGeometry CreateOrbEllipse(string across, string down)
    {
        var geometry = _compositor.CreateEllipseGeometry();
        Bind($"Vector2({BodyX}, {BodyY})").Start(geometry, "Center");
        Bind($"Vector2({BodyWidth} * {across}, {BodyHeight} * {down})").Start(geometry, "Radius");
        return geometry;
    }

    private CompositionRadialGradientBrush Radial(params (float Offset, Func<MascotColor, Color> Of)[] stops)
    {
        var brush = _compositor.CreateRadialGradientBrush();
        foreach (var (offset, of) in stops)
        {
            brush.ColorStops.Add(Stop(offset, of));
        }

        return brush;
    }

    /// <summary>A stop of the orb or the relief, from the mascot's colour: it follows it when it changes (see <see cref="SetColor"/>).</summary>
    private CompositionColorGradientStop Stop(float offset, Func<MascotColor, Color> of)
    {
        var stop = _compositor.CreateColorGradientStop(offset, of(_color));
        _orbStops.Add((stop, of));
        return stop;
    }

    private static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>
    /// A little relief on the body, under the eyes: a soft shadow round its edge (more at the bottom) and a soft light up on
    /// its left. Still, nothing runs; none on the wave or the handle's line, which stay plain.
    /// </summary>
    private ShapeVisual CreateRelief()
    {
        var relief = CreateLayer(out var shapes);
        var shadow = _compositor.CreateSpriteShape(CreateBodyGeometry());
        var dark = Radial((0, c => Alpha(c.Shade, 0)), (0.66f, c => Alpha(c.Shade, 0)), (1, c => Alpha(Deepen(c.Shade), 0x58)));
        dark.GradientOriginOffset = new Vector2(0, -0.1f);
        dark.EllipseRadius = new Vector2(0.56f, 0.6f);
        shadow.FillBrush = dark;
        shapes.Shapes.Add(shadow);

        var light = _compositor.CreateSpriteShape(CreateBodyGeometry());
        var shine = Radial((0, _ => Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF)), (0.55f, _ => Color.FromArgb(0, 0xFF, 0xFF, 0xFF)));
        shine.GradientOriginOffset = new Vector2(-0.16f, -0.24f);
        light.FillBrush = shine;
        shapes.Shapes.Add(light);

        relief.Clip = CreateBodyClip();
        Bind($"(1 - {Flat}) * (1 - {Wave})").Start(relief, "Opacity");
        return relief;

        static Color Deepen(Color c) => Color.FromArgb(255, (byte)(c.R * 0.7), (byte)(c.G * 0.7), (byte)(c.B * 0.7));
    }

    /// <summary>A layer's clip to the body (see <see cref="CreateLayer"/>): whatever goes past its edge is cut.</summary>
    private CompositionGeometricClip CreateBodyClip()
    {
        var clip = _compositor.CreateGeometricClip(CreateBodyGeometry());
        clip.Offset = new Vector2(Box, 0);
        return clip;
    }

    /// <summary>
    /// A layer of shapes, three boxes wide and centred on the box (its shapes drawn in the box's own coordinates): squashed
    /// flat into a wide handle, the body reaches past the box without being cut.
    /// </summary>
    private ShapeVisual CreateLayer(out CompositionContainerShape shapes)
    {
        var layer = _compositor.CreateShapeVisual();
        layer.Size = new Vector2(Box * 3, Box);
        layer.Offset = new Vector3(-Box, 0, 0);
        shapes = _compositor.CreateContainerShape();
        shapes.Offset = new Vector2(Box, 0);
        layer.Shapes.Add(shapes);
        return layer;
    }

    /// <summary>Root visual (100 × 100): host it and scale it to the room it has.</summary>
    public ContainerVisual Root { get; }

    /// <summary>Wears <paramref name="color"/> (body, wave and eyes, and the orb's light), fading to it, or at once.</summary>
    public void SetColor(MascotColor color, bool animate = true)
    {
        _color = color;
        Fade(_tone, color.Tone);
        Fade(_shade, color.Shade);
        Fade(_eyes, color.Eyes);
        foreach (var (stop, of) in _orbStops)
        {
            Fade(stop, of(color));
        }

        void Fade(CompositionObject target, Color to)
        {
            if (!animate)
            {
                target.StopAnimation("Color");
                switch (target)
                {
                    case CompositionColorGradientStop stop:
                        stop.Color = to;
                        break;

                    case CompositionColorBrush brush:
                        brush.Color = to;
                        break;
                }

                return;
            }

            var animation = _compositor.CreateColorKeyFrameAnimation();
            animation.InsertKeyFrame(1, to, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1)));
            animation.Duration = TimeSpan.FromMilliseconds(420);
            target.StartAnimation("Color", animation);
        }
    }

    /// <summary>Takes on <paramref name="mood"/>, springing to it, or at once.</summary>
    public void SetMood(MascotMood mood, bool animate = true)
    {
        var pose = MascotPose.For(mood);
        Go(_bodyWidth, pose.BodyWidth);
        Go(_bodyHeight, pose.BodyHeight);
        Go(_bodyRadius, pose.BodyRadius);
        Go(_eyeWidth, pose.EyeWidth);
        Go(_eyeHeight, pose.EyeHeight);
        Go(_eyeSpacing, pose.EyeSpacing);
        Go(_eyeRise, pose.EyeRise);
        Go(_eyeTilt, pose.EyeTilt);
        Go(_blush, pose.Blush);

        void Go(SpringChannel channel, double value)
        {
            if (animate)
            {
                channel.AnimateTo(value, Change);
            }
            else
            {
                channel.Snap(value);
            }
        }
    }

    /// <summary>Looks towards <paramref name="direction"/> (each axis from -1 to 1; zero is straight ahead).</summary>
    public void Look(Vector2 direction)
    {
        _lookX.AnimateTo(Math.Clamp(direction.X, -1, 1), Gaze);
        _lookY.AnimateTo(Math.Clamp(direction.Y, -1, 1), Gaze);
    }

    /// <summary>
    /// Bounces to <paramref name="beat"/>'s <c>Bass</c> (0 to 1, kept up to date by its owner): it squashes on each
    /// kick, on the compositor. <c>null</c> stops it.
    /// </summary>
    public void Groove(CompositionPropertySet? beat)
    {
        _wave.AnimateTo(beat is null ? 0 : 1, Morph);
        if (beat is null)
        {
            foreach (string level in Levels.Append("Beat"))
            {
                _p.StopAnimation(level);
                _p.InsertScalar(level, 0);
            }

            return;
        }

        Bind("Clamp(g.Bass, 0, 1)").With("g", beat).Start(_p, "Beat");
        foreach (string level in Levels)
        {
            Bind($"Clamp(g.{level}, 0, 1)").With("g", beat).Start(_p, level);
        }
    }

    /// <summary>
    /// Squashes it flat into the notch's handle as <paramref name="notch"/>'s <c>Mini</c> rises: its body becomes the
    /// handle's grabber line (as wide as the handle's, <c>Min(n.Width * 0.47, 56)</c>, and 4 pixels thick), its eyes
    /// shut and go, the wave folds away. <paramref name="pixelsPerUnit"/>: its box's scale on screen, to size the line.
    /// </summary>
    public void Flatten(CompositionPropertySet notch, float pixelsPerUnit)
    {
        Bind("Clamp(n.Mini, 0, 1)").With("n", notch).Start(_p, "Flat");
        Bind("Min(n.Width * 0.47, 56) / u").With("n", notch).With("u", pixelsPerUnit).Start(_p, "FW");
        Bind("4 / u").With("u", pixelsPerUnit).Start(_p, "FH");
    }

    /// <summary>Both eyes shut and open again, quickly.</summary>
    public void Blink() => Pulse("Blink", TimeSpan.FromMilliseconds(170), hold: 0);

    /// <summary>One eye shuts for a moment.</summary>
    public void Wink(bool left = false) => Pulse(left ? "WinkL" : "WinkR", TimeSpan.FromMilliseconds(620), hold: 0.45f);

    /// <summary>Its book in its hands (the Pomodoro's focus), coming up from below, or put away.</summary>
    public void Book(bool reading) => _book.AnimateTo(reading ? 1 : 0, Morph);

    /// <summary>Its gamepad in its hands (the Pomodoro's break), coming up from below, or put away.</summary>
    public void Pad(bool playing)
    {
        _pad.AnimateTo(playing ? 1 : 0, Morph);
        if (!playing)
        {
            _padTilt.AnimateTo(0, Morph);
            _padBob.AnimateTo(0, Morph);
        }
    }

    /// <summary>It plays: the gamepad jerks aside (one way, then the other, turn by turn) and dips, then settles.</summary>
    public void Wiggle()
    {
        _wiggles++;
        _padTilt.Kick((_wiggles % 2 == 0 ? 1 : -1) * 70, Give);
        _padBob.Kick(28, Give);
    }

    /// <summary>A page of its book turns: it folds over the spine onto the other one (then is back, unseen).</summary>
    public void TurnPage()
    {
        var turn = _compositor.CreateScalarKeyFrameAnimation();
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.35f, 1));
        turn.InsertKeyFrame(0, 0);
        turn.InsertKeyFrame(1, 1, ease);
        turn.Duration = TimeSpan.FromMilliseconds(650);
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _p.StartAnimation("Flip", turn);
        batch.End();
        batch.Completed += (_, _) =>
        {
            _p.StopAnimation("Flip");
            _p.InsertScalar("Flip", 0); // Back over the right page, where it looks the same.
        };
    }

    /// <summary>A little jump: it stretches as it leaves the floor and squashes as it lands.</summary>
    public void Hop(double strength = 1)
    {
        _hop.Kick(-150 * strength, Jump);
        _squash.Kick(-9 * strength, Give);
    }

    /// <summary>
    /// Thinking (an answer awaited): three dots come out and chase one another round the body, until it is done. The
    /// only endless animation here, and only meanwhile: stopped, nothing turns.
    /// </summary>
    public void Think(bool thinking)
    {
        _dots.AnimateTo(thinking ? 1 : 0, Change);
        if (thinking)
        {
            var orbit = _compositor.CreateScalarKeyFrameAnimation();
            orbit.InsertKeyFrame(0, 0);
            orbit.InsertKeyFrame(1, 1, _compositor.CreateLinearEasingFunction());
            orbit.Duration = OrbitTime;
            orbit.IterationBehavior = AnimationIterationBehavior.Forever;
            _p.StartAnimation("Orbit", orbit);
        }
        else
        {
            _p.StopAnimation("Orbit");
        }
    }

    /// <summary>The assistant's orb (see <see cref="CreateOrb"/>) comes, or fades away.</summary>
    public void Orb(bool shown) => _orb.AnimateTo(shown ? 1 : 0, Morph);

    /// <summary>A word comes out (the assistant's answer, as it is written): a small bounce.</summary>
    public void Talk()
    {
        _mouth.Kick(5, MouthMove);
        _mouthShape.AnimateTo(Random.Shared.NextDouble(), MouthMove);
        _squash.Kick(-4, Give);
        _hop.Kick(-30, Jump);
    }

    /// <summary>Its mouth as open as its voice is loud now (<paramref name="level"/>, 0 to 1); 0 shuts it.</summary>
    public void Say(double level) => _mouth.AnimateTo(Math.Clamp(level, 0, 1), MouthMove);

    /// <summary>A new syllable: the mouth takes another shape (<paramref name="shape"/>: 0 an « o », 1 an « a »).</summary>
    public void ShapeMouth(double shape) => _mouthShape.AnimateTo(Math.Clamp(shape, 0, 1), MouthMove);

    /// <summary>A syllable of its voice (it talks aloud): a bounce as strong as the voice is loud (<paramref name="strength"/>, 0 to 1).</summary>
    public void Speak(double strength)
    {
        strength = Math.Clamp(strength, 0, 1);
        _squash.Kick(-1.5 - 3 * strength, Give);
        _hop.Kick(-6 - 24 * strength, Jump);
    }

    /// <summary>
    /// It says something short aloud, without a voice (« Temps écoulé ! On prend une pause »): its mouth opens and shuts
    /// on <paramref name="syllables"/> syllables of varied shapes, a little bounce on the first, then it shuts, played
    /// once by the compositor.
    /// </summary>
    public void Exclaim(int syllables = 9)
    {
        var mouth = _compositor.CreateScalarKeyFrameAnimation();
        var shape = _compositor.CreateScalarKeyFrameAnimation();
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0), new Vector2(0.4f, 1));
        mouth.InsertKeyFrame(0, 0);
        shape.InsertKeyFrame(0, 0.5f);
        for (int i = 0; i < syllables; i++)
        {
            float at = (float)i / syllables;
            float half = 0.5f / syllables;
            mouth.InsertKeyFrame(at + half * 0.55f, 0.55f + (float)Random.Shared.NextDouble() * 0.45f, ease);
            mouth.InsertKeyFrame(at + half * 1.7f, i == syllables - 1 ? 0 : 0.12f, ease);
            shape.InsertKeyFrame(at + half * 0.4f, (float)Random.Shared.NextDouble(), ease);
        }

        mouth.InsertKeyFrame(1, 0);
        shape.InsertKeyFrame(1, 0.5f);
        mouth.Duration = shape.Duration = TimeSpan.FromMilliseconds(190 * syllables);
        _p.StartAnimation("Mouth", mouth);
        _p.StartAnimation("MouthShape", shape);
        Speak(0.8);
    }

    /// <summary>A key typed while it listens: the faintest nod.</summary>
    public void Nod() => _squash.Kick(-2.5, Give);

    /// <summary>A start (something came): it squashes, then stands up tall.</summary>
    public void Startle()
    {
        _squash.Kick(10, Give);
        _hop.Kick(-70, Jump);
    }

    /// <summary>0 → 1 → 0 on <paramref name="property"/>, held at 1 for <paramref name="hold"/> of the time.</summary>
    private void Pulse(string property, TimeSpan duration, float hold)
    {
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0), new Vector2(0.2f, 1));
        float closed = (1 - hold) * 0.4f;
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0);
        animation.InsertKeyFrame(closed, 1, ease);
        if (hold > 0)
        {
            animation.InsertKeyFrame(closed + hold, 1);
        }

        animation.InsertKeyFrame(1, 0, ease);
        animation.Duration = duration;
        _p.StartAnimation(property, animation);
    }

    private CompositionRoundedRectangleGeometry CreateBodyGeometry()
    {
        var geometry = _compositor.CreateRoundedRectangleGeometry();
        Bind($"Vector2({BodyWidth}, {BodyHeight})").Start(geometry, "Size");
        Bind($"Vector2({BodyX} - {BodyWidth} * 0.5, {BodyBottom} - {BodyHeight})").Start(geometry, "Offset");
        const string Radius = "Min(p.BR, Min(" + BodyWidth + ", " + BodyHeight + ") * 0.5)";
        Bind($"Vector2({Radius}, {Radius})").Start(geometry, "CornerRadius");
        return geometry;
    }

    /// <summary>
    /// Its smile, under the eyes, following the gaze: the bottom of an ellipse, stroked with round ends (a composition
    /// ellipse's path starts at its top and runs clockwise: its bottom is the middle third of it).
    /// </summary>
    private CompositionSpriteShape CreateSmile(CompositionBrush brush)
    {
        var geometry = _compositor.CreateEllipseGeometry();
        geometry.Radius = new Vector2(7.5f, 4.6f);
        geometry.TrimStart = 0.31f;
        geometry.TrimEnd = 0.69f;
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = brush;
        shape.StrokeThickness = 2.3f;
        shape.StrokeStartCap = CompositionStrokeCap.Round;
        shape.StrokeEndCap = CompositionStrokeCap.Round;
        Bind($"Vector2({BodyX} + p.LX * lx, {EyeY} + p.EH * 0.5 + 4)").Start(shape, "Offset");
        return shape;
    }

    /// <summary>
    /// The mouth open, under the eyes, between them, following the gaze (shut, it is the smile, see CreateSmile); as the voice is loud
    /// (<c>p.Mouth</c>), a rounded opening from its middle, of a shape that changes with the syllables (<c>p.MouthShape</c>:
    /// 0 round and tall, an « o »; 1 wide and low, an « a »). A capsule all along: simple, never a hole.
    /// </summary>
    private CompositionSpriteShape CreateMouth(CompositionBrush brush)
    {
        const string Shape = "Clamp(p.MouthShape, 0, 1)";
        string width = $"(Lerp(9.5, 15, {Shape}) * (1 - {Mouth} * 0.15))";
        string height = $"(1.8 + {Mouth} * Lerp(8, 4.6, {Shape}))";
        var geometry = _compositor.CreateRoundedRectangleGeometry();
        Bind($"Vector2({width}, {height})").Start(geometry, "Size");
        Bind($"Vector2(-{width} * 0.5, -{height} * 0.5)").Start(geometry, "Offset");
        Bind($"Vector2(Min({width}, {height}) * 0.5, Min({width}, {height}) * 0.5)").Start(geometry, "CornerRadius");
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.FillBrush = brush;
        Bind($"Vector2({BodyX} + p.LX * lx, {EyeY} + p.EH * 0.5 + 8)").Start(shape, "Offset");
        return shape;
    }

    /// <summary>A capsule eye, on <paramref name="side"/> (-1 left, 1 right), shut by blinking or its wink.</summary>
    private CompositionSpriteShape CreateEye(CompositionBrush brush, int side, string wink)
    {
        string shut = $"Clamp(p.{wink}, 0, 1)";
        string width = $"(p.EW * (1 + {shut} * 0.3) * (1 - {Flat} * 0.5))";
        string height = $"(Max(p.EH * (1 - Clamp(p.Blink, 0, 1) * 0.88) * (1 - {shut} * 0.86) * (1 - {Mouth} * 0.12), 1.6) * (1 - {Flat}))";

        var geometry = _compositor.CreateRoundedRectangleGeometry();
        Bind($"Vector2({width}, {height})").Start(geometry, "Size");
        Bind($"Vector2(-{width} * 0.5, -{height} * 0.5)").Start(geometry, "Offset");
        Bind($"Vector2(Min({width}, {height}) * 0.5, Min({width}, {height}) * 0.5)").Start(geometry, "CornerRadius");

        var shape = _compositor.CreateSpriteShape(geometry);
        shape.FillBrush = brush;
        Bind($"Vector2({BodyX} + p.ES * s + p.LX * lx, {EyeY})").With("s", side).Start(shape, "Offset");
        Bind("p.Tilt * s").With("s", side).Start(shape, "RotationAngleInDegrees");
        return shape;
    }

    /// <summary>
    /// A side bar of the wave, on <paramref name="side"/> (-1 left, 1 right), <paramref name="rank"/> bars out from the
    /// body: as tall as <paramref name="level"/> says, centred on the wave's middle line, grown by <c>p.Wave</c>.
    /// </summary>
    private CompositionSpriteShape CreateBar(CompositionBrush brush, int side, int rank, string level)
    {
        string width = $"(bw * {Wave} * (1 - {Flat}))";
        // Its level lifted like the kick, and the bass too: every beat shows on the whole wave.
        string lifted = $"Max(Clamp(Pow(Clamp(p.{level}, 0, 1), 0.7) * 1.35, 0, 1), {Kick} * 0.6)";
        string height = $"((br + {lifted} * bh) * k * {Wave} * (1 - {Flat}))";
        var geometry = _compositor.CreateRoundedRectangleGeometry();
        Bar($"Vector2({width}, {height})").Start(geometry, "Size");
        Bar($"Vector2(-{width} * 0.5, -{height} * 0.5)").Start(geometry, "Offset");
        Bar($"Vector2({width} * 0.5, {width} * 0.5)").Start(geometry, "CornerRadius");

        var shape = _compositor.CreateSpriteShape(geometry);
        shape.FillBrush = brush;
        Bar("Vector2(50 + s * (p.BW * 0.5 + bg + bw * 0.5 + r * (bw + bg)), 50 + p.Hop)")
            .With("s", side).With("r", rank)
            .Start(shape, "Offset");
        return shape;

        CompositionExpression Bar(string expression) => Bind(expression)
            .With("bw", BarWidth).With("bg", BarGap).With("br", BarRest).With("bh", BarReach).With("k", rank == 0 ? 1 : OuterReach);
    }

    /// <summary>A thinking dot, <paramref name="rank"/> behind the leader on the orbit round the body (see <see cref="Think"/>).</summary>
    private CompositionSpriteShape CreateDot(CompositionBrush brush, int rank)
    {
        var geometry = _compositor.CreateEllipseGeometry();
        Bind("Vector2(r, r) * Clamp(p.Dots, 0, 1) * (1 - Clamp(p.Flat, 0, 1))").With("r", 5.5f - rank * 1.4f).Start(geometry, "Radius");
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.FillBrush = brush;
        const string Angle = "((p.Orbit - k * t) * 6.2831853)";
        Bind($"Vector2(50 + ox * Sin({Angle}), 54 - oy * Cos({Angle}))")
            .With("k", rank).With("t", OrbitTrail).With("ox", OrbitX).With("oy", OrbitY)
            .Start(shape, "Offset");
        return shape;
    }

    /// <summary>A soft cheek under an eye, grown by <c>p.Blush</c>.</summary>
    private CompositionSpriteShape CreateCheek(CompositionBrush brush, int side)
    {
        var geometry = _compositor.CreateEllipseGeometry();
        Bind($"Vector2(6.5, 3.6) * Clamp(p.Blush, 0, 1) * (1 - {Flat})").Start(geometry, "Radius");
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.FillBrush = brush;
        Bind($"Vector2({BodyX} + (p.ES + 9) * s + p.LX * lx * 0.6, {EyeY} + 10)").With("s", side).Start(shape, "Offset");
        return shape;
    }

    private CompositionExpression Bind(string expression) => new CompositionExpression(_compositor, expression)
        .With("p", _p).With("fl", Floor).With("lx", LookTravelX).With("ly", LookTravelY);
}
