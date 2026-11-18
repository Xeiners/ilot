using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;
using Windows.UI;

namespace Ilot.Modules.Study;

/// <summary>How the words around formulas are set (see <see cref="StudyMathView.Render"/>).</summary>
internal sealed record MathStyle(
    FontFamily Font, double Size, Windows.UI.Text.FontWeight Weight, Brush Foreground, double LineHeight, bool Centered = false, bool Selectable = true);

/// <summary>
/// Formulas drawn as in a book: fractions over their bar, roots under their sign, indices and exponents in small, lowered
/// or raised, brackets as tall as what they hold; in Cambria (its italic for the variables). Laid out with plain panels,
/// no measuring of glyphs: each piece is cut to its capitals and baseline (TextLineBounds Tight) and centred on the line,
/// and whatever is drawn above or below a piece is matched on its other side, so baselines line up. A formula with a
/// fraction or a root gets a line of its own (a little plate, shrunk if too wide); a short one (C_A, 10⁻³) stays within
/// the words.
/// </summary>
internal static class StudyMathView
{
    private static readonly FontFamily MathFont = new("Cambria");

    /// <summary>Cambria looks smaller than Segoe UI at the same size: a little larger, to match the words.</summary>
    private const double MathScale = 1.08;

    /// <summary>
    /// <paramref name="text"/> with its formulas: its words wrapping, the short formulas within them, the tall ones on lines
    /// of their own. <paramref name="words"/> sets a run of words (the sheet's bold and italics), plain if not given;
    /// <paramref name="first"/> is a block that already holds the start (a definition's term).
    /// </summary>
    public static FrameworkElement Render(string text, MathStyle style, Action<InlineCollection, string>? words = null, TextBlock? first = null)
    {
        words ??= (inlines, run) => inlines.Add(new Run { Text = run });
        var pieces = StudyMath.Split(StudyMath.Repair(text));
        var panel = new StackPanel { Spacing = 6 };
        TextBlock? block = first;
        if (first is not null)
        {
            panel.Children.Add(first);
        }

        for (int i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            if (piece.IsMath && piece.Display)
            {
                panel.Children.Add(Plate(Formula(piece.Text, style.Size * 1.05, style.Foreground), style.Centered));
                block = null;
                continue;
            }

            if (piece.IsMath)
            {
                block ??= Add(panel, Words(style));
                AddInline(block.Inlines, piece.Text, style.Size);
                continue;
            }

            string run = Unpaired(piece.Text, i > 0 && pieces[i - 1].IsMath, i + 1 < pieces.Count && pieces[i + 1].IsMath);
            if (block is null)
            {
                run = run.TrimStart(' ', ',', ';', '.'); // After a formula on its own line: no line starting with its comma.
                if (run.Trim().Length == 0)
                {
                    continue;
                }

                block = Add(panel, Words(style));
            }

            words(block.Inlines, run);
        }

        return panel.Children.Count == 1 && panel.Children[0] is FrameworkElement only && only != first ? Detach(panel, only) : panel;
    }

    private static TextBlock Add(StackPanel panel, TextBlock block)
    {
        panel.Children.Add(block);
        return block;
    }

    private static FrameworkElement Detach(StackPanel panel, FrameworkElement only)
    {
        panel.Children.Clear();
        return only;
    }

    private static TextBlock Words(MathStyle style) => new()
    {
        FontFamily = style.Font,
        FontSize = style.Size,
        FontWeight = style.Weight,
        Foreground = style.Foreground,
        LineHeight = style.LineHeight,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = style.Centered ? TextAlignment.Center : TextAlignment.Left,
        IsTextSelectionEnabled = style.Selectable,
    };

