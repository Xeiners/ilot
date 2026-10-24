using Windows.Foundation;

namespace MyNotch.Notch;

/// <summary>
/// A bubble hanging under the open notch (see <see cref="NotchContext.ShowBubble"/>). Disposing it draws it back
/// into the notch.
/// </summary>
public sealed class NotchBubble : IDisposable
{
    private readonly Action<NotchBubble> _end;
    private readonly Action<NotchBubble, Size> _resize;

    internal NotchBubble(Size size, Size? minimum, Size maximum, Action<NotchBubble> end, Action<NotchBubble, Size> resize)
    {
        Size = size;
        Minimum = minimum;
        Maximum = maximum;
        _end = end;
        _resize = resize;
    }

    /// <summary>Its size now (the user may change it by pulling a bottom corner).</summary>
    public Size Size { get; internal set; }

    /// <summary>The smallest the user may make it; <c>null</c>: it cannot be resized.</summary>
    public Size? Minimum { get; }

    public Size Maximum { get; }

    /// <summary>
    /// The user resized it: raised as they pull and once more when they let go, with its new size (a module may
    /// remember it; the notch may close before the release).
    /// </summary>
    public event EventHandler<Size>? Resized;

    public void Dispose() => _end(this);

    /// <summary>
    /// The module changes its size (what it holds grew or shrank): it springs there, within <see cref="Maximum"/>.
    /// Not while the user is pulling a corner.
    /// </summary>
    public void Resize(Size size) => _resize(this, size);

    internal void RaiseResized() => Resized?.Invoke(this, Size);
}
