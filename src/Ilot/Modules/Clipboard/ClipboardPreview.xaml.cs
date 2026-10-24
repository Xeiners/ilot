using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Ilot.Controls;
using Ilot.Localization;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Ilot.Modules.Clipboard;

/// <summary>
/// A copy shown larger, in a drop under the notch (see ClipboardView.Preview), to touch it up and copy it again. An
/// image: a pen to draw on it (to circle something), in a few colours, cropping (a frame traced over it, kept as
/// soon as it is let go), turning it a quarter (R) and mirroring it (M), each step undone with Ctrl+Z. A text (a link, a colour too): edited as a page. "Copier"
/// (Ctrl+Entrée; for an image, Entrée or Ctrl+C too) puts the result on the clipboard, where the history records it as
/// a new copy; unchanged, it is the copy itself again. Échap closes it (Espace too, for an image, as it opened).
/// </summary>
/// <remarks>The image is edited as pixels (see <see cref="ImagePixels"/>): what is shown is what is copied.</remarks>
public sealed partial class ClipboardPreview : UserControl
{
    // The room for the content: an image fits in it (its toolbar and margins around).
    private const double MaxImageWidth = 900;
    private const double MaxImageHeight = 520;
    private const double Chrome = 90;
    private const double Sides = 32;

    /// <summary>The pen's width on the screen (its stroke is that wide whatever the image's scale).</summary>
    private const double PenWidth = 5;

    /// <summary>Undo steps kept (each one is a whole image).</summary>
    private const int UndoSteps = 12;

