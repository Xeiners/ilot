using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MyNotch.Localization;
using MyNotch.Mascot;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using static MyNotch.Modules.Study.StudyPaint;

namespace MyNotch.Modules.Study;

/// <summary>
/// A mind map drawn (placed by <see cref="MindMapLayout"/>): the notion at its centre in the revisions' colour, its
/// definition under it; its formulas first (top right), in a mathematical font; each branch a card of its own colour (its
/// aspect, then its details), a curve from the centre to it. Laid out at its own size (its <c>Width</c> and <c>Height</c>
/// set): the canvas that shows it zooms and moves it (see MindMapCanvas).
/// </summary>
internal static class MindMapDrawing
{
    // The branches' colours, in turn: the mascot's palette, so a map looks like the rest of the notch.
    private static readonly string[] BranchColors = ["sky", "mint", "lemon", "peach", "pink", "lavender", "coral"];
    private static readonly FontFamily DisplayFont = new("Segoe UI Variable Display");
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text");
    private static readonly FontFamily MathFont = new("Cambria Math");
    private static readonly Color FormulaColor = Color.FromArgb(0xFF, 0xE6, 0xE6, 0xEE);
    private static readonly Color Ink = Color.FromArgb(0xFF, 0x11, 0x11, 0x14);
    private const double Edge = 6; // Room around the map, for the cards' edges.
    private const int MostOverviewDetails = 6;

    /// <param name="overview">The whole course (its parts around it, their notions under each): its centre a little larger.</param>
    public static FrameworkElement Draw(MindMap map, Color accent, bool overview)
    {
        var centre = Centre(map, accent, overview);
        var branches = new List<Border>();
        var colors = new List<Color>();
        if (map.Formulas.Count > 0)
        {
            branches.Add(Formulas(map.Formulas));
            colors.Add(FormulaColor);
        }

        for (int i = 0; i < map.Branches.Count; i++)
        {
            branches.Add(Branch(map.Branches[i], BranchColor(i), overview));
            colors.Add(BranchColor(i));
        }

        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        centre.Measure(infinite);
        foreach (var branch in branches)
        {
            branch.Measure(infinite);
        }

        var placed = MindMapLayout.Arrange(
            (centre.DesiredSize.Width, centre.DesiredSize.Height),
            branches.Select(branch => (branch.DesiredSize.Width, branch.DesiredSize.Height)).ToList());

        var canvas = new Canvas { Width = placed.Width + 2 * Edge, Height = placed.Height + 2 * Edge };
        for (int i = 0; i < branches.Count; i++)
        {
            canvas.Children.Add(Link(placed.Centre, placed.Branches[i], placed.OnRight[i], colors[i]));
        }

        Put(centre, placed.Centre);
        for (int i = 0; i < branches.Count; i++)
        {
            Put(branches[i], placed.Branches[i]);
        }

        return canvas;

        void Put(FrameworkElement element, MindBox box)
        {
            Canvas.SetLeft(element, box.X + Edge);
            Canvas.SetTop(element, box.Y + Edge);
            canvas.Children.Add(element);
        }
    }

    /// <summary>A branch's colour (a part's, in the course's overview, and in the list of the notions).</summary>
    public static Color BranchColor(int index) => MascotPalette.Find(BranchColors[index % BranchColors.Length]).Tone;

    /// <summary>The notion: a pill of the revisions' colour, dark text on it; its definition under it, if the course gives one.</summary>
    private static Border Centre(MindMap map, Color accent, bool overview)
    {
        bool defined = map.Definition.Length > 0;
        var lines = new StackPanel { Spacing = 5 };
        lines.Children.Add(new TextBlock
        {
            Text = map.Notion,
            FontFamily = DisplayFont,
            FontSize = overview ? 18 : 16.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Ink),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (defined)
        {
            lines.Children.Add(new TextBlock
            {
                Text = map.Definition,
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = new SolidColorBrush(Alpha(Ink, 0xD0)),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        return new Border
        {
            MaxWidth = overview ? 220 : defined ? 250 : 190,
            Padding = defined ? new Thickness(16, 12, 16, 13) : new Thickness(18, 12, 18, 13),
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(accent),
            Child = lines,
        };
    }

    /// <summary>The formulas: a light card, each one in a mathematical font on its own little plate.</summary>
    private static Border Formulas(IReadOnlyList<string> formulas)
    {
        var lines = new StackPanel { Spacing = 6 };
        lines.Children.Add(new TextBlock
        {
            Text = Loc.T("Formules"),
            FontFamily = TextFont,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(FormulaColor),
        });
        foreach (string formula in formulas)
        {
            lines.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(9, 3, 9, 5),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                Child = new TextBlock
                {
                    Text = formula,
                    FontFamily = MathFont,
                    FontSize = 15,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        return new Border
        {
            MaxWidth = 260,
            Padding = new Thickness(13, 9, 13, 11),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Mix(CardBase, FormulaColor, 0.07)),
            BorderBrush = new SolidColorBrush(Alpha(FormulaColor, 0x40)),
            BorderThickness = new Thickness(1),
            Child = lines,
        };
    }

    /// <summary>A branch: a card tinted with its colour, its aspect in that colour, its details under it.</summary>
    private static Border Branch(MindBranch branch, Color color, bool overview)
    {
        var lines = new StackPanel { Spacing = 3 };
        lines.Children.Add(new TextBlock
        {
            Text = branch.Label,
            FontFamily = TextFont,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Mix(color, Microsoft.UI.Colors.White, 0.3)),
            TextWrapping = TextWrapping.Wrap,
        });

        // The whole course: a part's notions, the first ones and how many more.
        IReadOnlyList<string> details = overview && branch.Details.Count > MostOverviewDetails
            ? [.. branch.Details.Take(MostOverviewDetails - 1), Loc.F("+ {0} autres", branch.Details.Count - (MostOverviewDetails - 1))]
            : branch.Details;
        foreach (string detail in details)
        {
            // Its dot in the branch's colour, the text wrapping beside it (not under it).
            var line = new Grid { ColumnSpacing = 7 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.Children.Add(new TextBlock { Text = "•", FontFamily = TextFont, FontSize = 12, Foreground = new SolidColorBrush(color) });
            var text = new TextBlock
            {
                Text = detail,
                FontFamily = TextFont,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            lines.Children.Add(line);
        }

        return new Border
        {
            MaxWidth = 200, // Narrow enough that a map fits the sheet at its size, or nearly.
            Padding = new Thickness(13, 9, 14, 11),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Mix(CardBase, color, 0.12)),
            BorderBrush = new SolidColorBrush(Alpha(color, 0x5A)),
            BorderThickness = new Thickness(1),
            Child = lines,
        };
    }

    /// <summary>A curve from the centre's side to the branch's near side, leaving and arriving level.</summary>
    private static Path Link(MindBox centre, MindBox branch, bool onRight, Color color)
    {
        var from = new Point((onRight ? centre.Right : centre.X) + Edge, centre.Middle + Edge);
        var to = new Point((onRight ? branch.X : branch.Right) + Edge, branch.Middle + Edge);
        double bend = (to.X - from.X) * 0.55;
        var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment { Point1 = new Point(from.X + bend, from.Y), Point2 = new Point(to.X - bend, to.Y), Point3 = to });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return new Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(Alpha(color, 0xB0)),
            StrokeThickness = 2.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
    }
}
