using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Ilot.Interop;
using Microsoft.UI.Dispatching;
using Ilot.Notch;
using Ilot.Settings;
using Windows.Graphics;

namespace Ilot.Windowing;

/// <summary>
/// The host window: borderless, transparent, always on top, pinned to the top center of the primary display,
/// never activated by a click and absent from the taskbar and Alt+Tab. Only the notch receives the mouse;
/// the rest of the window clicks through to whatever is underneath.
/// </summary>
public sealed class NotchWindow : Window
{
    private const int MaxKeyboardTries = 4;
    private static readonly TimeSpan KeyboardRetryDelay = TimeSpan.FromMilliseconds(70);

    private readonly NotchView _view;
    private readonly NotchLayout _layout;
    private readonly nint _hwnd;
    private readonly WindowMessageHook _messageHook;
    private readonly AppSettings _settings;
    private readonly FullscreenWatcher _fullscreen;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly PointerWatcher _pointerWatcher;
    private readonly ClickWatcher _clickWatcher;
    private readonly DispatcherQueueTimer _keyboardRetry;
    private double _scale = 1;
    private RectInt32 _appliedRegion;
    private RectInt32[] _appliedSides = [];
    private bool _shown;
    private bool _concealed;
    private nint _keyboardOwner;
    private bool _typing;
    private int _keyboardTries;
    private nint _monitor;

    // The window hides itself (an app full screen, a satellite done): only then may it be hidden; and it is meant to be
    // seen (shown, not put out of sight): then it stays, whatever else wants it minimized or hidden.
    private bool _hiding;
    private bool _meantShown;
    private bool _summoned;
    private readonly bool _satellite;

    /// <param name="satellite">
    /// A satellite window: the search on another screen (see <see cref="ShowOn"/>, <see cref="Withdraw"/>). Hidden until
    /// asked for, never put out of sight by an app full screen.
    /// </param>
    public NotchWindow(NotchView view, NotchLayout layout, AppSettings settings, bool satellite = false)
    {
        _view = view;
        _satellite = satellite;
        _layout = layout;
        _settings = settings;
        Title = "Ilot";
        Content = view;
        SystemBackdrop = new TransparentBackdrop();

        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        ConfigureChrome();
        _messageHook = new WindowMessageHook(_hwnd, OnWindowMessage);

        _view.InputBoundsChanged += (_, _) => ApplyInputRegion();
        _view.ActivationRequested += (_, _) => NativeMethods.SetForegroundWindow(_hwnd);
        _view.KeyboardRequested += (_, _) => TakeKeyboard();
        _view.KeyboardReleased += (_, _) => ReturnKeyboard();
        _pointerWatcher = new PointerWatcher(DispatcherQueue);
        _view.PeekStarted += (_, bounds) => WatchPeek(bounds);
        _view.PeekEnded += (_, _) => _pointerWatcher.Stop();

        // Engaged (the user clicked in it), the notch stays open until a click outside, seen without taking the focus.
        _clickWatcher = new ClickWatcher(DispatcherQueue, _hwnd);
        _view.EngagedChanged += (_, _) =>
        {
            if (_view.IsEngaged)
            {
                _clickWatcher.Watch(_view.OnClickedOutside);
            }
            else
            {
                _clickWatcher.Stop();
            }
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                LoseKeyboard();
            }
        };

        // Should the window not have been activated (Windows refused it the foreground), its deactivation never comes:
        // another app coming to the foreground says it all the same.
        _keyboardRetry = DispatcherQueue.CreateTimer();
        _keyboardRetry.Interval = KeyboardRetryDelay;
        _keyboardRetry.IsRepeating = false;
        _keyboardRetry.Tick += (_, _) => RetryKeyboard();

