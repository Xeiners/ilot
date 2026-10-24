using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Ilot.Interop;
using Ilot.Windowing;
using Windows.Graphics;
using Windows.UI;

namespace Ilot.Modules.Media.Lyrics;

/// <summary>
/// The sung line on the taskbar (see <see cref="TaskbarLyrics"/>): a see-through window with no frame, against the tray's
/// icons and above the taskbar, that never takes the focus nor has a button of its own in the taskbar. The line being
/// sung (brightening as it is sung) and the next one under it, flush right against the artwork, the playing app's icon on
/// its corner. Under the pointer it shows how wide the lines may go: its left edge, pulled, sets that (the right one stays
/// against the icons); clicked, it opens the notch. It fades in and out on the compositor.
/// </summary>
internal sealed class TaskbarLyricsWindow : Window
{
    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan HoverTime = TimeSpan.FromMilliseconds(160);

    // Its parts besides the lines, in logical pixels: the grip on the left, the gap before the artwork, the artwork, a
    // margin on the right.
    private const double GripWidth = 12;
    private const double Gap = 10;
    private const double ArtSize = 34;
    private const double RightMargin = 2;

    /// <summary>How much wider than its lines it is, in logical pixels.</summary>
    public const double Chrome = GripWidth + Gap + ArtSize + RightMargin;

    private readonly nint _hwnd;
    private readonly LyricsSurface _root;
    private readonly Border _frame; // Under the pointer: how wide the lines may go, and the grip to change it.
    private readonly Border _gripLine;
    private readonly Border _art;
    private readonly ImageBrush _cover;
    private readonly FontIcon _noCover;
    private readonly Image _badge;
    private readonly WindowMessageHook _messages;
    private float _shown; // Where the fade goes: 1 shown, 0 hidden.
    private bool _light; // On a light taskbar: dark ink.
    private bool _hovered;
    private bool _pressed; // Pressed on the lines: a click, once let go there.
    private (int Pointer, int Width, int Right)? _pulled; // The grip held: where the pointer, the width and the right edge were.

    public TaskbarLyricsWindow()
    {
        Title = "Ilot";
        Panel = new LyricsPanel
        {
            ShowsNextLine = true,
            FlushRight = true,
            Karaoke = true,
            NeighbourOpacity = 0.72,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(Panel, 1);

        _gripLine = new Border
        {
            Width = 3,
            Height = 16,
            Margin = new Thickness(4.5, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(1.5),
        };
        _frame = new Border
        {
            Margin = new Thickness(0, 6, 0, 6),
            CornerRadius = new CornerRadius(7),
            IsHitTestVisible = false,
            Child = _gripLine,
        };
        Grid.SetColumnSpan(_frame, 2);

        // The grip: the frame's left edge, pulled sideways.
        var grip = new GripZone { Background = new SolidColorBrush(Colors.Transparent) };
        grip.PointerPressed += OnGripPressed;
        grip.PointerMoved += OnGripMoved;
        grip.PointerReleased += (_, e) => EndPull(e);
        grip.PointerCaptureLost += (_, e) => EndPull(e);

        // The artwork, the playing app's icon on its lower left corner.
        _cover = new ImageBrush { Stretch = Stretch.UniformToFill };
        _noCover = new FontIcon { Glyph = "", FontSize = 14 };
        _art = new Border
        {
            Width = ArtSize,
            Height = ArtSize,
            CornerRadius = new CornerRadius(6),
            Child = _noCover,
        };
        _badge = new Image
        {
            Width = 15,
            Height = 15,
            Margin = new Thickness(-5, 0, 0, -3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
        };
        var artwork = new Grid
        {
            Width = ArtSize,
            Height = ArtSize,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Children = { _art, _badge },
        };
        Grid.SetColumn(artwork, 3);

        _root = new LyricsSurface { Background = new SolidColorBrush(Colors.Transparent) };
        foreach (double width in new[] { GripWidth, -1, Gap, ArtSize, RightMargin })
        {
            _root.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width),
            });
        }

        _root.Children.Add(_frame);
        _root.Children.Add(Panel);
        _root.Children.Add(artwork);
        _root.Children.Add(grip);
        _root.PointerEntered += (_, _) => Hover(true);
        _root.PointerExited += (_, _) => Hover(false);
        _root.PointerPressed += (_, e) => _pressed = _root.CapturePointer(e.Pointer);
        _root.PointerReleased += (_, _) =>
        {
            if (_pressed)
            {
                _pressed = false;
                _root.ReleasePointerCaptures();
                Clicked?.Invoke(this, EventArgs.Empty);
            }
        };
        _root.PointerCaptureLost += (_, _) => _pressed = false;
        Content = _root;
        ElementCompositionPreview.GetElementVisual(_root).Opacity = 0;
        ElementCompositionPreview.GetElementVisual(_frame).Opacity = 0;
        SystemBackdrop = new TransparentBackdrop();
        Tint(light: false);

        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        ConfigureChrome();
        _messages = new WindowMessageHook(_hwnd, (message, _, _) =>
            message == NativeMethods.WM_MOUSEACTIVATE ? NativeMethods.MA_NOACTIVATE : null); // A click never takes the focus.
        Closed += (_, _) => _messages.Dispose();
    }

