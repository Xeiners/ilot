using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Ilot.Interop;

namespace Ilot.Windowing;

/// <summary>
/// Tells whether an app is running full screen on the notch's screen (game, video, presentation…).
/// </summary>
/// <remarks>
/// No polling: the system notifies when the foreground app changes and when its window moves or resizes (a video
/// switching to full screen in a browser). A maximized window is not full screen, even over an auto-hidden taskbar or
/// without a title bar (Discord, Spotify…), and the desktop, the taskbar and the task switcher never count. An app full
/// screen here stays so while the user works on another screen.
/// </remarks>
internal sealed class FullscreenWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    /// <summary>A window that keeps moving (some games, all along) is looked at all the same, at the latest after this.</summary>
    private const long MostWaitMs = 500;

    private static readonly HashSet<string> ShellClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame", "ForegroundStaging", "Windows.UI.Core.CoreWindow",
    ];

    private readonly NativeMethods.WinEventProc _callback; // Kept alive: the system holds a pointer to it.
    private readonly nint[] _hooks;
    private readonly DispatcherQueueTimer _debounce;
    private static readonly uint OwnProcess = (uint)Environment.ProcessId;
    private Windows.Graphics.RectInt32 _screen = DisplayArea.Primary.OuterBounds;
    private long _pendingSince;

    /// <summary>The app last found full screen on the notch's screen: it still is while the user works on another screen.</summary>
    private nint _cover;

    public FullscreenWatcher(DispatcherQueue dispatcher)
    {
        _debounce = dispatcher.CreateTimer();
        _debounce.Interval = Debounce;
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Evaluate();

        // Out-of-context hooks call back on this (UI) thread through its message loop.
        _callback = OnWinEvent;
        nint callback = Marshal.GetFunctionPointerForDelegate(_callback);
        const uint Flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;
        _hooks =
        [
            NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, 0, callback, 0, 0, Flags),
            NativeMethods.SetWinEventHook(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, 0, callback, 0, 0, Flags),
        ];
        IsFullscreen = Detect();
    }

    public bool IsFullscreen { get; private set; }

    public event EventHandler? Changed;

    /// <summary>The screen to watch: the notch's (whole monitor, physical pixels).</summary>
    public Windows.Graphics.RectInt32 Screen
    {
        get => _screen;
        set
        {
            if (!value.Equals(_screen))
            {
                _screen = value; // Another screen, or this one at another resolution (a game switching to its own): looks again.
                Schedule();
            }
        }
    }

    /// <summary>Another app came to the foreground (the user clicked it, switched to it…). Raised on the UI thread.</summary>
    public event EventHandler? ForegroundChanged;

    public void Dispose()
    {
        _debounce.Stop();
        foreach (nint hook in _hooks)
        {
            if (hook != 0)
            {
                NativeMethods.UnhookWinEvent(hook);
            }
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            ForegroundChanged?.Invoke(this, EventArgs.Empty);
        }

        // Location changes fire for every window, caret and cursor: only the foreground window matters, and the one full
        // screen here (it may leave full screen while the user works on another screen).
        if (eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE
            && (idObject != NativeMethods.OBJID_WINDOW || (hwnd != NativeMethods.GetForegroundWindow() && hwnd != _cover)))
        {
            return;
        }

        Schedule();
    }

    /// <summary>
    /// A window going full screen moves several times in a row: decide once it has settled. One that never settles (a
    /// game moving its window all along) is decided all the same, every half second at most.
    /// </summary>
    private void Schedule()
    {
        long now = Environment.TickCount64;
        if (!_debounce.IsRunning)
        {
            _pendingSince = now;
        }
        else if (now - _pendingSince >= MostWaitMs)
        {
            return; // Already overdue: it fires as planned.
        }

        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Looks again now (the notch moved to another screen).</summary>
    public void Recheck() => Evaluate();

    private void Evaluate()
    {
        bool fullscreen = Detect();
        if (fullscreen != IsFullscreen)
        {
            IsFullscreen = fullscreen;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool Detect()
    {
        nint foreground = NativeMethods.GetForegroundWindow();
        if (Fills(foreground, Screen) || ExclusiveHere(foreground))
        {
            _cover = foreground;
            return true;
        }

        // The user works on another screen (or in Ilot itself): the app full screen on this one is still in front here.
        if (_cover != 0 && !Here(foreground) && Fills(_cover, Screen) && NativeMethods.IsWindowVisible(_cover))
        {
            return true;
        }

        _cover = 0;
        return false;
    }

    /// <summary>
    /// A game in exclusive full screen on this screen: its window may not match the screen's size as last seen (it set its
    /// own resolution), Windows says so all the same.
    /// </summary>
    private bool ExclusiveHere(nint window) =>
        NativeMethods.SHQueryUserNotificationState(out int state) == 0
        && state == NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN
        && Here(window)
        && NativeMethods.GetWindowThreadProcessId(window, out uint process) != 0
        && process != OwnProcess
        && !ShellClasses.Contains(NativeMethods.GetClassName(window));

    /// <summary>Whether <paramref name="window"/> is an app's, on the notch's screen.</summary>
    private bool Here(nint window)
    {
        if (window == 0
            || NativeMethods.GetWindowThreadProcessId(window, out uint process) == 0
            || process == OwnProcess)
        {
            return false;
        }

        nint monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var bounds = info.Monitor;
        return bounds.Left == Screen.X && bounds.Top == Screen.Y
            && bounds.Right - bounds.Left == Screen.Width && bounds.Bottom - bounds.Top == Screen.Height;
    }

    /// <summary>
    /// Whether the app in front runs full screen on its own screen, whichever it is (a game on the second monitor too):
    /// the global shortcuts then let their keys through to it.
    /// </summary>
    public static bool ForegroundIsFullscreen()
    {
        nint window = NativeMethods.GetForegroundWindow();
        nint monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (window == 0 || monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var screen = info.Monitor;
        return Fills(window, new(screen.Left, screen.Top, screen.Right - screen.Left, screen.Bottom - screen.Top));
    }

    private static bool Fills(nint window, Windows.Graphics.RectInt32 screen)
    {
        try
        {
            if (window == 0
                || NativeMethods.GetWindowThreadProcessId(window, out uint process) == 0
                || process == OwnProcess
                || ShellClasses.Contains(NativeMethods.GetClassName(window))
                || !NativeMethods.GetWindowRect(window, out var rect))
            {
                return false;
            }

            // A maximized window with a title bar or a resizing frame is just maximized, even when it covers an auto-hidden
            // taskbar or a screen without one, and draws its own title bar (Discord, Spotify, VS Code…). Full screen, a
            // browser or a game drops both.
            long style = NativeMethods.GetWindowLongPtr(window, NativeMethods.GWL_STYLE);
            if (NativeMethods.IsZoomed(window) && (style & (NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME)) != 0)
            {
                return false;
            }

            return rect.Left <= screen.X
                && rect.Top <= screen.Y
                && rect.Right >= screen.X + screen.Width
                && rect.Bottom >= screen.Y + screen.Height;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Fullscreen] Detection failed: {ex.Message}");
            return false;
        }
    }
}
