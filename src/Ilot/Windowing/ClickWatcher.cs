using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Ilot.Interop;

namespace Ilot.Windowing;

/// <summary>
/// The first click outside a window, anywhere on screen, without taking the keyboard from the app in use: a low-level
/// mouse hook, installed only while watching, which only looks (the click goes on to where it was meant). The window's
/// own popups (its menus, a list's drop-down: windows of their own) count as the window. Nothing runs in between.
/// </summary>
internal sealed class ClickWatcher : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly nint _window;
    private readonly NativeMethods.LowLevelMouseProc _callback; // Kept alive: native code holds a pointer to it.
    private nint _hook;
    private Action? _outside;

    public ClickWatcher(DispatcherQueue dispatcher, nint window)
    {
        _dispatcher = dispatcher;
        _window = window;
        _callback = OnMouse;
    }

    /// <summary>Calls <paramref name="outside"/> once, on the UI thread, at the first button pressed outside the window.</summary>
    public void Watch(Action outside)
    {
        _outside = outside;
        if (_hook == 0)
        {
            _hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL, Marshal.GetFunctionPointerForDelegate(_callback), NativeMethods.GetModuleHandle(null), 0);
            if (_hook == 0)
            {
                Debug.WriteLine($"[Clicks] Not followed: error {Marshal.GetLastPInvokeError()}");
            }
        }
    }

    public void Stop()
    {
        _outside = null;
        if (_hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    public void Dispose() => Stop();

    /// <summary>Called by Windows for every mouse event, everywhere: it notes a press, and decides later.</summary>
    private unsafe nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && _outside is not null && (uint)wParam is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
            or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN)
        {
            var mouse = (NativeMethods.MouseHookData*)lParam;
            var point = new NativeMethods.Point { X = mouse->X, Y = mouse->Y };
            _dispatcher.TryEnqueue(() => Pressed(point));
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Pressed(NativeMethods.Point point)
    {
        if (_outside is not { } outside || IsOurs(NativeMethods.WindowFromPoint(point)))
        {
            return;
        }

        Stop();
        outside();
    }

    /// <summary>The window (where it takes the mouse: its region), or one of this app's popups (a menu, a drop-down).</summary>
    private bool IsOurs(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        nint root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == _window || NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOTOWNER) == _window)
        {
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(root, out uint process);
        if (process != (uint)Environment.ProcessId)
        {
            return false;
        }

        string kind = NativeMethods.GetClassName(root);
        return kind.Contains("Popup", StringComparison.Ordinal) || kind == "#32768"; // XAML popups; a shell menu.
    }
}
