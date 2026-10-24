using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Ilot.Controls;
using Ilot.Interop;
using Ilot.Localization;
using Windows.Graphics;

namespace Ilot.Windowing;

/// <summary>
/// A sheet detached from the notch (a course's, a note's), in a window of its own drawn like the bubble it left: black,
/// its corners rounded the same, on a see-through window with no frame. Its handle at the top moves it, its bottom corners
/// resize it; its pin keeps it above the other windows, « Réduire » puts it in the taskbar. It opens where it was last put,
/// else where the bubble was, at its size. Its host listens to the sheet itself (« Rattacher », « Fermer »).
/// </summary>
public sealed partial class DetachedWindow : Window
{
    /// <summary>The bubble's corners (see ResizeGrip).</summary>
    public const double Radius = 32;

    private readonly nint _hwnd;
    private readonly WindowPlace _place;
    private readonly Windows.Foundation.Size _smallest;
    private readonly IDetachableSheet _sheet;
    private readonly MotionButton _pin;
    private readonly Brush _chosenBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private Pull? _pull;
    private bool _shown;

    private enum Edge
    {
        Move,
        Left,
        Right,
    }

    /// <summary>The handle or a corner held: from where the pointer was, the window as it was then.</summary>
    private sealed record Pull(Edge Edge, FrameworkElement Holder, NativeMethods.Point Start, RectInt32 From, double Scale);

    /// <param name="sheet">The sheet, an <see cref="IDetachableSheet"/>.</param>
    /// <param name="from">Where the bubble was, in the screen's pixels: the window takes its place the first time.</param>
    /// <param name="smallest">The smallest it can be pulled to (as the bubble's).</param>
    /// <param name="size">Its size when nothing else tells it.</param>
    public DetachedWindow(FrameworkElement sheet, WindowPlace place, RectInt32? from, Windows.Foundation.Size smallest, Windows.Foundation.Size size)
    {
        _sheet = sheet as IDetachableSheet ?? throw new ArgumentException("A detachable sheet is expected.", nameof(sheet));
        _place = place;
        _smallest = smallest;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Ilot.ico"));
        SystemBackdrop = new TransparentBackdrop();
        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        ConfigureChrome();
        Place(from, size);

        Holder.Content = sheet;
        _sheet.RoundCorners(Radius);
        _pin = Tool("\uE718");
        _pin.Click += (_, _) =>
        {
            place.OnTop = !place.OnTop;
            KeepOnTop();
        };
        var minimize = Tool("\uE921");
        ToolTipService.SetToolTip(minimize, Loc.T("Réduire"));
        AutomationProperties.SetName(minimize, Loc.T("Réduire"));
        minimize.Click += (_, _) => NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_MINIMIZE);
        _sheet.AddWindowTools(_pin, minimize);
        KeepOnTop();

        foreach (var (element, edge) in new (FrameworkElement, Edge)[] { (Handle, Edge.Move), (LeftGrip, Edge.Left), (RightGrip, Edge.Right) })
        {
            element.PointerPressed += (_, e) => StartPull(element, edge, e);
            element.PointerMoved += OnPulling;
            element.PointerReleased += (_, _) => EndPull();
            element.PointerCaptureLost += (_, _) => EndPull();
        }

