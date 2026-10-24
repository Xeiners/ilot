using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Ilot.Localization;
using Windows.UI;

namespace Ilot.Modules.Study;

/// <summary>
/// A summary sheet (the Markdown Notch writes) drawn to be studied, plain and easy to read: each part a card of neutral
/// glass, its number, its title and its pages; its points as a list, a definition's term in bold white, the pages each
/// comes from as little chips; « À retenir » and « À ne pas confondre » as boxes of their own (the latter with an amber
/// line). No colour but that: the text first. Read once, as it is: nothing moves.
/// </summary>
internal static partial class StudySheet
{
    private static readonly Color Confuse = Color.FromArgb(0xFF, 0xFF, 0xB8, 0x6B);
    private static readonly Color Plain = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color Body = Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF);
    private static readonly FontFamily Text = new("Segoe UI Variable Text");
    private static readonly FontFamily Display = new("Segoe UI Variable Display");

    /// <summary>A part of the sheet: its title, and the card it is drawn in (to scroll to it).</summary>
    public sealed record Part(string Title, FrameworkElement Card);

    /// <summary>The sheet of <paramref name="markdown"/>; its parts, in their order.</summary>
    public static (FrameworkElement Sheet, IReadOnlyList<Part> Parts) Build(string markdown)
    {
        var sheet = new StackPanel { Spacing = 14, MaxWidth = 800 };
        var parts = new List<Part>();
        Section? section = null;
        Panel? box = null;

        foreach (string raw in markdown.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (PartLine().Match(line) is { Success: true } part)
            {
                section = new Section(parts.Count + 1, part.Groups["title"].Value);
                sheet.Children.Add(section.Card);
                parts.Add(new Part(section.Title, section.Card));
                box = null;
                continue;
            }

            if (line.StartsWith('#'))
            {
                // « ### À retenir », « ### À ne pas confondre », or a heading of the part's own; « # » (the course) is the header's.
                string heading = line.TrimStart('#').Trim();
                if (line.StartsWith("##", StringComparison.Ordinal))
                {
                    section ??= Start(Loc.T("Fiche"));
                    box = Box(section, heading);
                }

                continue;
            }

            section ??= Start(Loc.T("Fiche"));
            var target = box ?? section.Body;
            if (Item().Match(line) is { Success: true } item)
            {
                int level = item.Groups["indent"].Length / 2;
                target.Children.Add(Point(item.Groups["text"].Value, level));
            }
            else
            {
                target.Children.Add(Paragraph(line.Trim()));
            }
        }

        return (sheet, parts);

        Section Start(string title)
        {
            var started = new Section(parts.Count + 1, title);
            sheet.Children.Add(started.Card);
            parts.Add(new Part(started.Title, started.Card));
            return started;
        }
    }

    /// <summary>
    /// « À retenir » (the sheet's colour, a star), « À ne pas confondre » (amber, a warning), or another heading of the
    /// part (a title over what follows).
    /// </summary>
    private static Panel Box(Section section, string heading)
    {
        string plain = heading.ToLowerInvariant();
        bool keep = plain.Contains("retenir", StringComparison.Ordinal) || plain.Contains("remember", StringComparison.Ordinal);
        bool confuse = plain.Contains("confondre", StringComparison.Ordinal) || plain.Contains("confuse", StringComparison.Ordinal);
        if (!keep && !confuse)
        {
            section.Body.Children.Add(new TextBlock
            {
                Text = heading,
                FontFamily = Display,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            return section.Body;
        }

        var color = keep ? Plain : Confuse;
        byte tint = keep ? (byte)0x0F : (byte)0x10;
        var items = new StackPanel { Spacing = 7 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 2) };
        title.Children.Add(new FontIcon { Glyph = keep ? "\uE735" : "\uE7BA", FontSize = 13, Foreground = Brush(Lighter(color)) });
        title.Children.Add(new TextBlock
        {
            Text = heading,
            FontFamily = Text,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(Lighter(color)),
        });
        items.Children.Add(title);

        var bar = new Rectangle { Width = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Brush(keep ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Alpha(color, 0xCC)) };
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(items, 1);
        content.Children.Add(bar);
        content.Children.Add(items);
        section.Body.Children.Add(new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(12, 12, 14, 12),
            CornerRadius = new CornerRadius(14),
            Background = Brush(keep ? Color.FromArgb(tint, 0xFF, 0xFF, 0xFF) : Alpha(color, tint)),
            BorderBrush = Brush(keep ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Alpha(color, 0x2E)),
            BorderThickness = new Thickness(1),
            Child = content,
        });
        return items;
    }

    /// <summary>
    /// A point: its dot (hollow, nested), its text (a definition's term in the sheet's colour, **bold** brighter), and the
    /// pages it comes from as chips at its end.
    /// </summary>
    private static FrameworkElement Point(string text, int level)
    {
        var (body, sources) = Sources(text);
        var row = new Grid { ColumnSpacing = 10, Margin = new Thickness(level * 18, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = level == 0 ? 6 : 5,
            Height = level == 0 ? 6 : 5,
            Margin = new Thickness(0, 9, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Fill = level == 0 ? Brush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)) : null,
            Stroke = level == 0 ? null : Brush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.2,
        };
        var words = Words(body, definition: true);
        Grid.SetColumn(words, 1);
        row.Children.Add(dot);
        row.Children.Add(words);
        if (Chips(sources) is { } chips)
        {
            Grid.SetColumn(chips, 2);
            row.Children.Add(chips);
        }

        return row;
    }

    private static FrameworkElement Paragraph(string text)
    {
        var (body, sources) = Sources(text);
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Words(body, definition: false));
        if (Chips(sources) is { } chips)
        {
            Grid.SetColumn(chips, 1);
            row.Children.Add(chips);
        }

        return row;
    }

    /// <summary>
    /// The words of a point: « **Terme** : définition » with the term in the sheet's colour; within, **bold** brighter,
    /// *italics*, `code`.
    /// </summary>
    private static TextBlock Words(string text, bool definition)
    {
        var block = new TextBlock
        {
            FontFamily = Text,
            FontSize = 14,
            LineHeight = 23,
            Foreground = Brush(Body),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        if (definition && Definition().Match(text) is { Success: true } term)
        {
            var name = new Bold { Foreground = Brush(Plain) };
            name.Inlines.Add(new Run { Text = term.Groups["term"].Value });
            block.Inlines.Add(name);
            block.Inlines.Add(new Run { Text = " : ", Foreground = Brush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)) });
            text = term.Groups["rest"].Value;
        }

        int at = 0;
        foreach (Match mark in Inline().Matches(text))
        {
            if (mark.Index > at)
            {
                block.Inlines.Add(new Run { Text = text[at..mark.Index] });
            }

            if (mark.Groups["bold"].Success)
            {
                var bold = new Bold { Foreground = Brush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)) };
                bold.Inlines.Add(new Run { Text = mark.Groups["bold"].Value });
                block.Inlines.Add(bold);
            }
            else if (mark.Groups["code"].Success)
            {
                block.Inlines.Add(new Run { Text = mark.Groups["code"].Value, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 });
            }
            else
            {
                var italic = new Italic();
                italic.Inlines.Add(new Run { Text = mark.Groups["italic"].Value });
                block.Inlines.Add(italic);
            }

            at = mark.Index + mark.Length;
        }

        if (at < text.Length)
        {
            block.Inlines.Add(new Run { Text = text[at..] });
        }

        return block;
    }

    /// <summary>The pages a point ends with (« (p. 4) », « (p. 6, 7) », « (diapo 3) »): taken off its text.</summary>
    private static (string Text, List<string> Sources) Sources(string text)
    {
        var sources = new List<string>();
        while (Source().Match(text) is { Success: true } source)
        {
            sources.Insert(0, source.Groups["source"].Value.Trim());
            text = text[..source.Index];
        }

        return (text.TrimEnd(' ', ';', ','), sources);
    }

    /// <summary>Little chips for where a point comes from, at its end.</summary>
    private static FrameworkElement? Chips(List<string> sources)
    {
        if (sources.Count == 0)
        {
            return null;
        }

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        foreach (string source in sources)
        {
            chips.Children.Add(Chip(source));
        }

        return chips;
    }

    /// <summary>A chip of glass: a page, a count.</summary>
    public static Border Chip(string text, Color? tint = null) => new()
    {
        Padding = new Thickness(8, 2, 8, 3),
        CornerRadius = new CornerRadius(9),
        Background = Brush(tint is { } color ? Alpha(color, 0x2E) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
        BorderBrush = Brush(tint is { } edge ? Alpha(edge, 0x4D) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = text,
            FontFamily = Text,
            FontSize = 11,
            Foreground = Brush(tint is { } light ? Lighter(light) : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
        },
    };

    /// <summary>A part of the sheet: a card of glass, lit from above in the sheet's colour, its number, title and pages.</summary>
    private sealed class Section
    {
        public Section(int number, string heading)
        {
            var title = Clean(heading, out string pages);
            Title = title;
            Body = new StackPanel { Spacing = 9 };

            var badge = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = Brush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                BorderBrush = Brush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = number.ToString(Loc.Culture),
                    FontFamily = Display,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var name = new TextBlock
            {
                Text = title,
                FontFamily = Display,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var header = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(name, 1);
            header.Children.Add(badge);
            header.Children.Add(name);
            if (pages.Length > 0)
            {
                var chip = Chip(Loc.F("p. {0}", pages));
                chip.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(chip, 2);
                header.Children.Add(chip);
            }

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(header);
            content.Children.Add(new Rectangle { Height = 1, Fill = Brush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 0, 0, 4) });
            content.Children.Add(Body);

            var light = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 0), EndPoint = new Windows.Foundation.Point(0.5, 1) };
            light.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) });
            light.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF) });
            var edge = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 0), EndPoint = new Windows.Foundation.Point(0.5, 1) };
            edge.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF) });
            edge.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF) });
            Card = new Border
            {
                Padding = new Thickness(22, 18, 22, 20),
                CornerRadius = new CornerRadius(20),
                Background = light,
                BorderBrush = edge,
                BorderThickness = new Thickness(1),
                Child = content,
            };
        }

        public string Title { get; }

        public StackPanel Body { get; }

        public Border Card { get; }

        /// <summary>A part's title without its numbering (the badge has it) nor its pages (a chip has them).</summary>
        private static string Clean(string heading, out string pages)
        {
            pages = "";
            var match = Heading().Match(heading.Trim());
            if (!match.Success)
            {
                return heading.Trim();
            }

            pages = match.Groups["pages"].Value.Trim();
            return match.Groups["title"].Value.Trim();
        }
    }

    private static SolidColorBrush Brush(Color color) => new(color);

    private static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>The colour, lighter (a third of the way to white): to read on the dark.</summary>
    private static Color Lighter(Color color) => Color.FromArgb(
        255, (byte)(color.R + (255 - color.R) * 0.4), (byte)(color.G + (255 - color.G) * 0.4), (byte)(color.B + (255 - color.B) * 0.4));

    // « ## Titre » (a part), not « ### ».
    [GeneratedRegex(@"^##(?!#)\s*(?<title>.+)$")]
    private static partial Regex PartLine();

    // A part's title: its numbering (« I. », « 2) ») and its pages (« (1-12) », « (p. 3-12) ») apart.
    [GeneratedRegex(@"^(?:(?:[IVXLC]+|\d+)[.)]\s+)?(?<title>.+?)(?:\s*\((?:p\.|pp\.|pages?|diapos?\.?)?\s*(?<pages>[\d\s,–\-]+)\))?$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(?<indent>\s*)(?:[-*+•]|\d+[.)])\s+(?<text>.*)$")]
    private static partial Regex Item();

    // « **Terme** : définition » (or « - »): the term, then the rest.
    [GeneratedRegex(@"^\*\*(?<term>[^*]+)\*\*\s*[:–\-]\s*(?<rest>.*)$")]
    private static partial Regex Definition();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>[^*]+)\*\*|(?<![\w*])\*(?<italic>[^*\s][^*]*)\*(?![\w*])")]
    private static partial Regex Inline();

    // Where a point comes from, at its end: « (p. 4) », « (p. 6, 7) », « (pp. 3-5) », « (diapo 3) », « (§ Innervation) ».
    [GeneratedRegex(@"\s*\((?<source>(?:p\.|pp\.|pages?|diapos?\.?|diapositives?|§)\s*[^()]*)\)\s*[.;]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Source();
}