    /// <summary>
    /// A « ** » cut off by a formula (« **$x$ est** ») would show as is: dropped, on the formula's side, while there is
    /// one too many.
    /// </summary>
    private static string Unpaired(string text, bool mathBefore, bool mathAfter)
    {
        int count = (text.Length - text.Replace("**", "", StringComparison.Ordinal).Length) / 2;
        if (count % 2 == 0)
        {
            return text;
        }

        if (mathAfter && text.TrimEnd().EndsWith("**", StringComparison.Ordinal))
        {
            return text.TrimEnd()[..^2] + (text.EndsWith(' ') ? " " : "");
        }

        if (mathBefore && text.TrimStart().StartsWith("**", StringComparison.Ordinal))
        {
            return text.TrimStart()[2..];
        }

        return text;
    }

    /// <summary>A short formula within words: its pieces as runs, indices and exponents in small characters.</summary>
    public static void AddInline(InlineCollection inlines, string latex, double size)
    {
        var node = StudyMath.Parse(latex);
        Runs(inlines, node, size * MathScale, small: false);
    }

    private static void Runs(InlineCollection inlines, MathNode node, double size, bool small)
    {
        switch (node)
        {
            case MathText text:
                inlines.Add(Run(text.Face == MathFace.Operator ? $"\u2005{text.Text}\u2005" : text.Text == "\n" ? " ; " : text.Text, text.Face, size, small));
                break;
            case MathRow row:
                for (int i = 0; i < row.Items.Count; i++)
                {
                    var item = row.Items[i];
                    if (item is MathText { Face: MathFace.Operator } op && Unary(row.Items, i))
                    {
                        inlines.Add(Run(op.Text, MathFace.Upright, size, small));
                        continue;
                    }

                    if (StudyMath.IsFunction(item) && i > 0 && row.Items[i - 1] is not MathText { Face: MathFace.Operator })
                    {
                        inlines.Add(Run("\u2009", MathFace.Upright, size, small));
                    }

                    Runs(inlines, item, size, small);
                    if (StudyMath.IsFunction(item) && i + 1 < row.Items.Count)
                    {
                        inlines.Add(Run("\u2009", MathFace.Upright, size, small));
                    }
                }

                break;
            case MathScripts scripts:
                Runs(inlines, scripts.Base, size, small);
                Script(inlines, scripts.Sub, StudyMath.AsSubscript, size);
                Script(inlines, scripts.Sup, StudyMath.AsSuperscript, size);
                break;
            case MathFence fence when !StudyMath.IsTall(fence.Body):
                inlines.Add(Run(fence.Open, MathFace.Upright, size, small));
                Runs(inlines, fence.Body, size, small);
                inlines.Add(Run(fence.Close, MathFace.Upright, size, small));
                break;
            default:
                inlines.Add(Run(StudyMath.Linear(node), MathFace.Upright, size, small));
                break;
        }
    }

    /// <summary>An index or an exponent within words: in index characters if it has them all (₀, ⁻³), else small.</summary>
    private static void Script(InlineCollection inlines, MathNode? script, Func<string, string?> convert, double size)
    {
        if (script is null)
        {
            return;
        }

        if (convert(StudyMath.Linear(script)) is { Length: > 0 } converted)
        {
            inlines.Add(Run(converted, MathFace.Upright, size, small: false));
        }
        else
        {
            Runs(inlines, script, size, small: true);
        }
    }

    private static Run Run(string text, MathFace face, double size, bool small) => new()
    {
        Text = text,
        FontFamily = MathFont,
        FontSize = small ? Math.Max(size * 0.7, 8) : size,
        FontStyle = face == MathFace.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
    };

    private static bool Unary(IReadOnlyList<MathNode> items, int index) =>
        items[index] is MathText { Text: "−" or "+" or "±" or "∓" }
        && (index == 0 || items[index - 1] is MathText { Face: MathFace.Operator } or MathText { Text: "(" or "[" or "{" or "," or ";" or "\n" });

