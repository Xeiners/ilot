using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Ilot.Controls;
using Ilot.Localization;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Ilot.Modules.Study;

/// <summary>
/// A mind map as a canvas: drawn at its own size (its text always readable, however small the sheet), moved by dragging
/// it (or the touchpad), zoomed by the wheel towards the pointer (a pinch, « − » and « + » too; the zoom clicked shows it
/// whole); and written on: a pen, a highlighter, notes typed where clicked, an eraser, « Annuler » (Ctrl + Z), « Tout
/// effacer ». What is drawn is kept with the map (<see cref="MapInk"/>, in the map's own space), the course writing it a
/// moment later.
/// </summary>
internal sealed partial class MindMapCanvas : Grid
{
    private const double Around = 160; // Room around the map, to write in.
    private const double PenWidth = 2.5;
    private const double MarkerWidth = 14;
    private const double MarkerOpacity = 0.35;
    private const double EraseReach = 10;
    private const double Readable = 0.85; // The first view is never smaller than this: the text stays readable.
    private const float LeastZoom = 0.3f;
    private const float MostZoom = 3f;

    private readonly ScrollViewer _viewer;
    private readonly MapBoard _board;
    private readonly Canvas _ink = new() { IsHitTestVisible = false };
    private readonly TextBlock _zoom;
    private readonly Dictionary<Tool, MotionButton> _tools = [];
    private readonly List<(MotionButton Button, Ellipse Dot)> _swatches = [];
    private readonly Dictionary<object, UIElement> _shapes = []; // Each stroke's and note's drawing.
    private readonly Stack<Action> _undo = [];
    private readonly Brush _chosen = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private readonly Brush _clear = new SolidColorBrush(Colors.Transparent);
    private Color[] _colors = [];
    private MapInk _mapInk = new();
    private string? _key;
    private Size _mapSize;
    private Tool _tool = Tool.Move;
    private Color _color = Colors.White;
    private (Polyline Line, List<double> Points, bool Marker)? _drawing;
    private (Point From, double Left, double Top)? _panning;
    private bool _erasing;
    private (TextBox Box, InkNote Note, bool Fresh)? _typing;
    private bool _framing; // A new map waits to be framed, once the canvas has its size.
    private (double Left, double Top, float Zoom)? _aim; // Where the wheel is taking the view (it turns faster than it moves).
    private ToolStrip? _strip;

    // Takes the keys once the map is clicked (Ctrl + Z); nothing else on the canvas can.
    private readonly ContentControl _keys = new()
    {
        IsTabStop = true,
        UseSystemFocusVisuals = false,
        Width = 0,
        Height = 0,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private enum Tool
    {
        Move,
        Pen,
        Marker,
        Text,
        Eraser,
    }

    public MindMapCanvas()
    {
        _board = new MapBoard
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Colors.Transparent), // Taken by the pointer all over, around the map too.
        };
        _board.PointerPressed += OnPressed;
        _board.PointerMoved += OnMoved;
        _board.PointerReleased += (_, _) => EndPointer();
        _board.PointerCaptureLost += (_, _) => EndPointer();

        _viewer = new ScrollViewer
        {
            Content = _board,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = LeastZoom,
            MaxZoomFactor = MostZoom,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            IsTabStop = false,
        };
        _viewer.ViewChanged += (_, e) =>
        {
            ShowZoom();
            if (!e.IsIntermediate)
            {
                _aim = null;
            }
        };

