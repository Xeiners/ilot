using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using MyNotch.Interop;
using Windows.Graphics;

namespace MyNotch.Windowing;

/// <summary>
/// Tells when the pointer leaves an area of the screen where the window lets the clicks through (the closed
/// notch's pill, drawn back into its handle under the pointer): the window gets no mouse there, so a low-level mouse
/// hook follows it, installed only meanwhile. It costs nothing while the mouse rests, and nothing at all once removed.
/// </summary>
internal sealed class PointerWatcher : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly NativeMethods.LowLevelMouseProc _callback; // Kept alive: native code holds a pointer to it.
    private nint _hook;
    private RectInt32 _area;
    private Action? _left;

    public PointerWatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = OnMouse;
    }

    /// <summary>Calls <paramref name="left"/> once, as soon as the pointer is out of <paramref name="area"/> (physical pixels).</summary>
    public void Watch(RectInt32 area, Action left)
    {
        _area = area;
        _left = left;
        if (_hook == 0)
        {
            _hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL, Marshal.GetFunctionPointerForDelegate(_callback), NativeMethods.GetModuleHandle(null), 0);
            if (_hook == 0)
            {
                Debug.WriteLine($"[Pointer] Not followed: error {Marshal.GetLastPInvokeError()}");
            }
        }
    }

    public void Stop()
    {
        _left = null;
        if (_hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    public void Dispose() => Stop();

    /// <summary>Called by Windows for every mouse event, everywhere: it must decide fast, and do nothing else.</summary>
    private unsafe nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && (uint)wParam == NativeMethods.WM_MOUSEMOVE && _left is { } left)
        {
            var mouse = (NativeMethods.MouseHookData*)lParam;
            bool inside = mouse->X >= _area.X && mouse->X < _area.X + _area.Width
                && mouse->Y >= _area.Y && mouse->Y < _area.Y + _area.Height;
            if (!inside)
            {
                _left = null;
                _dispatcher.TryEnqueue(() => left());
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }
}
