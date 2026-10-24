using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Ilot.Mascot;
using Ilot.Settings;
using Windows.Foundation;
using Windows.UI;

namespace Ilot.Modules.Study;

/// <summary>The revisions' colours and motions, shared by the sheet in the notch and the review's own window.</summary>
internal static class StudyPaint
{
    /// <summary>A right answer, a wrong one, a card's dark ground.</summary>
    public static readonly Color Right = Color.FromArgb(0xFF, 0x6C, 0xD9, 0x7E);
    public static readonly Color Wrong = Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B);
    public static readonly Color CardBase = Color.FromArgb(0xFF, 0x16, 0x16, 0x18);

    /// <summary>The revisions' colour: the mascot's own, as chosen (its light, should it be too dark to glow on black).</summary>
    public static Color Accent(AppSettings settings)
    {
        var color = MascotPalette.Find(settings.MascotColor);
        var tone = color.Tone;
        return (0.2126 * tone.R + 0.7152 * tone.G + 0.0722 * tone.B) / 255 < 0.25 ? color.Glow : tone;
    }

    public static LinearGradientBrush Linear(Point start, Point end, params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (offset, color) in stops)
        {
            brush.GradientStops.Add(new GradientStop { Offset = offset, Color = color });
        }

        return brush;
    }

    public static RadialGradientBrush Radial(Point center, double radiusX, double radiusY, params (double Offset, Color Color)[] stops)
    {
        var brush = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = radiusX, RadiusY = radiusY };
        foreach (var (offset, color) in stops)
        {
            brush.GradientStops.Add(new GradientStop { Offset = offset, Color = color });
        }

        return brush;
    }

    public static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary><paramref name="a"/> towards <paramref name="b"/> by <paramref name="amount"/> (0 to 1), opaque.</summary>
    public static Color Mix(Color a, Color b, double amount) => Color.FromArgb(
        255,
        (byte)Math.Round(a.R + (b.R - a.R) * amount),
        (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));

    /// <summary>
    /// The two soft glows of a revisions' surface, in <paramref name="accent"/>: a faint one from the top left corner, a
    /// fuller one in the bottom right (the black was too flat there); both fade out before the middle, where the text is.
    /// </summary>
    public static (Brush Top, Brush Bottom) Glows(Color accent) => (
        Radial(new Point(0.1, -0.08), 0.55, 0.5, (0, Alpha(accent, 0x30)), (0.5, Alpha(accent, 0x12)), (1, Alpha(accent, 0))),
        Radial(new Point(0.9, 0.98), 0.62, 0.58, (0, Alpha(accent, 0x46)), (0.45, Alpha(accent, 0x1C)), (1, Alpha(accent, 0))));

    /// <summary>
    /// <paramref name="element"/> comes in: from <paramref name="from"/> (a little below: what just changed; from the
    /// side: the next card), fading in, on the compositor.
    /// </summary>
    public static void Arrive(UIElement element, Vector3 from, int delay = 0)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = TimeSpan.FromMilliseconds(240);
        fade.DelayTime = TimeSpan.FromMilliseconds(delay);
        fade.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, from);
        slide.InsertKeyFrame(1, Vector3.Zero, ease);
        slide.Duration = TimeSpan.FromMilliseconds(360);
        slide.DelayTime = fade.DelayTime;
        slide.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
    }
}