        // The wheel zooms (the ScrollViewer would scroll): stopped on the board, taken on the whole canvas.
        _board.PointerWheelChanged += (_, e) =>
        {
            if (IsWheel(e))
            {
                e.Handled = true;
            }
        };
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnWheel), handledEventsToo: true);
        KeyDown += OnKey;
        _viewer.SizeChanged += (_, _) =>
        {
            if (_framing)
            {
                Frame(whole: false);
            }
        };
        Children.Add(_viewer);

        // Its corners rounded as the frame it sits in (on the compositor: the map may pass under them).
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this);
        var corners = visual.Compositor.CreateRoundedRectangleGeometry();
        corners.CornerRadius = new System.Numerics.Vector2(19);
        visual.Clip = visual.Compositor.CreateGeometricClip(corners);
        SizeChanged += (_, e) => corners.Size = new System.Numerics.Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);

        _zoom = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _strip = Toolbar();
        Children.Add(_strip);
        Children.Add(_keys);
        Choose(Tool.Move);
    }

    /// <summary>Something was drawn, written, erased or undone: the course keeps it (a moment later).</summary>
    public event EventHandler? InkChanged;

    /// <summary>A note is being typed: the notch must give the keyboard (in a window, it has it).</summary>
    public event EventHandler? TypingStarted;

    /// <summary>
    /// <paramref name="map"/> (at its own size, its <c>Width</c> and <c>Height</c> set), with what was drawn on it
    /// (<paramref name="ink"/>, kept under <paramref name="key"/>): seen anew, readable and centred, unless it is the same
    /// map again (the mascot's colour changed): then as it was.
    /// </summary>
    public void Show(FrameworkElement map, string key, MapInk ink, Color accent)
    {
        CommitTyping();
        bool same = key == _key;
        _key = key;
        _mapInk = ink;
        _mapSize = new Size(map.Width, map.Height);
        _colors = [Colors.White, accent, Color.FromArgb(0xFF, 0xFB, 0xD5, 0x3F), Color.FromArgb(0xFF, 0xF9, 0x8B, 0x8B), Color.FromArgb(0xFF, 0x5C, 0xC9, 0xF9)];
        PaintSwatches();

        _board.Children.Clear();
        _board.Width = _mapSize.Width + 2 * Around;
        _board.Height = _mapSize.Height + 2 * Around;
        map.HorizontalAlignment = HorizontalAlignment.Left;
        map.VerticalAlignment = VerticalAlignment.Top;
        map.Margin = new Thickness(Around);
        _board.Children.Add(map);
        _ink.Width = _board.Width;
        _ink.Height = _board.Height;
        _board.Children.Add(_ink);
        DrawInk();

        if (!same)
        {
            _undo.Clear();
            StudyPaint.Arrive(map, new System.Numerics.Vector3(0, 8, 0));
            _framing = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Frame(whole: false)); // Once laid out.
        }
    }

    /// <summary>A note being typed is kept as it is (another map shown, the sheet put away).</summary>
    public void CommitTyping()
    {
        if (_typing is not { } typing)
        {
            return;
        }

        _typing = null;
        _ink.Children.Remove(typing.Box);
        string text = typing.Box.Text.Trim();
        var note = typing.Note;
        if (typing.Fresh)
        {
            if (text.Length == 0)
            {
                return;
            }

            note.Text = text;
            Add(note);
            _undo.Push(() => Remove(note));
        }
        else if (text.Length == 0)
        {
            int at = _mapInk.Notes.IndexOf(note);
            Remove(note);
            _undo.Push(() => Add(note, at));
        }
        else
        {
            string before = note.Text;
            note.Text = text;
            Redraw(note);
            _undo.Push(() =>
            {
                note.Text = before;
                Redraw(note);
            });
        }

        InkChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── The view ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The map centred: whole if <paramref name="whole"/> (« Ajuster »), else as large as fits but never smaller than
    /// <see cref="Readable"/> (a large map is then moved around rather than shrunk), never larger than its size.
    /// </summary>
    private void Frame(bool whole)
    {
        double width = _viewer.ActualWidth;
        double height = _viewer.ActualHeight;
        if (width <= 0 || height <= 0 || _mapSize.Width <= 0)
        {
            return;
        }

        _framing = false;
        double fit = Math.Min(width / (_mapSize.Width + 48), height / (_mapSize.Height + 48));
        float zoom = (float)Math.Clamp(whole ? fit : Math.Max(fit, Readable), LeastZoom, 1);
        _viewer.ChangeView(
            (Around + _mapSize.Width / 2) * zoom - width / 2,
            (Around + _mapSize.Height / 2) * zoom - height / 2,
            zoom,
            disableAnimation: !whole);
    }

    /// <summary>Zoomed in or out by <paramref name="factor"/>, around the middle of what is seen.</summary>
    private void ZoomBy(double factor) => ZoomAt(new Point(_viewer.ActualWidth / 2, _viewer.ActualHeight / 2), factor);

    /// <summary>
    /// Zoomed by <paramref name="factor"/> around <paramref name="at"/> (in the canvas): what is there stays under it. From
    /// where the last turns of the wheel are taking it, not from where the view has got to yet.
    /// </summary>
    private void ZoomAt(Point at, double factor)
    {
        var (left, top, from) = _aim ?? (_viewer.HorizontalOffset, _viewer.VerticalOffset, _viewer.ZoomFactor);
        float to = (float)Math.Clamp(from * factor, LeastZoom, MostZoom);
        if (to == from)
        {
            return;
        }

        double x = (left + at.X) / from;
        double y = (top + at.Y) / from;
        _aim = (Math.Max(0, x * to - at.X), Math.Max(0, y * to - at.Y), to);
        _viewer.ChangeView(_aim.Value.Left, _aim.Value.Top, to, disableAnimation: true);
    }

    /// <summary>A mouse's wheel, turned (tilted, it moves the map sideways; a touchpad moves it).</summary>
    private bool IsWheel(PointerRoutedEventArgs e) =>
        e.Pointer.PointerDeviceType == PointerDeviceType.Mouse
        && !e.GetCurrentPoint(_viewer).Properties.IsHorizontalMouseWheel
        && !Within(e.OriginalSource as DependencyObject, _strip);

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!IsWheel(e))
        {
            return;
        }

        e.Handled = true;
        var point = e.GetCurrentPoint(_viewer);
        ZoomAt(point.Position, Math.Pow(1.15, point.Properties.MouseWheelDelta / 120.0));
    }

    /// <summary>Ctrl + Z: the last stroke, note or erasing undone (a note being typed undoes its own text).</summary>
    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Z && InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            Undo();
        }
    }

    /// <summary>The keys to the canvas (Ctrl + Z): the sheet in front, on the maps.</summary>
    public void FocusKeys()
    {
        if (_typing is null)
        {
            _keys.Focus(FocusState.Programmatic);
        }
    }

    private static bool Within(DependencyObject? element, DependencyObject? ancestor)
    {
        for (; element is not null && ancestor is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    private void ShowZoom() => _zoom.Text = $"{Math.Round(_viewer.ZoomFactor * 100).ToString(CultureInfo.InvariantCulture)} %";

    // ── Drawing, writing, erasing ───────────────────────────────────────────────────────────────────────────

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_ink);
        bool middle = point.Properties.IsMiddleButtonPressed;
        if (!point.Properties.IsLeftButtonPressed && !middle)
        {
            return;
        }

        if (_tool == Tool.Text && !middle)
        {
            Type(point.Position);
            e.Handled = true;
            return;
        }

        CommitTyping();
        _keys.Focus(FocusState.Pointer); // Ctrl + Z, from now on.
        if (!_board.CapturePointer(e.Pointer))
        {
            return;
        }

        e.Handled = true;
        if (_tool == Tool.Move || middle)
        {
            _panning = (e.GetCurrentPoint(_viewer).Position, _viewer.HorizontalOffset, _viewer.VerticalOffset);
        }
        else if (_tool == Tool.Eraser)
        {
            _erasing = true;
            EraseAt(point.Position);
        }
        else
        {
            bool marker = _tool == Tool.Marker;
            var line = Line(_color, marker);
            line.Points.Add(point.Position);
            line.Points.Add(new Point(point.Position.X + 0.1, point.Position.Y)); // A dot, if it goes no further.
            _ink.Children.Add(line);
            _drawing = (line, [Round(point.Position.X - Around), Round(point.Position.Y - Around)], marker);
        }
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_panning is { } panning)
        {
            var now = e.GetCurrentPoint(_viewer).Position;
            _viewer.ChangeView(panning.Left - (now.X - panning.From.X), panning.Top - (now.Y - panning.From.Y), null, disableAnimation: true);
        }
        else if (_erasing)
        {
            EraseAt(e.GetCurrentPoint(_ink).Position);
        }
        else if (_drawing is { } drawing)
        {
            var at = e.GetCurrentPoint(_ink).Position;
            var last = drawing.Line.Points[^1];
            if (Math.Abs(at.X - last.X) + Math.Abs(at.Y - last.Y) >= 1.5)
            {
                drawing.Line.Points.Add(at);
                drawing.Points.Add(Round(at.X - Around));
                drawing.Points.Add(Round(at.Y - Around));
            }
        }
    }

    /// <summary>Let go: a stroke drawn is kept (undone by « Annuler »); moving or erasing ends.</summary>
    private void EndPointer()
    {
        _panning = null;
        if (_erasing)
        {
            _erasing = false;
        }

        if (_drawing is { } drawing)
        {
            _drawing = null;
            _board.ReleasePointerCaptures();
            var points = drawing.Points.Count >= 4 ? drawing.Points : [drawing.Points[0], drawing.Points[1], drawing.Points[0] + 0.1, drawing.Points[1]];
            var stroke = new InkStroke(Hex(_color), drawing.Marker, points);
            _ink.Children.Remove(drawing.Line);
            Add(stroke);
            _undo.Push(() => Remove(stroke));
            InkChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _board.ReleasePointerCaptures();
    }

    /// <summary>Text: a click on a note edits it, elsewhere starts a new one there.</summary>
    private void Type(Point at)
    {
        CommitTyping();
        var note = _mapInk.Notes.LastOrDefault(each => _shapes.TryGetValue(each, out var shape) && Contains(shape, at));
        bool fresh = note is null;
        note ??= new InkNote { X = Round(at.X - Around), Y = Round(at.Y - Around - 12), Color = Hex(_color) };
        if (!fresh && _shapes.Remove(note, out var shown))
        {
            _ink.Children.Remove(shown);
        }

        var box = new TextBox
        {
            Text = note.Text,
            Style = (Style)Application.Current.Resources["NotchFieldTextBoxStyle"],
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Parse(note.Color)),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 140,
            MaxWidth = 280,
            PlaceholderText = Loc.T("Écrivez…"),
        };
        Canvas.SetLeft(box, note.X + Around);
        Canvas.SetTop(box, note.Y + Around);
        box.LostFocus += (_, _) => CommitTyping();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                CommitTyping();
            }
        };
        _ink.IsHitTestVisible = true; // The box takes the pointer while it is there.
        _ink.Children.Add(box);
        _typing = (box, note, fresh);
        TypingStarted?.Invoke(this, EventArgs.Empty);
        DispatcherQueue.TryEnqueue(() => box.Focus(FocusState.Programmatic));
    }

    /// <summary>Strokes and notes under the eraser go (« Annuler » brings them back).</summary>
    private void EraseAt(Point at)
    {
        var x = at.X - Around;
        var y = at.Y - Around;
        foreach (var stroke in _mapInk.Strokes.Where(stroke => Near(stroke, x, y)).ToList())
        {
            int index = _mapInk.Strokes.IndexOf(stroke);
            Remove(stroke);
            _undo.Push(() => Add(stroke, index));
            InkChanged?.Invoke(this, EventArgs.Empty);
        }

        foreach (var note in _mapInk.Notes.Where(note => _shapes.TryGetValue(note, out var shape) && Contains(shape, at)).ToList())
        {
            int index = _mapInk.Notes.IndexOf(note);
            Remove(note);
            _undo.Push(() => Add(note, index));
            InkChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Undo()
    {
        CommitTyping();
        if (_undo.TryPop(out var undo))
        {
            undo();
            InkChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>All this map's ink goes at once (« Annuler » brings it all back).</summary>
    private void ClearAll()
    {
        CommitTyping();
        if (_mapInk.IsEmpty)
        {
            return;
        }

        var strokes = _mapInk.Strokes.ToList();
        var notes = _mapInk.Notes.ToList();
        _mapInk.Strokes.Clear();
        _mapInk.Notes.Clear();
        DrawInk();
        _undo.Push(() =>
        {
            _mapInk.Strokes.AddRange(strokes);
            _mapInk.Notes.AddRange(notes);
            DrawInk();
        });
        InkChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── The ink, drawn ──────────────────────────────────────────────────────────────────────────────────────

    private void DrawInk()
    {
        _ink.Children.Clear();
        _shapes.Clear();
        _ink.IsHitTestVisible = false;
        foreach (var stroke in _mapInk.Strokes)
        {
            Show(stroke);
        }

        foreach (var note in _mapInk.Notes)
        {
            Show(note);
        }
    }

    private void Add(InkStroke stroke, int at = -1)
    {
        _mapInk.Strokes.Insert(at < 0 || at > _mapInk.Strokes.Count ? _mapInk.Strokes.Count : at, stroke);
        Show(stroke);
    }

    private void Add(InkNote note, int at = -1)
    {
        _mapInk.Notes.Insert(at < 0 || at > _mapInk.Notes.Count ? _mapInk.Notes.Count : at, note);
        Show(note);
    }

    private void Remove(object item)
    {
        if (item is InkStroke stroke)
        {
            _mapInk.Strokes.Remove(stroke);
        }
        else if (item is InkNote note)
        {
            _mapInk.Notes.Remove(note);
        }

        if (_shapes.Remove(item, out var shape))
        {
            _ink.Children.Remove(shape);
        }
    }

    private void Show(InkStroke stroke)
    {
        var line = Line(Parse(stroke.Color), stroke.Marker);
        for (int i = 0; i + 1 < stroke.Points.Count; i += 2)
        {
            line.Points.Add(new Point(stroke.Points[i] + Around, stroke.Points[i + 1] + Around));
        }

        _ink.Children.Insert(Math.Min(_mapInk.Strokes.IndexOf(stroke), _ink.Children.Count), line); // Under the notes.
        _shapes[stroke] = line;
    }

    private void Show(InkNote note)
    {
        var text = new TextBlock
        {
            Text = note.Text,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Parse(note.Color)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280,
            Padding = new Thickness(10, 5, 10, 6), // As the box it was typed in: it stays in place.
        };
        Canvas.SetLeft(text, note.X + Around);
        Canvas.SetTop(text, note.Y + Around);
        _ink.Children.Add(text);
        _shapes[note] = text;
    }

    private void Redraw(InkNote note)
    {
        if (_shapes.Remove(note, out var shape))
        {
            _ink.Children.Remove(shape);
        }

        Show(note);
    }

    private static Polyline Line(Color color, bool marker) => new()
    {
        Stroke = new SolidColorBrush(color),
        StrokeThickness = marker ? MarkerWidth : PenWidth,
        Opacity = marker ? MarkerOpacity : 1,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        IsHitTestVisible = false,
    };

    /// <summary>The stroke passes within reach of (<paramref name="x"/>, <paramref name="y"/>), in the map's space.</summary>
    private static bool Near(InkStroke stroke, double x, double y)
    {
        double reach = EraseReach + (stroke.Marker ? MarkerWidth / 2 : 0);
        for (int i = 0; i + 1 < stroke.Points.Count; i += 2)
        {
            if (Math.Abs(stroke.Points[i] - x) <= reach && Math.Abs(stroke.Points[i + 1] - y) <= reach)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(UIElement shape, Point at) =>
        shape is FrameworkElement element
        && at.X >= Canvas.GetLeft(element) && at.X <= Canvas.GetLeft(element) + element.ActualWidth
        && at.Y >= Canvas.GetTop(element) && at.Y <= Canvas.GetTop(element) + element.ActualHeight;

    private static double Round(double value) => Math.Round(value, 1);

    private static string Hex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color Parse(string hex) =>
        hex.Length == 9 && uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb)
            ? Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)
            : Colors.White;

    // ── The toolbar ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Floating at the bottom: the tools (move, pen, highlighter, text, eraser), the colours, « Annuler », « Tout effacer »,
    /// and the zoom (« − », how much, « + »; how much, clicked, shows the map whole).
    /// </summary>
    private ToolStrip Toolbar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        AddTool(Tool.Move, "\uE7C2", Loc.T("Déplacer la carte"));
        AddTool(Tool.Pen, "\uED63", Loc.T("Crayon"));
        AddTool(Tool.Marker, "\uE7E6", Loc.T("Surligneur"));
        AddTool(Tool.Text, "\uE97E", Loc.T("Écrire une note"));
        AddTool(Tool.Eraser, "\uE75C", Loc.T("Gomme"));
        row.Children.Add(Separator());
        for (int i = 0; i < 5; i++)
        {
            int index = i;
            var dot = new Ellipse { Width = 14, Height = 14 };
            var swatch = Button(dot, Loc.T("Couleur"));
            swatch.Click += (_, _) =>
            {
                _color = _colors[index];
                PaintSwatches();
                if (_tool is Tool.Move or Tool.Eraser)
                {
                    Choose(Tool.Pen); // A colour is chosen to draw with.
                }
            };
            _swatches.Add((swatch, dot));
            row.Children.Add(swatch);
        }

        row.Children.Add(Separator());
        var undo = Button(Glyph("\uE7A7"), Loc.T("Annuler (Ctrl+Z)"));
        undo.Click += (_, _) => Undo();
        row.Children.Add(undo);
        var clear = Button(Glyph("\uE74D"), Loc.T("Tout effacer sur cette carte"));
        clear.Click += (_, _) => ClearAll();
        row.Children.Add(clear);
        row.Children.Add(Separator());
        var smaller = Button(Glyph("\uE71F"), Loc.T("Dézoomer"));
        smaller.Click += (_, _) => ZoomBy(1 / 1.25);
        row.Children.Add(smaller);
        var fit = Button(_zoom, Loc.T("Voir toute la carte"));
        fit.Width = 52;
        fit.Click += (_, _) => Frame(whole: true);
        row.Children.Add(fit);
        var larger = Button(Glyph("\uE8A3"), Loc.T("Zoomer"));
        larger.Click += (_, _) => ZoomBy(1.25);
        row.Children.Add(larger);

        var strip = new ToolStrip
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 10, 10),
        };
        strip.Children.Add(new Border
        {
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(19),
            Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x1C, 0x1C, 0x1F)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer
            {
                Content = row,
                HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        });
        return strip;

        void AddTool(Tool tool, string glyph, string name)
        {
            var button = Button(Glyph(glyph), name);
            button.Click += (_, _) => Choose(tool);
            _tools[tool] = button;
            row.Children.Add(button);
        }
    }

    private void Choose(Tool tool)
    {
        CommitTyping();
        _tool = tool;
        foreach (var (each, button) in _tools)
        {
            button.Background = each == tool ? _chosen : _clear;
        }

        _board.Show(tool switch
        {
            Tool.Move => InputSystemCursorShape.SizeAll,
            Tool.Text => InputSystemCursorShape.IBeam,
            _ => InputSystemCursorShape.Cross,
        });
    }

    private void PaintSwatches()
    {
        if (_colors.Length == 0)
        {
            return;
        }

        if (!_colors.Contains(_color))
        {
            _color = _colors[0];
        }

        for (int i = 0; i < _swatches.Count && i < _colors.Length; i++)
        {
            var (button, dot) = _swatches[i];
            dot.Fill = new SolidColorBrush(_colors[i]);
            dot.Stroke = _colors[i] == _color ? new SolidColorBrush(Colors.White) : null;
            dot.StrokeThickness = 2;
            button.Background = _colors[i] == _color ? _chosen : _clear;
        }
    }

    private static MotionButton Button(UIElement content, string name)
    {
        var button = new MotionButton
        {
            Style = (Style)Application.Current.Resources["NotchPillButtonStyle"],
            Width = 30,
            Height = 30,
            MinWidth = 0,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            Content = content,
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private static FontIcon Glyph(string glyph) => new() { Glyph = glyph, FontSize = 13 };

    private static Rectangle Separator() => new()
    {
        Width = 1,
        Height = 18,
        Margin = new Thickness(5, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Fill = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
    };
}

/// <summary>The map's board: its cursor says what the pointer does there (move, draw, write).</summary>
internal sealed partial class MapBoard : Grid
{
    public void Show(InputSystemCursorShape cursor) => ProtectedCursor = InputSystemCursor.Create(cursor);
}

/// <summary>The canvas's toolbar: the plain arrow over it, whatever the tool.</summary>
internal sealed partial class ToolStrip : Grid
{
    public ToolStrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }
}
