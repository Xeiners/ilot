using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;

namespace MyNotch.Composition;

/// <summary>
/// Implicit animations driven by springs: attached to a visual, they animate every change XAML makes to it
/// (layout moving an element, an element entering or leaving the tree) on the compositor thread.
/// </summary>
public static class ImplicitMotion
{
    private const double KeyFramesPerSecond = 60;

    /// <summary>Whenever layout moves the element, it springs from its old place to the new one.</summary>
    public static ImplicitAnimationCollection Reflow(Compositor compositor, Spring spring)
    {
        var (duration, curve) = Bake(spring);
        var linear = compositor.CreateLinearEasingFunction();
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Target = "Offset";
        for (int i = 0; i < curve.Length; i++)
        {
            animation.InsertExpressionKeyFrame(
                (float)(i + 1) / (curve.Length + 1),
                $"this.StartingValue + (this.FinalValue - this.StartingValue) * {Format(curve[i])}",
                linear);
        }

        animation.InsertExpressionKeyFrame(1, "this.FinalValue", linear);
        animation.Duration = duration;

        var collection = compositor.CreateImplicitAnimationCollection();
        collection["Offset"] = animation;
        return collection;
    }

    /// <summary>Appearance: fades in while it grows from <paramref name="fromScale"/> on a spring.</summary>
    public static CompositionAnimationGroup Show(Compositor compositor, Spring spring, float fromScale)
    {
        var (duration, curve) = Bake(spring);
        var linear = compositor.CreateLinearEasingFunction();
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Target = "Scale";
        scale.InsertKeyFrame(0, new Vector3(fromScale, fromScale, 1), linear);
        for (int i = 0; i < curve.Length; i++)
        {
            float s = fromScale + (1 - fromScale) * (float)curve[i];
            scale.InsertKeyFrame((float)(i + 1) / (curve.Length + 1), new Vector3(s, s, 1), linear);
        }

        scale.InsertKeyFrame(1, Vector3.One, linear);
        scale.Duration = duration;

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity";
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1)));
        opacity.Duration = TimeSpan.FromMilliseconds(260);

        var group = compositor.CreateAnimationGroup();
        group.Add(scale);
        group.Add(opacity);
        return group;
    }

    /// <summary>Disappearance: shrinks to <paramref name="toScale"/> and fades out, quickly.</summary>
    public static CompositionAnimationGroup Hide(Compositor compositor, float toScale)
    {
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(1, 1));
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Target = "Scale";
        scale.InsertKeyFrame(1, new Vector3(toScale, toScale, 1), ease);
        scale.Duration = TimeSpan.FromMilliseconds(180);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = "Opacity";
        opacity.InsertKeyFrame(1, 0, ease);
        opacity.Duration = TimeSpan.FromMilliseconds(160);

        var group = compositor.CreateAnimationGroup();
        group.Add(scale);
        group.Add(opacity);
        return group;
    }

    /// <summary>Progress (0 → 1, overshoot included) of a spring released from rest, sampled at regular steps.</summary>
    private static (TimeSpan Duration, double[] Curve) Bake(Spring spring)
    {
        double duration = spring.SettlingTime(1, 0, 1e-3);
        int frames = Math.Clamp((int)Math.Ceiling(duration * KeyFramesPerSecond), 2, 120);
        var curve = new double[frames - 1];
        for (int i = 0; i < curve.Length; i++)
        {
            curve[i] = 1 - spring.Evaluate(duration * (i + 1) / frames, 1, 0).Displacement;
        }

        return (TimeSpan.FromSeconds(duration), curve);
    }

    // Expressions are parsed by the compositor: always invariant culture ("0.5", never "0,5").
    private static string Format(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
}
