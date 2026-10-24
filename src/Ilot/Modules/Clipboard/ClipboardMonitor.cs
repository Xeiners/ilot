using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Ilot.Interop;
using Ilot.Windowing;

namespace Ilot.Modules.Clipboard;

/// <summary>
/// Tells when the system clipboard changes. Windows notifies a hidden message-only window
/// (<c>AddClipboardFormatListener</c>): nothing is polled, nothing runs between two copies.
/// </summary>
/// <remarks>
/// An app often writes a copy in several steps (one format at a time); notifications are gathered for a moment, so
/// one copy is read once, complete.
/// </remarks>
internal sealed class ClipboardMonitor : IDisposable
{
    private static readonly TimeSpan Gathering = TimeSpan.FromMilliseconds(120);

    private readonly nint _hwnd;
    private readonly WindowMessageHook? _hook;
    private readonly DispatcherQueueTimer _gather;

    public ClipboardMonitor(DispatcherQueue dispatcher)
    {
        _gather = dispatcher.CreateTimer();
        _gather.Interval = Gathering;
        _gather.IsRepeating = false;
        _gather.Tick += (_, _) => Changed?.Invoke(this, EventArgs.Empty);

        // A predefined window class, so there is nothing to register; HWND_MESSAGE keeps it out of sight.
        _hwnd = NativeMethods.CreateWindowEx(0, "STATIC", "Ilot.Clipboard", 0, 0, 0, 0, 0, NativeMethods.HWND_MESSAGE, 0, 0, 0);
        if (_hwnd == 0)
        {
            Debug.WriteLine("[Clipboard] No message window: the history will not record copies.");
            return;
        }

        _hook = new WindowMessageHook(_hwnd, OnMessage);
        if (!NativeMethods.AddClipboardFormatListener(_hwnd))
        {
            Debug.WriteLine("[Clipboard] The clipboard listener was refused.");
        }
    }

    /// <summary>Raised on the UI thread once the clipboard holds something new.</summary>
    public event EventHandler? Changed;

    public void Dispose()
    {
        _gather.Stop();
        if (_hwnd != 0)
        {
            NativeMethods.RemoveClipboardFormatListener(_hwnd);
            _hook?.Dispose();
            NativeMethods.DestroyWindow(_hwnd);
        }
    }

    private nint? OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message != NativeMethods.WM_CLIPBOARDUPDATE)
        {
            return null;
        }

        _gather.Stop();
        _gather.Start();
        return 0;
    }
}
