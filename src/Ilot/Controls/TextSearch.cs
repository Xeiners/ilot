using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Ilot.Controls;

/// <summary>
/// Searching the way people type: case and accents do not matter ("ete" finds "Été"), and every word of the query
/// must appear somewhere, in any order. Shared by every tab that searches.
/// </summary>
public static class TextSearch
{
    private const CompareOptions Loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;

    private static readonly CompareInfo Compare = CultureInfo.GetCultureInfo("fr-FR").CompareInfo;

    /// <summary>The words of a query; none means "show everything".</summary>
    public static string[] Words(string query) => query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether every word appears in <paramref name="text"/>.</summary>
    public static bool Matches(string text, IReadOnlyList<string> words)
    {
        foreach (string word in words)
        {
            if (Compare.IndexOf(text, word, Loose) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Where the words appear in <paramref name="text"/>, sorted and merged, to highlight.</summary>
    public static IReadOnlyList<(int Start, int Length)> Find(string text, IReadOnlyList<string> words)
    {
        var ranges = new List<(int Start, int Length)>();
        foreach (string word in words)
        {
            for (int start = 0; start < text.Length;)
            {
                int index = Compare.IndexOf(text.AsSpan(start), word, Loose, out int length);
                if (index < 0 || length == 0)
                {
                    break;
                }

                ranges.Add((start + index, length));
                start += index + length;
            }
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int Length)>(ranges.Count);
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.Start + last.Length, range.Start + range.Length) - last.Start);
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    /// <summary>Lights up where the words appear in the text blocks (nothing when there are none).</summary>
    public static void Highlight(IReadOnlyList<string> words, params TextBlock[] blocks)
    {
        foreach (var block in blocks)
        {
            block.TextHighlighters.Clear();
            if (words.Count == 0 || string.IsNullOrEmpty(block.Text))
            {
                continue;
            }

            var ranges = Find(block.Text, words);
            if (ranges.Count == 0)
            {
                continue;
            }

            var highlighter = new TextHighlighter
            {
                Background = (Brush)Application.Current.Resources["NotchHighlightBrush"],
                Foreground = (Brush)Application.Current.Resources["NotchHighlightTextBrush"],
            };
            foreach (var (start, length) in ranges)
            {
                highlighter.Ranges.Add(new TextRange { StartIndex = start, Length = length });
            }

            block.TextHighlighters.Add(highlighter);
        }
    }
}
