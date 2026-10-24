using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Composition;

namespace Ilot.Composition;

/// <summary>
/// One scalar of a <see cref="CompositionPropertySet"/> driven by interruptible springs.
/// </summary>
/// <remarks>
/// Composition has no "retarget and keep the velocity" primitive. Each <see cref="AnimateTo"/> therefore bakes the
/// analytic spring solution into a keyframe animation that runs on the compositor thread. Keyframes are written
/// relative to <c>this.StartingValue</c>, so a new target always starts exactly where the compositor is, and the
/// velocity tracked by the analytic model carries over: reversing a hover mid-flight feels physical instead of
/// restarting from rest.
/// </remarks>
public sealed class SpringChannel
{
    private const double KeyFramesPerSecond = 90;
    private const int MaxKeyFrames = 120;

    private readonly CompositionPropertySet _properties;
    private readonly CompositionEasingFunction _linear;

    // Analytic model of the animation currently running on the compositor.
    private double _origin;
    private double _target;
    private double _velocity;
    private Spring _spring;
    private double _duration;
    private long _startTimestamp;

    public SpringChannel(CompositionPropertySet properties, string name, double initialValue)
    {
        _properties = properties;
        _linear = properties.Compositor.CreateLinearEasingFunction();
        Name = name;
        _origin = _target = initialValue;
        properties.InsertScalar(name, (float)initialValue);
    }

    public string Name { get; }

    /// <summary>Jumps to <paramref name="value"/> without animating.</summary>
    public void Snap(double value)
    {
        _properties.StopAnimation(Name);
        _properties.InsertScalar(Name, (float)value);
        _origin = _target = value;
        _velocity = 0;
        _duration = 0;
    }

    /// <summary>Gives the channel a kick (units per second) and lets it spring back to its current target.</summary>
    public TimeSpan Kick(double velocity, Spring spring) => AnimateTo(_target, spring, velocity);

    /// <summary>Springs towards <paramref name="target"/>, preserving the current velocity.</summary>
    /// <returns>How long the motion takes to settle.</returns>
    public TimeSpan AnimateTo(double target, Spring spring, double impulse = 0)
    {
        var (value, velocity) = Sample();
        velocity += impulse;

        double displacement = value - target;
        double tolerance = Math.Max(1e-4, 1e-3 * Math.Max(Math.Abs(displacement), Math.Abs(velocity) / spring.AngularFrequency));
        if (Math.Abs(displacement) <= tolerance && Math.Abs(velocity) <= tolerance * spring.AngularFrequency)
        {
            Snap(target);
            return TimeSpan.Zero;
        }

        double duration = spring.SettlingTime(displacement, velocity, tolerance);
        int frames = Math.Clamp((int)Math.Ceiling(duration * KeyFramesPerSecond), 2, MaxKeyFrames);

        var animation = _properties.Compositor.CreateScalarKeyFrameAnimation();
        for (int i = 1; i < frames; i++)
        {
            double t = duration * i / frames;
            // x(t) = target + (x0 - target)·f(t) + v0·g(t): linear in the starting value, so it can be
            // expressed against this.StartingValue and still carry the velocity of the motion it replaces.
            double f = spring.Evaluate(t, 1, 0).Displacement;
            double g = spring.Evaluate(t, 0, 1).Displacement;
            double offset = target * (1 - f) + velocity * g;
            animation.InsertExpressionKeyFrame(
                (float)i / frames,
                $"this.StartingValue * ({Format(f)}) + ({Format(offset)})",
                _linear);
        }

        animation.InsertKeyFrame(1f, (float)target, _linear);
        animation.Duration = TimeSpan.FromSeconds(duration);
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        _properties.StartAnimation(Name, animation);

        _origin = value;
        _target = target;
        _velocity = velocity;
        _spring = spring;
        _duration = duration;
        _startTimestamp = Stopwatch.GetTimestamp();
        return animation.Duration;
    }

    /// <summary>
    /// The value the compositor showed <paramref name="ago"/> (read from the analytic model, the compositor is not
    /// asked): looking a little back covers the frame or two it starts later than the model.
    /// </summary>
    public double ValueAgo(TimeSpan ago) => Sample(ago).Value;

    private (double Value, double Velocity) Sample(TimeSpan ago = default)
    {
        if (_duration <= 0)
        {
            return (_target, 0);
        }

        double elapsed = Math.Max(0, (Stopwatch.GetElapsedTime(_startTimestamp) - ago).TotalSeconds);
        if (elapsed >= _duration)
        {
            return (_target, 0);
        }

        var (displacement, velocity) = _spring.Evaluate(elapsed, _origin - _target, _velocity);
        return (_target + displacement, velocity);
    }

    // Expressions are parsed by the compositor: always invariant culture ("0.5", never "0,5").
    private static string Format(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
}