    /// <summary>A formula on a line of its own: on a faint plate, shrunk to fit if it is wider than the line.</summary>
    public static Border Plate(FrameworkElement formula, bool centered) => new()
    {
        HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        Margin = new Thickness(0, 2, 0, 2),
        Padding = new Thickness(14, 10, 14, 11),
        CornerRadius = new CornerRadius(10),
        Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        Child = new Viewbox { StretchDirection = StretchDirection.DownOnly, Stretch = Stretch.Uniform, Child = formula },
    };

    /// <summary>A formula drawn (its LaTeX): several lines (« \\ ») one under the other.</summary>
    public static FrameworkElement Formula(string latex, double size, Brush foreground)
    {
        var node = StudyMath.Parse(StudyMath.Repair(latex));
        var builder = new Builder(foreground);
        var lines = StudyMath.Lines(node);
        if (lines.Count == 1)
        {
            return builder.Build(lines[0], size * MathScale, 0);
        }

        var stack = new StackPanel { Spacing = size * 0.6 };
        foreach (var line in lines)
        {
            stack.Children.Add(builder.Build(line, size * MathScale, 0));
        }

        return stack;
    }

    private sealed class Builder(Brush foreground)
    {
        public FrameworkElement Build(MathNode node, double size, int depth)
        {
            var element = node switch
            {
                MathRow row => Row(row.Items, size, depth),
                MathScripts scripts => Scripts(scripts, size, depth),
                MathFraction fraction => Fraction(fraction, size, depth),
                MathRoot root => Root(root, size, depth),
                MathFence fence => Fence(fence, size, depth),
                MathAccent accent => Accent(accent, size, depth),
                _ => Row([node], size, depth),
            };
            element.VerticalAlignment = VerticalAlignment.Center;
            return element;
        }

        /// <summary>Pieces side by side, centred on the line: the words of the formula as runs, the rest drawn.</summary>
        private FrameworkElement Row(IReadOnlyList<MathNode> items, double size, int depth)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var runs = new List<(string Text, MathFace Face)>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item is MathText { Face: not MathFace.Large } text)
                {
                    if (text.Face == MathFace.Operator && !Unary(items, i))
                    {
                        runs.Add(("\u2005" + text.Text + "\u2005", MathFace.Upright));
                    }
                    else
                    {
                        if (text.Face == MathFace.Function && i > 0 && items[i - 1] is not MathText { Face: MathFace.Operator })
                        {
                            runs.Add(("\u2009", MathFace.Upright));
                        }

                        runs.Add((text.Text, text.Face));
                        if (text.Face == MathFace.Function && i + 1 < items.Count)
                        {
                            runs.Add(("\u2009", MathFace.Upright));
                        }
                    }

                    continue;
                }

                Flush(row, runs, size);
                if (item is MathText large)
                {
                    row.Children.Add(Text([(large.Text, MathFace.Upright)], size * 1.35));
                    continue;
                }