    private static readonly Color[] InkColors =
    [
        Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A),
        Color.FromArgb(0xFF, 0xFF, 0xD6, 0x0A),
        Color.FromArgb(0xFF, 0x32, 0xD7, 0x4B),
        Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF),
        Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
    ];

    private static readonly SolidColorBrush Chosen = new(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Clear = new(Microsoft.UI.Colors.Transparent);

    private readonly ClipboardViewModel _viewModel;
    private readonly List<ImagePixels> _undo = [];
    private readonly List<Vector2> _points = [];
    private ImagePixels? _image;
    private WriteableBitmap? _bitmap;
    private Task _drawing = Task.CompletedTask;
    private bool _cropping;
    private Color _ink = InkColors[0];
    private Polyline? _stroke;
    private Rectangle? _frame;
    private Microsoft.UI.Xaml.Shapes.Path? _shade;
    private Point _from;
    private bool _pressed;
    private bool _copying;

    public ClipboardPreview(ClipboardEntry entry, ClipboardViewModel viewModel)
    {
        Entry = entry;
        _viewModel = viewModel;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.

        KindIcon.Glyph = entry.KindGlyph;
        TitleText.Text = entry.KindLabel;
        PreviewKeyDown += OnKeyDown; // Before the text field: Ctrl+Entrée copies, it is not a new line.
        if (entry.Kind == ClipboardKind.Image)
        {
            ImageStage.Visibility = Visibility.Visible;
            ImageTools.Visibility = Visibility.Visible;
            DetailText.Text = Loc.F("{0} × {1}", entry.ImageWidth, entry.ImageHeight);
            ShowInks();
            ShowTool();
            Ink.PointerPressed += OnInkPressed;
            Ink.PointerMoved += OnInkMoved;
            Ink.PointerReleased += OnInkReleased;
            Ink.PointerCaptureLost += (_, _) => _pressed = false;
            Ink.PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross); // Precise, to draw or frame.
            Ink.PointerExited += (_, _) => ProtectedCursor = null;
            _ = LoadImageAsync();
        }
        else
        {
            TextStage.Visibility = Visibility.Visible;
            Editor.Text = entry.Text ?? "";
            Editor.TextChanged += (_, _) => ShowCount();
            ShowCount();
        }
    }

    public ClipboardEntry Entry { get; }

    /// <summary>It asks to be closed (Échap, its cross, or copied).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The size it wants (the image's shape changed: cropped, undone).</summary>
    public event EventHandler<Size>? SizeWanted;

    /// <summary>The size for <paramref name="entry"/>: an image fitted in the room as it is shaped, a text as a page.</summary>
    public static Size SizeFor(ClipboardEntry entry) => entry.Kind == ClipboardKind.Image
        ? SizeFor(entry.ImageWidth, entry.ImageHeight)
        : new Size(660, 360);

    /// <summary>The smallest it may be pulled to.</summary>
    public static Size Minimum => new(480, 240);

    private static Size SizeFor(int width, int height)
    {
        double scale = Math.Min(1, Math.Min(MaxImageWidth / Math.Max(width, 1), MaxImageHeight / Math.Max(height, 1)));
        return new Size(
            Math.Max(Minimum.Width, Math.Ceiling(width * scale + Sides)),
            Math.Max(Minimum.Height, Math.Ceiling(height * scale + Chrome)));
    }

    /// <summary>Ready to type: the caret at the end of the text, or the keys for the image.</summary>
    public void TakeFocus()
    {
        if (Entry.Kind == ClipboardKind.Image)
        {
            Focus(FocusState.Programmatic);
        }
        else
        {
            Editor.Focus(FocusState.Programmatic);
            Editor.Select(Editor.Text.Length, 0);
        }
    }

    // ── The image ───────────────────────────────────────────────────────────────────────────────────────────

    private async Task LoadImageAsync()
    {
        try
        {
            _image = await ImagePixels.LoadAsync(Entry.ImagePath!);
            ShowImage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"[Clipboard] Preview unavailable: {ex.Message}");
            DetailText.Text = Loc.T("Image introuvable");
        }
    }

    /// <summary>Shows the pixels as they are now (a new bitmap only when the size changed).</summary>
    private void ShowImage()
    {
        if (_image is null)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != _image.Width || _bitmap.PixelHeight != _image.Height)
        {
            _bitmap = new WriteableBitmap(_image.Width, _image.Height);
            Picture.Source = _bitmap;
            Surface.Width = _image.Width;
            Surface.Height = _image.Height;
        }

        _image.CopyTo(_bitmap.PixelBuffer);
        _bitmap.Invalidate();
        DetailText.Text = Loc.F("{0} × {1}", _image.Width, _image.Height);
        UndoButton.IsEnabled = _undo.Count > 0;
    }

    /// <summary>Screen pixels per image pixel (the image fitted in its stage).</summary>
    private double ImageScale => _image is null || Fit.ActualWidth <= 0 ? 1 : Fit.ActualWidth / _image.Width;

    /// <summary>Before a step: the image as it was, to come back to.</summary>
    private void Remember()
    {
        _undo.Add(_image!.Clone());
        if (_undo.Count > UndoSteps)
        {
            _undo.RemoveAt(0);
        }
    }

    private void OnInkPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Ink);
        if (_image is null || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        _pressed = Ink.CapturePointer(e.Pointer);
        _from = point.Position;
        if (_cropping)
        {
            ShowFrame(_from, _from);
            return;
        }

        _points.Clear();
        _points.Add(ToVector(_from));
        _stroke = new Polyline
        {
            Stroke = new SolidColorBrush(_ink),
            StrokeThickness = PenWidth / ImageScale,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        _stroke.Points.Add(_from);
        Ink.Children.Add(_stroke);
    }

    private void OnInkMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        var at = Clamp(e.GetCurrentPoint(Ink).Position);
        if (_cropping)
        {
            ShowFrame(_from, at);
            return;
        }

        // A point at least a screen pixel away from the last one: a smooth line, not thousands of points.
        if (_stroke is not null && Vector2.Distance(_points[^1], ToVector(at)) * ImageScale >= 1)
        {
            _points.Add(ToVector(at));
            _stroke.Points.Add(at);
        }
    }

    private void OnInkReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        Ink.ReleasePointerCapture(e.Pointer);
        var at = Clamp(e.GetCurrentPoint(Ink).Position);
        if (_cropping)
        {
            HideFrame();
            Crop(_from, at);
            return;
        }

        if (_stroke is { } stroke)
        {
            _stroke = null;
            _drawing = DrawAsync(_drawing, [.. _points], (float)(PenWidth / ImageScale / 2), _ink, stroke);
        }
    }

    /// <summary>
    /// Lays the stroke into the pixels (after the one before, off the UI thread), then shows them and lets the line drawn
    /// on top go: it never blinks.
    /// </summary>
    private async Task DrawAsync(Task before, Vector2[] points, float radius, Color color, Polyline stroke)
    {
        await before;
        if (_image is not { } image)
        {
            return;
        }

        Remember();
        await Task.Run(() => image.DrawStroke(points, radius, color));
        ShowImage();
        Ink.Children.Remove(stroke);
    }

    /// <summary>Keeps the part framed (if it is a frame at all, not a click), undoable.</summary>
    private async void Crop(Point a, Point b)
    {
        await _drawing;
        if (_image is null)
        {
            return;
        }

        int left = (int)Math.Floor(Math.Min(a.X, b.X));
        int top = (int)Math.Floor(Math.Min(a.Y, b.Y));
        int width = (int)Math.Ceiling(Math.Max(a.X, b.X)) - left;
        int height = (int)Math.Ceiling(Math.Max(a.Y, b.Y)) - top;
        if (width * ImageScale < 8 || height * ImageScale < 8)
        {
            return;
        }

        Remember();
        _image = _image.Crop(left, top, width, height);
        ShowImage();
        SizeWanted?.Invoke(this, SizeFor(_image.Width, _image.Height));
    }

    /// <summary>The frame being traced: a light outline, the rest of the image dimmed.</summary>
    private void ShowFrame(Point a, Point b)
    {
        var rect = new Rect(a, b);
        double line = 1.5 / ImageScale;
        if (_frame is null)
        {
            _shade = new Microsoft.UI.Xaml.Shapes.Path { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false };
            _frame = new Rectangle { Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), IsHitTestVisible = false };
            Ink.Children.Add(_shade);
            Ink.Children.Add(_frame);
        }

        var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shade.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, _image!.Width, _image.Height) });
        shade.Children.Add(new RectangleGeometry { Rect = rect });
        _shade!.Data = shade;
        _frame.StrokeThickness = line;
        _frame.Width = rect.Width;
        _frame.Height = rect.Height;
        Canvas.SetLeft(_frame, rect.X);
        Canvas.SetTop(_frame, rect.Y);
    }

    private void HideFrame()
    {
        if (_frame is not null)
        {
            Ink.Children.Remove(_frame);
            Ink.Children.Remove(_shade);
            _frame = null;
            _shade = null;
        }
    }

    private Point Clamp(Point point) => _image is null
        ? point
        : new Point(Math.Clamp(point.X, 0, _image.Width), Math.Clamp(point.Y, 0, _image.Height));

    private static Vector2 ToVector(Point point) => new((float)point.X, (float)point.Y);

    private async void Undo()
    {
        await _drawing;
        if (_undo.Count == 0)
        {
            return;
        }

        var before = _image;
        _image = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        ShowImage();
        if (before is not null && (before.Width != _image.Width || before.Height != _image.Height))
        {
            SizeWanted?.Invoke(this, SizeFor(_image.Width, _image.Height));
        }
    }

    // ── Tools ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The pen's colours: a dot each, the one in use ringed.</summary>
    private void ShowInks()
    {
        Inks.Children.Clear();
        foreach (var color in InkColors)
        {
            var dot = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(color == _ink && !_cropping ? Microsoft.UI.Colors.White : Color.FromArgb(0x40, 0, 0, 0)),
                StrokeThickness = color == _ink && !_cropping ? 2 : 1,
            };
            var button = new MotionButton { Width = 24, Height = 24, Content = dot };
            AutomationProperties.SetName(button, Loc.T("Couleur du crayon"));
            button.Click += (_, _) =>
            {
                _ink = color;
                _cropping = false;
                ShowTool();
                ShowInks();
            };
            Inks.Children.Add(button);
        }
    }

    private void ShowTool()
    {
        PenButton.Background = _cropping ? Clear : Chosen;
        CropButton.Background = _cropping ? Chosen : Clear;
        PenIcon.Foreground = new SolidColorBrush(_cropping ? Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF) : _ink);
    }

    private void OnPenClick(object sender, RoutedEventArgs e)
    {
        _cropping = false;
        ShowTool();
        ShowInks();
    }

    private void OnCropClick(object sender, RoutedEventArgs e)
    {
        _cropping = true;
        ShowTool();
        ShowInks();
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void OnRotateClick(object sender, RoutedEventArgs e) => Transform(image => image.Rotate());

    private void OnMirrorClick(object sender, RoutedEventArgs e) => Transform(image => image.Mirror());

    /// <summary>The whole image turned or mirrored (off the UI thread), undoable; the drop takes its new shape.</summary>
    private async void Transform(Func<ImagePixels, ImagePixels> change)
    {
        await _drawing;
        if (_image is not { } image)
        {
            return;
        }

        Remember();
        _image = await Task.Run(() => change(image));
        ShowImage();
        if (_image.Width != image.Width || _image.Height != image.Height)
        {
            SizeWanted?.Invoke(this, SizeFor(_image.Width, _image.Height));
        }
    }

    // ── The text ────────────────────────────────────────────────────────────────────────────────────────────

    private void ShowCount() => DetailText.Text = Loc.Count(Editor.Text.Length, "caractère", "caractères");

    // ── Copying, closing ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The result on the clipboard: "Copié" on the button for a moment, then it closes.</summary>
    private async Task CopyAsync()
    {
        if (_copying)
        {
            return;
        }

        _copying = true;
        bool copied;
        if (Entry.Kind == ClipboardKind.Image)
        {
            await _drawing;
            copied = _image is not null && (_undo.Count == 0
                ? await _viewModel.CopyAsync(Entry)
                : await _viewModel.CopyEditedImageAsync(_image));
        }
        else
        {
            copied = await _viewModel.CopyEditedTextAsync(Entry, Editor.Text);
        }

        if (!copied)
        {
            _copying = false;
            return;
        }

        CopyIcon.Glyph = "";
        CopyText.Text = Loc.T("Copié");
        await Task.Delay(650);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e) => await CopyAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool image = Entry.Kind == ClipboardKind.Image;
        switch (e.Key)
        {
            case VirtualKey.Escape:
            case VirtualKey.Space when image:
                e.Handled = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;

            case VirtualKey.Enter when control || image:
            case VirtualKey.C when control && image:
                e.Handled = true;
                await CopyAsync();
                break;

            case VirtualKey.Z when control && image:
                e.Handled = true;
                Undo();
                break;

            case VirtualKey.R when image && !control:
                e.Handled = true;
                Transform(picture => picture.Rotate());
                break;

            case VirtualKey.M when image && !control:
                e.Handled = true;
                Transform(picture => picture.Mirror());
                break;
        }
    }
}

