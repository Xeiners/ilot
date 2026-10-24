using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MyNotch.Windowing;

/// <summary>
/// A system backdrop that draws nothing: the desktop shows through everywhere the notch does not paint.
/// </summary>
/// <remarks>
/// <see cref="ICompositionSupportsSystemBackdrop"/> takes a brush from the <em>system</em> compositor
/// (Windows.UI.Composition), which in turn needs a Windows.System dispatcher queue on the UI thread.
/// </remarks>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? s_systemCompositor;
    private Windows.UI.Composition.CompositionColorBrush? _brush;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _brush ??= GetSystemCompositor().CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        connectedTarget.SystemBackdrop = _brush;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
    }

    private static Windows.UI.Composition.Compositor GetSystemCompositor()
    {
        if (s_systemCompositor is null)
        {
            DispatcherQueue.GetForCurrentThread().EnsureSystemDispatcherQueue();
            s_systemCompositor = new Windows.UI.Composition.Compositor();
        }

        return s_systemCompositor;
    }
}
