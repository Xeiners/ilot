using Microsoft.UI.Composition;

namespace Ilot.Composition;

/// <summary>
/// Small fluent wrapper over <see cref="ExpressionAnimation"/>: parameters must all be set before the animation
/// starts, which this builder makes impossible to get wrong.
/// </summary>
public readonly struct CompositionExpression
{
    private readonly ExpressionAnimation _animation;

    public CompositionExpression(Compositor compositor, string expression) =>
        _animation = compositor.CreateExpressionAnimation(expression);

    public CompositionExpression With(string name, CompositionObject reference)
    {
        _animation.SetReferenceParameter(name, reference);
        return this;
    }

    public CompositionExpression With(string name, float value)
    {
        _animation.SetScalarParameter(name, value);
        return this;
    }

    public CompositionExpression With(string name, double value) => With(name, (float)value);

    /// <summary>Binds <paramref name="property"/> of <paramref name="target"/> to the expression.</summary>
    public void Start(CompositionObject target, string property) => target.StartAnimation(property, _animation);
}
