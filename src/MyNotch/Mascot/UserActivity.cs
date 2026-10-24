using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using MyNotch.Interop;

namespace MyNotch.Mascot;

/// <summary>
/// Whether the user is at the computer: how long since they last typed or moved the mouse (Windows keeps the time, it is
/// only read), and the next time they do (low-level hooks, installed only while waiting, removed at the first input).
/// Nothing runs in between: no polling.
/// </summary>
internal sealed class UserActivity : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardCallback; // Kept alive: native code holds pointers to them.
    private readonly NativeMethods.LowLevelMouseProc _mouseCallback;
    private nint _keyboardHook;
    private nint _mouseHook;
    private Action? _input;

    public UserActivity(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _keyboardCallback = OnInput;
        _mouseCallback = OnInput;
    }

    /// <summary>How long since the last key or mouse input, anywhere.</summary>
    public static TimeSpan IdleFor
    {
        get
        {
            var info = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
            return NativeMethods.GetLastInputInfo(ref info)
                ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time))
                : TimeSpan.Zero;
        }
    }

    /// <summary>Calls <paramref name="input"/> once, on the UI thread, at the next key or mouse input.</summary>
    public void WaitForInput(Action input)
    {
        _input = input;
        nint module = NativeMethods.GetModuleHandle(null);
        if (_keyboardHook == 0)
        {
            _keyboardHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL, Marshal.GetFunctionPointerForDelegate(_keyboardCallback), module, 0);
        }

        if (_mouseHook == 0)
        {
            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL, Marshal.GetFunctionPointerForDelegate(_mouseCallback), module, 0);
        }

        if (_keyboardHook == 0 || _mouseHook == 0)
        {
            Debug.WriteLine($"[Mascot] Input not followed: error {Marshal.GetLastPInvokeError()}");
        }
    }

    public void Stop()
    {
        _input = null;
        if (_keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }

        if (_mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
    }

    public void Dispose() => Stop();

    /// <summary>Called by Windows for every key and mouse event, everywhere: it only notes the first one.</summary>
    private nint OnInput(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && _input is { } input)
        {
            _input = null;
            _dispatcher.TryEnqueue(() =>
            {
                Stop();
                input();
            });
        }

        return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
    }
}
