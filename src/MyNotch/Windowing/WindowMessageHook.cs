using System.Diagnostics;
using System.Runtime.InteropServices;
using MyNotch.Interop;

namespace MyNotch.Windowing;

/// <summary>
/// Subclasses a window to observe (and optionally answer) its messages.
/// </summary>
internal sealed class WindowMessageHook : IDisposable
{
    /// <summary>Returns a result to swallow the message, or <c>null</c> to let the window handle it.</summary>
    public delegate nint? MessageHandler(uint message, nint wParam, nint lParam);

    private readonly nint _hwnd;
    private readonly MessageHandler _handler;
    private readonly NativeMethods.WindowProc _windowProc; // Kept alive: native code holds a pointer to it.
    private nint _previousWindowProc;

    public WindowMessageHook(nint hwnd, MessageHandler handler)
    {
        _hwnd = hwnd;
        _handler = handler;
        _windowProc = WindowProc;
        _previousWindowProc = NativeMethods.SetWindowLongPtr(
            hwnd, NativeMethods.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_windowProc));
    }

    public void Dispose()
    {
        if (_previousWindowProc != 0)
        {
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_WNDPROC, _previousWindowProc);
            _previousWindowProc = 0;
        }
    }

    private nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (_handler(message, wParam, lParam) is { } result)
            {
                return result;
            }
        }
        catch (Exception ex)
        {
            // An exception must never unwind into native code.
            Debug.WriteLine($"[Window] message 0x{message:X4} failed: {ex}");
        }

        return NativeMethods.CallWindowProc(_previousWindowProc, hwnd, message, wParam, lParam);
    }
}
