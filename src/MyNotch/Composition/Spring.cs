namespace MyNotch.Composition;

/// <summary>
/// A damped harmonic oscillator described with designer-friendly parameters, the way SwiftUI does it.
/// </summary>
/// <param name="Response">Period of the undamped oscillation, in seconds. Lower is snappier.</param>
/// <param name="DampingRatio">ζ: 1 is critically damped (no overshoot), below 1 bounces.</param>
public readonly record struct Spring(double Response, double DampingRatio)
{
    private const double MaxDuration = 3.0;
    private const double SettleStep = 1.0 / 240;

    public double AngularFrequency => 2 * Math.PI / Response;

    /// <summary>
    /// Solves the oscillator analytically: returns the displacement from rest and the velocity
    /// <paramref name="t"/> seconds after starting at <paramref name="displacement"/> with <paramref name="velocity"/>.
    /// </summary>
    public (double Displacement, double Velocity) Evaluate(double t, double displacement, double velocity)
    {
        double w0 = AngularFrequency;
        double zeta = DampingRatio;
        double d0 = displacement;
        double v0 = velocity;

        if (zeta < 0.9999)
        {
            // Under-damped: decaying oscillation.
            double a = zeta * w0;
            double wd = w0 * Math.Sqrt(1 - zeta * zeta);
            double b = (v0 + a * d0) / wd;
            double decay = Math.Exp(-a * t);
            double cos = Math.Cos(wd * t);
            double sin = Math.Sin(wd * t);
            return (decay * (d0 * cos + b * sin),
                    decay * ((b * wd - a * d0) * cos - (d0 * wd + a * b) * sin));
        }

        if (zeta <= 1.0001)
        {
            // Critically damped: fastest return without overshoot.
            double decay = Math.Exp(-w0 * t);
            double b = v0 + w0 * d0;
            return (decay * (d0 + b * t), decay * (v0 - w0 * b * t));
        }

        // Over-damped: two decaying exponentials.
        double root = Math.Sqrt(zeta * zeta - 1);
        double r1 = -w0 * (zeta - root);
        double r2 = -w0 * (zeta + root);
        double c1 = (v0 - r2 * d0) / (r1 - r2);
        double c2 = d0 - c1;
        double e1 = Math.Exp(r1 * t);
        double e2 = Math.Exp(r2 * t);
        return (c1 * e1 + c2 * e2, c1 * r1 * e1 + c2 * r2 * e2);
    }

    /// <summary>Time after which the motion stays within <paramref name="tolerance"/> of rest.</summary>
    public double SettlingTime(double displacement, double velocity, double tolerance)
    {
        double velocityTolerance = tolerance * AngularFrequency;
        double lastOutside = 0;

        for (double t = SettleStep; t <= MaxDuration; t += SettleStep)
        {
            var (d, v) = Evaluate(t, displacement, velocity);
            if (Math.Abs(d) > tolerance || Math.Abs(v) > velocityTolerance)
            {
                lastOutside = t;
            }
            else if (t - lastOutside > Response * 0.25)
            {
                break;
            }
        }

        return Math.Min(lastOutside + SettleStep, MaxDuration);
    }
}