    /// <summary>The lines, as the notch draws them (karaoke here).</summary>
    public LyricsPanel Panel { get; }

    /// <summary>Clicked: the notch opens on the player.</summary>
    public event EventHandler? Clicked;

    /// <summary>Its left edge pulled, and let go: its new width (the screen's pixels), to be kept.</summary>
    public event EventHandler<int>? Resized;

    /// <summary>How narrow and how wide its left edge may make it (the screen's pixels): the room it has.</summary>
    public (int Narrowest, int Widest) WidthRange { get; set; }

    /// <summary>Its left edge being pulled (nothing else moves it meanwhile).</summary>
    public bool IsResizing => _pulled is not null;

    /// <summary>The playing track's artwork; none: a note in its place.</summary>
    public void SetCover(ImageSource? cover)
    {
        _cover.ImageSource = cover;
        ShowArt();
    }

    /// <summary>The playing app's icon, on the artwork's corner; none: no badge.</summary>
    public void SetBadge(ImageSource? icon)
    {
        _badge.Source = icon;
        _badge.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Light on a dark taskbar, dark on a light one: the lines, the frame under the pointer, the note without artwork.</summary>
    public void Tint(bool light)
    {
        _light = light;
        var ink = light ? Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1C) : Colors.White;
        Panel.LineForeground = new SolidColorBrush(ink);
        _frame.Background = new SolidColorBrush(Color.FromArgb(light ? (byte)0x12 : (byte)0x14, ink.R, ink.G, ink.B));
        _gripLine.Background = new SolidColorBrush(Color.FromArgb(0x8C, ink.R, ink.G, ink.B));
        _noCover.Foreground = new SolidColorBrush(Color.FromArgb(0xB3, ink.R, ink.G, ink.B));
        ShowArt();
    }

    /// <summary>The artwork, or a faint square with a note when the track has none.</summary>
    private void ShowArt()
    {
        bool none = _cover.ImageSource is null;
        var ink = _light ? Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1C) : Colors.White;
        _art.Background = none ? new SolidColorBrush(Color.FromArgb(0x26, ink.R, ink.G, ink.B)) : _cover;
        _noCover.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Under the pointer (or while its edge is pulled): how wide the lines may go, and the grip, fade in.</summary>
    private void Hover(bool hovered)
    {
        _hovered = hovered;
        FadeFrame();
    }