                row.Children.Add(Build(item, size, depth));
            }

            Flush(row, runs, size);
            return row.Children.Count == 1 ? Single(row) : row;
        }

        private static FrameworkElement Single(StackPanel row)
        {
            var only = (FrameworkElement)row.Children[0];
            row.Children.Clear();
            return only;
        }

        /// <summary>
        /// The runs gathered so far, as one block. Spaces around an operator at its edge become margins: a block's
        /// trailing space is not laid out.
        /// </summary>
        private void Flush(StackPanel row, List<(string Text, MathFace Face)> runs, double size)
        {
            if (runs.Count == 0)
            {
                return;
            }

            double left = 0;
            double right = 0;
            if (runs[0].Text.StartsWith('\u2005'))
            {
                runs[0] = (runs[0].Text.TrimStart('\u2005'), runs[0].Face);
                left = size * 0.22;
            }

            if (runs[^1].Text.EndsWith('\u2005') || runs[^1].Text.EndsWith('\u2009'))
            {
                runs[^1] = (runs[^1].Text.TrimEnd('\u2005', '\u2009'), runs[^1].Face);
                right = size * 0.22;
            }

            var block = Text(runs, size);
            block.Margin = new Thickness(left, 0, right, 0);
            row.Children.Add(block);
            runs.Clear();
        }

        private TextBlock Text(IEnumerable<(string Text, MathFace Face)> runs, double size)
        {
            var block = new TextBlock
            {
                FontFamily = MathFont,
                FontSize = size,
                Foreground = foreground,
                TextLineBounds = TextLineBounds.Tight,
                VerticalAlignment = VerticalAlignment.Center,
                IsTextScaleFactorEnabled = false,
            };
            foreach (var (text, face) in runs)
            {
                block.Inlines.Add(new Run
                {
                    Text = text,
                    FontStyle = face == MathFace.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                });
            }

            return block;
        }

        /// <summary>
        /// Indices lowered, exponents raised, in small, after their base; as much room left above the base as below it,
        /// so the base stays on the line.
        /// </summary>
        private FrameworkElement Scripts(MathScripts scripts, double size, int depth)
        {
            double shift = size * 0.22;
            double small = Math.Max(size * 0.7, 9);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var baseElement = Build(scripts.Base, size, depth);
            baseElement.Margin = new Thickness(0, shift, 0, shift);
            grid.Children.Add(baseElement);

            if (scripts.Sub is { } sub)
            {
                var index = Build(sub, small, depth + 1);
                index.VerticalAlignment = VerticalAlignment.Bottom;
                index.Margin = new Thickness(size * 0.03, 0, 0, 0);
                Grid.SetColumn(index, 1);
                grid.Children.Add(index);
            }

            if (scripts.Sup is { } sup)
            {
                var power = Build(sup, small, depth + 1);
                power.VerticalAlignment = VerticalAlignment.Top;
                power.Margin = new Thickness(size * 0.05, 0, 0, 0);
                Grid.SetColumn(power, 1);
                grid.Children.Add(power);
            }

            return grid;
        }

        /// <summary>
        /// The numerator over its bar over the denominator, a little smaller when nested; both halves as tall, so the bar
        /// sits on the middle of the line.
        /// </summary>
        private FrameworkElement Fraction(MathFraction fraction, double size, int depth)
        {
            double inner = depth == 0 ? size : Math.Max(size * 0.82, 10);
            double gap = size * 0.3;
            var over = Build(fraction.Over, inner, depth + 1);
            var under = Build(fraction.Under, inner, depth + 1);
            over.VerticalAlignment = VerticalAlignment.Bottom;
            under.VerticalAlignment = VerticalAlignment.Top;
            over.HorizontalAlignment = under.HorizontalAlignment = HorizontalAlignment.Center;

            double half = Math.Max(Height(over), Height(under));
            var top = new Grid { MinHeight = half, Margin = new Thickness(size * 0.1, 0, size * 0.1, gap) };
            top.Children.Add(over);
            var bottom = new Grid { MinHeight = half, Margin = new Thickness(size * 0.1, gap, size * 0.1, 0) };
            bottom.Children.Add(under);
            var bar = new Rectangle
            {
                Height = Math.Max(1, size * 0.055),
                Fill = fraction.Rule ? foreground : null,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var grid = new Grid { Margin = new Thickness(size * 0.1, 0, size * 0.1, 0) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(bar, 1);
            Grid.SetRow(bottom, 2);
            grid.Children.Add(top);
            grid.Children.Add(bar);
            grid.Children.Add(bottom);
            return grid;
        }

        private static double Height(FrameworkElement element)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return element.DesiredSize.Height;
        }

        /// <summary>The root sign as tall as what it holds, its bar over it; as much room below as the bar takes above.</summary>
        private FrameworkElement Root(MathRoot root, double size, int depth)
        {
            double stroke = Math.Max(1, size * 0.06);
            double above = size * 0.2 + stroke;
            var body = new Border
            {
                BorderBrush = foreground,
                BorderThickness = new Thickness(0, stroke, 0, 0),
                Padding = new Thickness(size * 0.08, size * 0.2, size * 0.04, 0),
                Child = Build(root.Body, size, depth),
            };

            var figure = new PathFigure { StartPoint = new Point(0, 6.2), IsClosed = false };
            var lines = new PolyLineSegment();
            lines.Points.Add(new Point(1.8, 5.2));
            lines.Points.Add(new Point(4.4, 10));
            lines.Points.Add(new Point(10, 0));
            figure.Segments.Add(lines);
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            var sign = new Path
            {
                Data = geometry,
                Stretch = Stretch.Fill,
                Stroke = foreground,
                StrokeThickness = stroke,
                StrokeLineJoin = PenLineJoin.Round,
                Width = size * 0.6,
                Margin = new Thickness(root.Index is null ? 0 : size * 0.22, 0, 0, -size * 0.1),
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            var grid = new Grid { Margin = new Thickness(size * 0.05, 0, size * 0.05, above) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(body, 1);
            grid.Children.Add(sign);
            grid.Children.Add(body);
            if (root.Index is { } index)
            {
                var degree = Build(index, Math.Max(size * 0.55, 8), depth + 1);
                degree.VerticalAlignment = VerticalAlignment.Top;
                degree.HorizontalAlignment = HorizontalAlignment.Left;
                degree.Margin = new Thickness(0, size * 0.05, 0, 0);
                grid.Children.Add(degree);
            }

            return grid;
        }

        /// <summary>Brackets: as characters around a line of words, drawn as tall as a fraction or a root they hold.</summary>
        private FrameworkElement Fence(MathFence fence, double size, int depth)
        {
            var body = Build(fence.Body, size, depth);
            if (!StudyMath.IsTall(fence.Body))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                if (fence.Open.Length > 0)
                {
                    row.Children.Add(Text([(fence.Open, MathFace.Upright)], size));
                }

                row.Children.Add(body);
                if (fence.Close.Length > 0)
                {
                    row.Children.Add(Text([(fence.Close, MathFace.Upright)], size));
                }

                return row;
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.Margin = new Thickness(size * 0.08, 0, size * 0.08, 0);
            Grid.SetColumn(body, 1);
            grid.Children.Add(body);
            if (Delimiter(fence.Open, size) is { } open)
            {
                grid.Children.Add(open);
            }

            if (Delimiter(fence.Close, size) is { } close)
            {
                Grid.SetColumn(close, 2);
                grid.Children.Add(close);
            }

            return grid;
        }

        /// <summary>A bracket drawn to the height of its line (a little beyond, top and bottom).</summary>
        private FrameworkElement? Delimiter(string kind, double size)
        {
            double stroke = Math.Max(1, size * 0.06);
            var margin = new Thickness(0, -size * 0.12, 0, -size * 0.12);
            if (kind is "|" or "‖")
            {
                var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = size * 0.08, Margin = margin };
                for (int i = 0; i < (kind == "‖" ? 2 : 1); i++)
                {
                    bars.Children.Add(new Rectangle { Width = stroke, Fill = foreground, VerticalAlignment = VerticalAlignment.Stretch });
                }

                return bars;
            }

            Point[]? points = kind switch
            {
                "[" => [new(10, 0), new(0, 0), new(0, 100), new(10, 100)],
                "]" => [new(0, 0), new(10, 0), new(10, 100), new(0, 100)],
                "⟨" => [new(10, 0), new(0, 50), new(10, 100)],
                "⟩" => [new(0, 0), new(10, 50), new(0, 100)],
                "⌊" => [new(0, 0), new(0, 100), new(10, 100)],
                "⌋" => [new(10, 0), new(10, 100), new(0, 100)],
                "⌈" => [new(0, 100), new(0, 0), new(10, 0)],
                "⌉" => [new(0, 0), new(10, 0), new(10, 100)],
                _ => null,
            };

            Geometry? geometry = null;
            if (points is not null)
            {
                var figure = new PathFigure { StartPoint = points[0], IsClosed = false };
                var segment = new PolyLineSegment();
                foreach (var point in points[1..])
                {
                    segment.Points.Add(point);
                }

                figure.Segments.Add(segment);
                var path = new PathGeometry();
                path.Figures.Add(figure);
                geometry = path;
            }
            else if (kind is "(" or ")")
            {
                bool left = kind == "(";
                var figure = new PathFigure { StartPoint = new Point(left ? 10 : 0, 0), IsClosed = false };
                figure.Segments.Add(new BezierSegment
                {
                    Point1 = new Point(left ? -2 : 12, 30),
                    Point2 = new Point(left ? -2 : 12, 70),
                    Point3 = new Point(left ? 10 : 0, 100),
                });
                var path = new PathGeometry();
                path.Figures.Add(figure);
                geometry = path;
            }
            else if (kind.Length > 0)
            {
                // « { », « } » and the like: the character, stretched.
                return new Viewbox
                {
                    Stretch = Stretch.Fill,
                    Width = size * 0.4,
                    Margin = margin,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Child = new TextBlock { Text = kind, FontFamily = MathFont, FontSize = size, Foreground = foreground },
                };
            }

            return geometry is null ? null : new Path
            {
                Data = geometry,
                Stretch = Stretch.Fill,
                Stroke = foreground,
                StrokeThickness = stroke,
                Width = size * 0.3,
                Margin = margin,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
        }

        /// <summary>
        /// An accent: on a single letter, the character's own (x̂, ẋ); over more, or an arrow or a bar, drawn above it, with
        /// as much room left below.
        /// </summary>
        private FrameworkElement Accent(MathAccent accent, double size, int depth)
        {
            bool arrow = accent.Mark is "\u20D7" or "→";
            bool bar = accent.Mark == "\u0305";
            if (!arrow && !bar && accent.Body is MathText { Text.Length: 1 } letter)
            {
                return Row([letter with { Text = letter.Text + accent.Mark }], size, depth);
            }

            double stroke = Math.Max(1, size * 0.055);
            double height = arrow ? size * 0.24 : bar ? stroke : size * 0.3;
            double gap = size * 0.12;
            FrameworkElement mark;
            if (arrow)
            {
                var figure = new PathFigure { StartPoint = new Point(0, 5), IsClosed = false };
                var line = new PolyLineSegment();
                line.Points.Add(new Point(100, 5));
                figure.Segments.Add(line);
                var head = new PathFigure { StartPoint = new Point(93, 0), IsClosed = false };
                var tip = new PolyLineSegment();
                tip.Points.Add(new Point(100, 5));
                tip.Points.Add(new Point(93, 10));
                head.Segments.Add(tip);
                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                geometry.Figures.Add(head);
                mark = new Path
                {
                    Data = geometry,
                    Stretch = Stretch.Fill,
                    Stroke = foreground,
                    StrokeThickness = stroke,
                    Height = height,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
            }
            else if (bar)
            {
                mark = new Rectangle { Height = stroke, Fill = foreground, HorizontalAlignment = HorizontalAlignment.Stretch };
            }
            else
            {
                string sign = accent.Mark switch
                {
                    "\u0302" => "^",
                    "\u0303" => "~",
                    "\u0307" => "˙",
                    "\u0308" => "¨",
                    _ => accent.Mark,
                };
                mark = new TextBlock
                {
                    Text = sign,
                    FontFamily = MathFont,
                    FontSize = size * 0.8,
                    Foreground = foreground,
                    TextLineBounds = TextLineBounds.Tight,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
            }

            mark.Margin = new Thickness(size * 0.04, 0, size * 0.04, gap);
            var grid = new Grid { Margin = new Thickness(0, 0, 0, height + gap) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var body = Build(accent.Body, size, depth);
            Grid.SetRow(body, 1);
            grid.Children.Add(mark);
            grid.Children.Add(body);
            return grid;
        }
    }
}
