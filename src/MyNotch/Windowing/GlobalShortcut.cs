using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using MyNotch.Interop;
using MyNotch.Modules.Clipboard;
using MyNotch.Settings;

namespace MyNotch.Windowing;

/// <summary>
/// A shortcut of the notch's, anywhere, kept in the settings (the clipboard's, Windows + V; the assistant's), and the
/// recording of a new one from the settings.
/// </summary>
/// <remarks>
/// Windows keeps Win+V (and most Win+ shortcuts) for itself: <c>RegisterHotKey</c> cannot have them. A low-level
/// keyboard hook sees the keys before Windows does and takes the shortcut (the way PowerToys remaps keys); recording
/// goes through it too, so any combination can be chosen. The hook only runs when a key is pressed, and is only there
/// while the shortcut is on (or one is being recorded). Two details make it seamless:
/// <list type="bullet">
/// <item>The key is swallowed down and up, so the app in use never sees it.</item>
/// <item>With Windows or Alt, a meaningless key is pressed right away: Windows then reads the modifier as part of a
/// combination, and does not open the Start menu (or a menu bar) when it is released.</item>
/// </list>
/// Windows does not let a hook see the keys sent to an app running as administrator: there, the shortcut does nothing.
/// </remarks>
public class GlobalShortcut : IDisposable
{
    // An unassigned key code: pressing it means nothing to any app.
    private const ushort Unassigned = 0xFF;

    private readonly DispatcherQueue _dispatcher;
    private readonly Func<string> _saved;
    private readonly Func<bool> _enabled;
    private readonly KeyGesture _fallback;
    private readonly string _name;
    private readonly NativeMethods.LowLevelKeyboardProc _callback; // Kept alive: native code holds a pointer to it.
    private nint _hook;
    private KeyGesture _gesture;
    private ushort _swallowing;
    private TaskCompletionSource<KeyGesture?>? _recording;

    /// <param name="name">What it is for, in the debug output.</param>
    /// <param name="saved">The shortcut as the settings keep it.</param>
    /// <param name="enabled">Whether it is on.</param>
    /// <param name="fallback">The shortcut when what is saved is not one.</param>
    /// <param name="watched">The settings that change it (the saved shortcut, and whether it is on).</param>
    protected GlobalShortcut(
        DispatcherQueue dispatcher, AppSettings settings, string name, Func<string> saved, Func<bool> enabled, KeyGesture fallback,
        params string[] watched)
    {
        _dispatcher = dispatcher;
        _name = name;
        _saved = saved;
        _enabled = enabled;
        _fallback = fallback;
        _callback = OnKey;
        _gesture = Gesture;
        settings.PropertyChanged += (_, e) =>
        {
            if (watched.Contains(e.PropertyName))
            {
                _gesture = Gesture;
                Update();
            }
        };
        Update();
    }

    /// <summary>Raised on the UI thread when the shortcut is pressed.</summary>
    public event EventHandler? Pressed;

    /// <summary>
    /// While set (the tour of the notch, asking for the shortcut to try it), pressing the shortcut calls this instead of
    /// <see cref="Pressed"/>, on the UI thread.
    /// </summary>
    public Action? Capture { get; set; }

    /// <summary>The shortcut in the settings (the default if what is saved is not one).</summary>
    public KeyGesture Gesture => KeyGesture.Parse(_saved()) ?? _fallback;

    /// <summary>
    /// Waits for the next combination pressed, anywhere, and returns it without letting it through (Escape cancels:
    /// <c>null</c>). The shortcut itself does nothing meanwhile.
    /// </summary>
    public Task<KeyGesture?> RecordAsync()
    {
        _recording?.TrySetResult(null);
        _recording = new TaskCompletionSource<KeyGesture?>();
        Update();
        return _recording.Task;
    }

    public void CancelRecording() => EndRecording(null);

    public void Dispose()
    {
        _recording?.TrySetResult(null);
        _recording = null;
        Unhook();
    }

    private void EndRecording(KeyGesture? gesture)
    {
        var recording = _recording;
        _recording = null;
        Update();
        recording?.TrySetResult(gesture);
    }

    /// <summary>The hook is there only while it has something to do.</summary>
    private void Update()
    {
        bool wanted = _enabled() || _recording is not null;
        if (wanted && _hook == 0)
        {
            _hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL, Marshal.GetFunctionPointerForDelegate(_callback), NativeMethods.GetModuleHandle(null), 0);
            if (_hook == 0)
            {
                Debug.WriteLine($"[{_name}] Shortcut not taken: error {Marshal.GetLastPInvokeError()}");
            }
        }
        else if (!wanted)
        {
            Unhook();
        }
    }

    private void Unhook()
    {
        if (_hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    /// <summary>Called by Windows for every key, everywhere: it must decide fast, and do nothing else.</summary>
    private unsafe nint OnKey(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var key = (NativeMethods.KeyboardHookData*)lParam;
            ushort virtualKey = (ushort)key->VirtualKey;
            bool injected = (key->Flags & NativeMethods.LLKHF_INJECTED) != 0;
            bool down = (uint)wParam is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            if (!injected && !KeyGesture.IsModifierKey(virtualKey))
            {
                if (!down && virtualKey == _swallowing)
                {
                    // The release of a key taken: the app never saw it go down.
                    _swallowing = 0;
                    return 1;
                }

                if (down && Take(new KeyGesture(PressedModifiers(), virtualKey)))
                {
                    _swallowing = virtualKey;
                    return 1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Whether this key press is for the notch: the shortcut, or the combination being recorded.</summary>
    private bool Take(KeyGesture pressed)
    {
        if (_recording is not null)
        {
            if (pressed.Key == NativeMethods.VK_ESCAPE && pressed.Modifiers == KeyModifiers.None)
            {
                _dispatcher.TryEnqueue(() => EndRecording(null));
                return true;
            }

            if (!pressed.IsValid)
            {
                return false; // A plain letter is still typing: wait for a real combination.
            }

            Cover(pressed);
            _dispatcher.TryEnqueue(() => EndRecording(pressed));
            return true;
        }

        // In a game or a video full screen, its keys are its own: the shortcut lets them through and does nothing.
        if (!_enabled() || pressed != _gesture || FullscreenWatcher.ForegroundIsFullscreen())
        {
            return false;
        }

        if (_swallowing != pressed.Key)
        {
            // Not on key repeat: once per press.
            Cover(pressed);
            _dispatcher.TryEnqueue(() =>
            {
                if (Capture is { } capture)
                {
                    capture();
                }
                else
                {
                    Pressed?.Invoke(this, EventArgs.Empty);
                }
            });
        }

        return true;
    }

    private static void Cover(KeyGesture pressed)
    {
        if (pressed.NeedsCover)
        {
            NativeMethods.PressKeys(Unassigned);
        }
    }

    private static KeyModifiers PressedModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (IsDown(NativeMethods.VK_CONTROL))
        {
            modifiers |= KeyModifiers.Ctrl;
        }

        if (IsDown(NativeMethods.VK_MENU))
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (IsDown(NativeMethods.VK_SHIFT))
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN))
        {
            modifiers |= KeyModifiers.Win;
        }

        return modifiers;

        static bool IsDown(ushort key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
    }
}
