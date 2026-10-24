using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ilot.Modules.Assistant;

/// <summary>
/// The little Markdown an answer uses, drawn in a <see cref="RichTextBlock"/>: headings, lists (bulleted or numbered),
/// code blocks, and within a line **bold**, *italics* and `code`. Anything else shows as written. Drawn again from the
/// whole text at each piece of a streaming answer: an unfinished mark simply shows as it is until it closes.
/// </summary>
public static partial class MarkdownLite
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly SolidColorBrush Code = new(Color.FromArgb(0xFF, 0xB4, 0xE3, 0xFF));
    private static readonly SolidColorBrush Block = new(Color.FromArgb(0xFF, 0xC8, 0xE8, 0xC8));

    public static void Fill(RichTextBlock target, string text)
    {
        target.Blocks.Clear();
        Paragraph? paragraph = null;
        bool inCode = false;
        foreach (string raw in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                paragraph = null;
                continue;
            }

            if (inCode)
            {
                // Code as written, line after line, in its own paragraph.
                if (paragraph is null)
                {
                    paragraph = new Paragraph { FontFamily = Mono, FontSize = 12.5, Foreground = Block, Margin = new Thickness(10, 4, 0, 4) };
                    target.Blocks.Add(paragraph);
                }
                else
                {
                    paragraph.Inlines.Add(new LineBreak());
                }

                paragraph.Inlines.Add(new Run { Text = raw });
                continue;
            }

            if (line.Length == 0)
            {
                paragraph = null; // A blank line ends the paragraph.
                continue;
            }

            if (Heading().Match(line) is { Success: true } heading)
            {
                var title = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = heading.Groups[1].Length == 1 ? 16 : 14.5, Margin = new Thickness(0, 6, 0, 2) };
                AddInlines(title.Inlines, heading.Groups[2].Value);
                target.Blocks.Add(title);
                paragraph = null;
                continue;
            }

            if (Item().Match(line) is { Success: true } item)
            {
                // A list item: its bullet (or number) hanging in the margin, its lines aligned after it.
                string mark = item.Groups[2].Value is "-" or "*" or "+" ? "•" : item.Groups[2].Value;
                double indent = 14 + item.Groups[1].Length * 6;
                var entry = new Paragraph { Margin = new Thickness(indent, 2, 0, 2), TextIndent = -12 };
                entry.Inlines.Add(new Run { Text = mark + "  " });
                AddInlines(entry.Inlines, item.Groups[3].Value);
                target.Blocks.Add(entry);
                paragraph = null;
                continue;
            }

            if (paragraph is null)
            {
                paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                target.Blocks.Add(paragraph);
            }
            else
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            AddInlines(paragraph.Inlines, line);
        }
    }

    /// <summary>A line's text, its **bold**, *italics* and `code` drawn so.</summary>
    private static void AddInlines(InlineCollection inlines, string text)
    {
        int at = 0;
        foreach (Match mark in Inline().Matches(text))
        {
            if (mark.Index > at)
            {
                inlines.Add(new Run { Text = text[at..mark.Index] });
            }

            if (mark.Groups["code"].Success)
            {
                inlines.Add(new Run { Text = mark.Groups["code"].Value, FontFamily = Mono, FontSize = 12.5, Foreground = Code });
            }
            else if (mark.Groups["bold"].Success)
            {
                var bold = new Bold();
                bold.Inlines.Add(new Run { Text = mark.Groups["bold"].Value });
                inlines.Add(bold);
            }
            else
            {
                var italic = new Italic();
                italic.Inlines.Add(new Run { Text = mark.Groups["italic"].Value });
                inlines.Add(italic);
            }

            at = mark.Index + mark.Length;
        }

        if (at < text.Length)
        {
            inlines.Add(new Run { Text = text[at..] });
        }
    }

    [GeneratedRegex(@"^(#{1,3})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex Item();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>[^*]+)\*\*|(?<![\w*])[*_](?<italic>[^*_\s][^*_]*)[*_](?![\w*])")]
    private static partial Regex Inline();
}