    private void FadeFrame()
    {
        var visual = ElementCompositionPreview.GetElementVisual(_frame);
        var compositor = visual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, _hovered || IsResizing ? 1 : 0);
        fade.Duration = HoverTime;
        visual.StartAnimation("Opacity", fade);
    }

    private void OnGripPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true; // Not a click on the lines.
        if (NativeMethods.GetCursorPos(out var pointer) && ((UIElement)sender).CapturePointer(e.Pointer))
        {
            _pulled = (pointer.X, AppWindow.Size.Width, AppWindow.Position.X + AppWindow.Size.Width);
            FadeFrame();
        }
    }

    /// <summary>Pulled sideways: wider to the left, narrower to the right, within its room; the right edge stays.</summary>
    private void OnGripMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pulled is not { } pulled || !NativeMethods.GetCursorPos(out var pointer))
        {
            return;
        }

        var (narrowest, widest) = WidthRange;
        int width = Math.Clamp(pulled.Width + pulled.Pointer - pointer.X, narrowest, Math.Max(narrowest, widest));
        if (width != AppWindow.Size.Width)
        {
            AppWindow.MoveAndResize(new RectInt32(pulled.Right - width, AppWindow.Position.Y, width, AppWindow.Size.Height));
        }
    }

    private void EndPull(PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_pulled is null)
        {
            return;
        }

        _pulled = null;
        _root.ReleasePointerCaptures();
        foreach (var child in _root.Children)
        {
            child.ReleasePointerCaptures();
        }

        FadeFrame();
        Resized?.Invoke(this, AppWindow.Size.Width);
    }

    /// <summary>
    /// As the notch's: a popup with no frame, no corners, no shadow nor animation of its own, see-through wherever it
    /// does not paint; a tool window (no taskbar button) that is never activated, above the others.
    /// </summary>
    private void ConfigureChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        NativeMethods.UpdateStyle(
            _hwnd,
            NativeMethods.GWL_STYLE,
            add: NativeMethods.WS_POPUP,
            remove: NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU
                  | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX);
        NativeMethods.UpdateStyle(
            _hwnd,
            NativeMethods.GWL_EXSTYLE,
            add: NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
            remove: NativeMethods.WS_EX_APPWINDOW | NativeMethods.WS_EX_WINDOWEDGE | NativeMethods.WS_EX_CLIENTEDGE
                  | NativeMethods.WS_EX_STATICEDGE | NativeMethods.WS_EX_DLGMODALFRAME);
        NativeMethods.EnablePerPixelTransparency(_hwnd);
        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, NativeMethods.DWMSBT_NONE);
        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_DONOTROUND);
        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_BORDER_COLOR, NativeMethods.DWMWA_COLOR_NONE);
        NativeMethods.SetDwmAttribute(_hwnd, NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED, 1);
    }

    /// <summary>At <paramref name="bounds"/> (the screen's pixels), above the taskbar.</summary>
    public void Place(RectInt32 bounds)
    {
        AppWindow.MoveAndResize(bounds);
        KeepAbove();
    }

    /// <summary>Above the taskbar again: clicked, it comes back on top of what is on it.</summary>
    public void KeepAbove() => NativeMethods.SetWindowPos(
        _hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

    /// <summary>Shown without taking the focus, fading in.</summary>
    public void Appear()
    {
        AppWindow.Show(activateWindow: false);
        KeepAbove();
        Fade(1);
    }

    /// <summary>Fades out; then hidden (nothing drawn, nothing running).</summary>
    public void Disappear() => Fade(0, () => AppWindow.Hide());

    private void Fade(float to, Action? done = null)
    {
        _shown = to;
        var visual = ElementCompositionPreview.GetElementVisual(_root);
        var compositor = visual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, to, compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1)));
        fade.Duration = FadeTime;
        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", fade);
        batch.End();
        if (done is not null)
        {
            batch.Completed += (_, _) =>
            {
                if (_shown == to)
                {
                    done(); // Unless it was asked back meanwhile.
                }
            };
        }
    }
}

/// <summary>The lines' surface on the taskbar: the hand says a click opens the notch.</summary>
internal sealed partial class LyricsSurface : Grid
{
    public LyricsSurface()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}

/// <summary>The left edge of the lines' frame: pulled sideways, it sets how wide they may go.</summary>
internal sealed partial class GripZone : Grid
{
    public GripZone()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