        // Full screen apps: the notch withdraws into the screen edge, then the window hides.
        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            _meantShown = false;
            _hiding = true;
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            _hiding = false;
        };
        _fullscreen = new FullscreenWatcher(DispatcherQueue);
        _fullscreen.Changed += (_, _) => UpdateConcealment();
        _fullscreen.ForegroundChanged += (_, _) =>
        {
            if (_typing && NativeMethods.GetForegroundWindow() != _hwnd)
            {
                LoseKeyboard();
            }
        };

        // Called by a shortcut while out of sight (an app full screen): it shows, then goes out of sight again at rest.
        _view.Summoned += (_, _) => Summon();
        _view.InputBoundsChanged += (_, _) =>
        {
            if (_summoned && _view.IsResting)
            {
                _summoned = false;
                UpdateConcealment();
            }
        };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.HideInFullscreen))
            {
                UpdateConcealment();
            }
        };

        Closed += (_, _) =>
        {
            _fullscreen.Dispose();
            _messageHook.Dispose();
            _pointerWatcher.Dispose();
            _clickWatcher.Dispose();
        };
    }

    /// <summary>Raised when the user asks for the settings from the notch.</summary>
    public event EventHandler? SettingsRequested
    {
        add => _view.SettingsRequested += value;
        remove => _view.SettingsRequested -= value;
    }

    /// <summary>The tour of the notch (see <see cref="NotchView.StartTour"/>): it opens and walks through <paramref name="steps"/>.</summary>
    public void StartTour(IReadOnlyList<TourStep> steps) => _view.StartTour(steps);

    /// <summary>Shows the notch without taking focus, then plays its entrance.</summary>
    public void ShowNotch()
    {
        PlaceOn(PrimaryMonitor); // Home: the primary screen.
        _fullscreen.Recheck();
        _shown = true;
        _concealed = ShouldConceal;
        if (!_concealed)
        {
            _meantShown = true;
            AppWindow.Show(activateWindow: false);
            _view.Arrive();
        }
    }

    /// <summary>
    /// A module needs to type. The notch never activates on a click, so it takes the keyboard explicitly, and
    /// remembers the app that had it.
    /// </summary>
    private void TakeKeyboard()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != _hwnd)
        {
            _keyboardOwner = foreground;
        }

        _typing = true;
        _keyboardTries = 0;
        if (!TakeForeground())
        {
            _keyboardRetry.Start(); // Windows said no this time (it was busy elsewhere): once more, in a moment.
        }
    }

    /// <summary>Brings the notch to the foreground; <c>true</c> if it is there.</summary>
    private bool TakeForeground()
    {
        if (NativeMethods.SetForegroundWindow(_hwnd) && NativeMethods.GetForegroundWindow() == _hwnd)
        {
            return true;
        }

        // Windows only lets the app in use hand the keyboard over (after a shortcut, the notch was not clicked):
        // share its input queue for a moment, which makes the request its own.
        var foreground = NativeMethods.GetForegroundWindow();
        uint target = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        uint self = NativeMethods.GetCurrentThreadId();
        if (target != 0 && target != self && NativeMethods.AttachThreadInput(self, target, true))
        {
            NativeMethods.SetForegroundWindow(_hwnd);
            NativeMethods.AttachThreadInput(self, target, false);
        }

        return NativeMethods.GetForegroundWindow() == _hwnd;
    }

    private void RetryKeyboard()
    {
        if (!_typing || TakeForeground())
        {
            return;
        }

        if (++_keyboardTries < MaxKeyboardTries)
        {
            _keyboardRetry.Start();
        }
        else
        {
            LoseKeyboard(); // Not to be had: better closed than open and deaf.
        }
    }

    /// <summary>The keyboard went to another app (a click elsewhere…): whoever was typing is told.</summary>
    private void LoseKeyboard()
    {
        _keyboardRetry.Stop();
        _typing = false;
        _keyboardOwner = 0;
        _view.OnKeyboardLost();
    }

    /// <summary>Done typing: the app that had the keyboard gets it back, ready to paste.</summary>
    private void ReturnKeyboard()
    {
        _keyboardRetry.Stop();
        _typing = false;
        var owner = _keyboardOwner;
        _keyboardOwner = 0;
        if (owner != 0 && NativeMethods.GetForegroundWindow() == _hwnd)
        {
            NativeMethods.SetForegroundWindow(owner);
        }
    }

    private bool ShouldConceal => _settings.HideInFullscreen && _fullscreen.IsFullscreen;

    private void UpdateConcealment()
    {
        if (_satellite || _summoned)
        {
            return; // A satellite shows when asked; a notch called up while out of sight settles once at rest.
        }

        bool conceal = ShouldConceal;
        if (!_shown || conceal == _concealed)
        {
            return;
        }

        _concealed = conceal;
        _hideTimer.Stop();
        if (conceal)
        {
            _hideTimer.Interval = _view.Retreat() + TimeSpan.FromMilliseconds(50);
            _hideTimer.Start();
        }
        else
        {
            _meantShown = true;
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            _view.Arrive();
        }
    }

    private void ConfigureChrome()
    {
        try
        {
            AppWindow.IsShownInSwitchers = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Window] IsShownInSwitchers unsupported: {ex.Message}");
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // A pure popup: the presenter still leaves a 3 px dialog frame, which shows as a light line.
        NativeMethods.UpdateStyle(
            _hwnd,
            NativeMethods.GWL_STYLE,
            add: NativeMethods.WS_POPUP,
            remove: NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU
                  | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX);

        // Tool window: no taskbar button. No-activate: clicking the notch never steals focus from the app in use.
        NativeMethods.UpdateStyle(
            _hwnd,
            NativeMethods.GWL_EXSTYLE,
            add: NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
            remove: NativeMethods.WS_EX_APPWINDOW | NativeMethods.WS_EX_WINDOWEDGE | NativeMethods.WS_EX_CLIENTEDGE
                  | NativeMethods.WS_EX_STATICEDGE | NativeMethods.WS_EX_DLGMODALFRAME);

        // See-through: DWM honours the window's alpha, so only what the notch draws is visible
        // (the XAML side uses a fully transparent system backdrop).
        NativeMethods.EnablePerPixelTransparency(_hwnd);
        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, NativeMethods.DWMSBT_NONE);

        // No DWM decoration: square corners, no border, no open/close animation (the notch animates itself).
        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_DONOTROUND);
        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_BORDER_COLOR, NativeMethods.DWMWA_COLOR_NONE);
        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
    }

    /// <summary>
    /// Anchors the notch at the top centre of the screen <paramref name="monitor"/> (the primary one if it is gone),
    /// at that screen's scale.
    /// </summary>
    private void PlaceOn(nint monitor)
    {
        DisplayArea? display = null;
        if (monitor != 0)
        {
            try
            {
                display = DisplayArea.GetFromDisplayId(Win32Interop.GetDisplayIdFromMonitor(monitor));
            }
            catch (ArgumentException)
            {
                // Unplugged meanwhile.
            }
        }

        display ??= DisplayArea.Primary;
        _monitor = Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        var screen = display.OuterBounds; // Whole monitor, physical pixels: the notch sits on the very top edge.
        _scale = NativeMethods.GetScaleForMonitor(_monitor);

        int width = (int)Math.Ceiling(_layout.CanvasWidth * _scale);
        int height = (int)Math.Ceiling(_layout.CanvasHeight * _scale);
        int x = screen.X + (int)Math.Round((screen.Width - width) / 2.0);
        AppWindow.MoveAndResize(new RectInt32(x, screen.Y, width, height));
        _fullscreen.Screen = screen;

        _appliedRegion = default;
        _appliedSides = [];
        ApplyInputRegion();
    }

    /// <summary>The notch's home: the primary screen.</summary>
    /// <summary>The primary screen: the notch's.</summary>
    private static nint PrimaryMonitor => Win32Interop.GetMonitorFromDisplayId(DisplayArea.Primary.DisplayId);

    /// <summary>The screen the window is on (monitor handle).</summary>
    public nint Monitor => _monitor;

    /// <summary>
    /// A shortcut calls the notch while an app full screen has it out of sight: it shows all the same (it was asked
    /// for), and goes out of sight again once closed and left alone. In any case it comes to the very top: an app full
    /// screen is often always on top too, and the one shown last would stay over the notch (it opens, unseen behind).
    /// </summary>
    private void Summon()
    {
        if (_satellite || !_shown)
        {
            return;
        }

        _summoned = true;
        if (_concealed)
        {
            _hideTimer.Stop();
            _concealed = false;
            _meantShown = true;
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// A satellite (the search on another screen): shown at the top of <paramref name="monitor"/>, its notch still out of
    /// sight in the edge, ready to open.
    /// </summary>
    public void ShowOn(nint monitor)
    {
        _hideTimer.Stop();
        PlaceOn(monitor);
        _meantShown = true;
        AppWindow.Show(activateWindow: false);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE); // Over an app full screen too.
        _shown = true;
    }

    /// <summary>A satellite done: its notch withdraws into the screen edge, then the window hides.</summary>
    public void Withdraw()
    {
        _hideTimer.Stop();
        _hideTimer.Interval = _view.Retreat() + TimeSpan.FromMilliseconds(50);
        _hideTimer.Start();
    }

    /// <summary>
    /// The closed notch's pill went back into its handle under the pointer, letting the clicks through: the
    /// window no longer gets the mouse there, so the screen is watched until the pointer leaves where they were.
    /// </summary>
    private void WatchPeek(Windows.Foundation.Rect bounds)
    {
        if (ToScreen(bounds) is { } area)
        {
            _pointerWatcher.Watch(area, _view.OnPeekLeft);
        }
        else
        {
            _view.OnPeekLeft();
        }
    }

    /// <summary>Logical bounds in the window, in physical screen pixels; <c>null</c> if the window has no place.</summary>
    private RectInt32? ToScreen(Windows.Foundation.Rect bounds)
    {
        if (bounds.IsEmpty || !NativeMethods.GetWindowRect(_hwnd, out var window))
        {
            return null;
        }

        int left = window.Left + (int)Math.Floor(bounds.Left * _scale);
        int top = window.Top + (int)Math.Floor(bounds.Top * _scale);
        int right = window.Left + (int)Math.Ceiling(bounds.Right * _scale);
        int bottom = window.Top + (int)Math.Ceiling(bounds.Bottom * _scale);
        return new RectInt32(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Restricts the window to the notch with a window region: outside of it, clicks go straight to the windows
    /// underneath. The region also clips rendering, which is why the view publishes generous transitional bounds.
    /// </summary>
    private void ApplyInputRegion()
    {
        var bounds = _view.InputBounds;
        if (bounds.IsEmpty)
        {
            return;
        }

        // The notch, and apart from it the little notches beside the open one (see NotchView.SideInputBounds).
        var region = ToPixels(bounds);
        var sides = _view.SideInputBounds.Where(side => !side.IsEmpty).Select(ToPixels).ToArray();
        if (region.Equals(_appliedRegion) && sides.SequenceEqual(_appliedSides))
        {
            return;
        }

        nint handle = NativeMethods.CreateRectRgn(region.X, region.Y, region.X + region.Width, region.Y + region.Height);
        foreach (var side in sides)
        {
            nint extra = NativeMethods.CreateRectRgn(side.X, side.Y, side.X + side.Width, side.Y + side.Height);
            NativeMethods.CombineRgn(handle, handle, extra, NativeMethods.RGN_OR);
            NativeMethods.DeleteObject(extra);
        }

        if (NativeMethods.SetWindowRgn(_hwnd, handle, redraw: true) != 0)
        {
            _appliedRegion = region; // The system now owns the region handle.
            _appliedSides = sides;
        }
        else
        {
            NativeMethods.DeleteObject(handle);
        }
    }

    /// <summary>Logical bounds in the window, in its physical pixels.</summary>
    private RectInt32 ToPixels(Windows.Foundation.Rect bounds)
    {
        int left = (int)Math.Floor(bounds.Left * _scale);
        int top = (int)Math.Floor(bounds.Top * _scale);
        int right = (int)Math.Ceiling(bounds.Right * _scale);
        int bottom = (int)Math.Ceiling(bounds.Bottom * _scale);
        return new RectInt32(left, top, right - left, bottom - top);
    }

    private nint? OnWindowMessage(uint message, nint wParam, nint lParam)
    {
        // A search result's Explorer menu on screen: its owner-drawn submenus (Send to…) draw through this window.
        if (Modules.Search.ShellMenu.HandleMessage(message, wParam, lParam) is { } handled)
        {
            return handled;
        }

        switch (message)
        {
            case NativeMethods.WM_MOUSEACTIVATE:
                return NativeMethods.MA_NOACTIVATE;

            case NativeMethods.WM_WINDOWPOSCHANGING:
                KeepTopmost(lParam);
                if (_meantShown && !_hiding)
                {
                    KeepShown(lParam);
                }

                break;

            // « Show desktop » (Windows + D, three fingers down on a touchpad) minimizes or hides every window: the notch
            // is part of the screen, not a window like the others. Refused; and should it be minimized all the same, it
            // comes back at once, without taking the focus.
            case NativeMethods.WM_SYSCOMMAND when (wParam & 0xFFF0) == NativeMethods.SC_MINIMIZE:
                return 0;

            case NativeMethods.WM_SIZE when wParam == NativeMethods.SIZE_MINIMIZED && _meantShown:
                DispatcherQueue.TryEnqueue(() =>
                {
                    NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
                    NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                });
                break;

            case NativeMethods.WM_DPICHANGED:
            case NativeMethods.WM_DISPLAYCHANGE:
            case NativeMethods.WM_SETTINGCHANGE when wParam == NativeMethods.SPI_SETWORKAREA:
                // Resolution, scale or screens changed: re-anchor once the system is done (the primary screen if the notch's
                // is gone).
                DispatcherQueue.TryEnqueue(() => PlaceOn(_satellite ? _monitor : PrimaryMonitor));
                break;
        }

        return null;
    }

    /// <summary>
    /// Shown and meant to be: no one else hides it, nor parks it off screen as a minimized window (-32000, -32000).
    /// </summary>
    private static unsafe void KeepShown(nint lParam)
    {
        var position = (NativeMethods.WindowPos*)lParam;
        position->Flags &= ~NativeMethods.SWP_HIDEWINDOW;
        if (position->X == -32000 && position->Y == -32000)
        {
            position->Flags |= NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE;
        }
    }

    /// <summary>Whoever moves the window in the z-order (the system included), it stays above other windows.</summary>
    private static unsafe void KeepTopmost(nint lParam)
    {
        var position = (NativeMethods.WindowPos*)lParam;
        if ((position->Flags & NativeMethods.SWP_NOZORDER) == 0)
        {
            position->InsertAfter = NativeMethods.HWND_TOPMOST;
        }
    }
}