        // The keys go to the sheet whenever the window is in front.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                return;
            }

            if (!_shown)
            {
                _shown = true;
                KeepOnTop(); // Once shown: showing it must not have undone its pin.
            }

            _sheet.FocusContent();
        };
        AppWindow.Changed += OnWindowChanged;
    }

    /// <summary>
    /// No frame, no title bar: a popup that DWM leaves see-through wherever the sheet does not paint (its rounded corners),
    /// with no border nor rounding of its own; still in the taskbar, so it can be put away there and found again.
    /// </summary>
    private void ConfigureChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }

        NativeMethods.UpdateStyle(
            _hwnd,
            NativeMethods.GWL_STYLE,
            add: NativeMethods.WS_POPUP | NativeMethods.WS_MINIMIZEBOX,
            remove: NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU | NativeMethods.WS_MAXIMIZEBOX);
        NativeMethods.UpdateStyle(
            _hwnd,
            NativeMethods.GWL_EXSTYLE,
            add: NativeMethods.WS_EX_APPWINDOW,
            remove: NativeMethods.WS_EX_WINDOWEDGE | NativeMethods.WS_EX_CLIENTEDGE | NativeMethods.WS_EX_STATICEDGE
                  | NativeMethods.WS_EX_DLGMODALFRAME);
        NativeMethods.EnablePerPixelTransparency(_hwnd);
        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, NativeMethods.DWMSBT_NONE);
        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_DONOTROUND);
        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_BORDER_COLOR, NativeMethods.DWMWA_COLOR_NONE);
    }

    /// <summary>A button of the window's, in the sheet's header: an icon in a round pill.</summary>
    private static MotionButton Tool(string glyph) => new()
    {
        Style = (Style)Application.Current.Resources["NotchPillButtonStyle"],
        Width = 28,
        MinWidth = 0,
        Padding = new Thickness(0),
        Content = new FontIcon { Glyph = glyph, FontSize = 10 },
    };

    /// <summary>Above the other windows, or not, as its pin says (lit while it is).</summary>
    private void KeepOnTop()
    {
        bool on = _place.OnTop;
        NativeMethods.SetWindowPos(
            _hwnd, on ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        _pin.Background = on ? _chosenBrush : (Brush)Application.Current.Resources["NotchPillBrush"];
        string tip = Loc.T(on ? "Ne plus garder au premier plan" : "Garder au premier plan");
        ToolTipService.SetToolTip(_pin, tip);
        AutomationProperties.SetName(_pin, tip);
    }

    // ── Moving, resizing ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The handle or a corner is held: from then on the window follows the pointer, read on the screen (the window moves
    /// under it).
    /// </summary>
    private void StartPull(FrameworkElement element, Edge edge, PointerRoutedEventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out var start) || !element.CapturePointer(e.Pointer))
        {
            return;
        }

        var (position, size) = (AppWindow.Position, AppWindow.Size);
        _pull = new Pull(edge, element, start, new RectInt32(position.X, position.Y, size.Width, size.Height), Root.XamlRoot?.RasterizationScale ?? 1);
        Light(element, true);
        e.Handled = true;
    }

    /// <summary>
    /// Moved by the handle; resized by a bottom corner, its top and its other side staying where they are, never smaller
    /// than it may be.
    /// </summary>
    private void OnPulling(object sender, PointerRoutedEventArgs e)
    {
        if (_pull is not { } pull || !NativeMethods.GetCursorPos(out var now))
        {
            return;
        }

        e.Handled = true;
        int dx = now.X - pull.Start.X;
        int dy = now.Y - pull.Start.Y;
        var from = pull.From;
        if (pull.Edge == Edge.Move)
        {
            AppWindow.Move(new PointInt32(from.X + dx, from.Y + dy));
            return;
        }

        int width = Math.Max((int)(_smallest.Width * pull.Scale), from.Width + (pull.Edge == Edge.Right ? dx : -dx));
        int height = Math.Max((int)(_smallest.Height * pull.Scale), from.Height + dy);
        int x = pull.Edge == Edge.Right ? from.X : from.X + from.Width - width;
        AppWindow.MoveAndResize(new RectInt32(x, from.Y, width, height));
    }

    private void EndPull()
    {
        if (_pull is not { } pull)
        {
            return;
        }

        _pull = null;
        pull.Holder.ReleasePointerCaptures();
        Light(pull.Holder, false);
    }

    private static void Light(FrameworkElement element, bool on)
    {
        switch (element)
        {
            case MoveHandle handle:
                handle.IsActive = on;
                handle.Light(on);
                break;

            case ResizeGrip grip:
                grip.IsActive = on;
                grip.Light(on);
                break;
        }
    }

    // ── Where it is ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Where it was last put, if that is still on a screen; else where the bubble was; else in the middle of the main
    /// screen. Never smaller than it may be, never larger than the screen.
    /// </summary>
    private void Place(RectInt32? from, Windows.Foundation.Size size)
    {
        foreach (var bounds in new[] { _place.Bounds, from })
        {
            if (bounds is { } rect && DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) is { } shown)
            {
                double scale = Scale(shown);
                var area = shown.WorkArea;
                int width = Math.Clamp(rect.Width, Math.Min((int)(_smallest.Width * scale), area.Width), area.Width);
                int height = Math.Clamp(rect.Height, Math.Min((int)(_smallest.Height * scale), area.Height), area.Height);
                int x = Math.Clamp(rect.X, area.X, area.X + area.Width - width);
                int y = Math.Clamp(rect.Y, area.Y, area.Y + area.Height - height);
                AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
                return;
            }
        }

        var display = DisplayArea.Primary;
        var work = display.WorkArea;
        double factor = Scale(display);
        int w = Math.Min((int)(size.Width * factor), work.Width);
        int h = Math.Min((int)(size.Height * factor), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }

    /// <summary>Moved or resized: kept for the next time (not while it is in the taskbar).</summary>
    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs e)
    {
        if ((e.DidPositionChange || e.DidSizeChange)
            && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored }
            && sender.Position.X > -30000)
        {
            var (position, size) = (sender.Position, sender.Size);
            _place.Bounds = new RectInt32(position.X, position.Y, size.Width, size.Height);
        }
    }

    /// <summary>
    /// Where <paramref name="element"/> is on the screen, in its pixels (the bubble a window takes the place of);
    /// <c>null</c> if that cannot be known.
    /// </summary>
    public static RectInt32? ScreenBounds(FrameworkElement element)
    {
        try
        {
            if (element.XamlRoot is not { ContentIslandEnvironment: { } island } root)
            {
                return null;
            }

            var host = AppWindow.GetFromWindowId(island.AppWindowId).Position;
            var origin = element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = root.RasterizationScale;
            return new RectInt32(
                host.X + (int)(origin.X * scale), host.Y + (int)(origin.Y * scale),
                (int)(element.ActualWidth * scale), (int)(element.ActualHeight * scale));
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static double Scale(DisplayArea display) =>
        NativeMethods.GetScaleForMonitor(Win32Interop.GetMonitorFromDisplayId(display.DisplayId));
}
