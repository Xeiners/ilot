using System.Globalization;
using System.Text;

namespace Ilot.Modules.Search;

/// <summary>
/// How well a name answers what was typed, the way a launcher should: "code" finds "Visual Studio Code", "vsc" too (its
/// initials), "parametres" finds "Paramètres" (no case, no accents), and a name that starts with the search, or a word of
/// it, comes before one that merely contains it.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>Lower case, without accents, one character for one: positions stay those of the original.</summary>
    public static string Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            folded.Append(FoldChar(c));
        }

        return folded.ToString();
    }

    /// <summary>The words typed, folded (see <see cref="Fold"/>).</summary>
    public static string[] Tokens(string query) =>
        Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Scores <paramref name="name"/> against the <paramref name="tokens"/> (each must match): about 1 for a name
    /// starting with the search, less for a word inside it, its initials, or letters in order; <c>null</c> for no match.
    /// </summary>
    public static double? Score(string name, string[] tokens, out List<(int Start, int Length)> matches)
    {
        matches = [];
        if (tokens.Length == 0 || name.Length == 0)
        {
            return null;
        }

        string folded = Fold(name);
        double total = 0;
        int covered = 0;
        foreach (string token in tokens)
        {
            if (TokenScore(name, folded, token, matches) is not { } score)
            {
                matches.Clear();
                return null;
            }

            total += score;
            covered += token.Length;
        }

        double result = total / tokens.Length;

        // The whole name typed, or nearly: a short name that is all match beats a long one that contains it.
        if (string.Equals(folded.Trim(), string.Join(' ', tokens), StringComparison.Ordinal))
        {
            result += 0.3;
        }

        result += 0.15 * Math.Min(1, (double)covered / folded.Length);
        matches = Merge(matches);
        return result;
    }

    private static double? TokenScore(string name, string folded, string token, List<(int, int)> matches)
    {
        int index = folded.IndexOf(token, StringComparison.Ordinal);
        if (index == 0)
        {
            matches.Add((0, token.Length));
            return 1;
        }

        // The start of a word inside the name ("Studio" in "Visual Studio") beats the middle of one.
        int word = WordStart(name, folded, token);
        if (word > 0)
        {
            matches.Add((word, token.Length));
            return 0.86;
        }

        if (index > 0)
        {
            matches.Add((index, token.Length));
            return 0.62 - Math.Min(index, 30) * 0.004;
        }

        if (token.Length >= 2 && Initials(name, folded, token, matches))
        {
            return 0.8;
        }

        if (token.Length >= 2 && InOrder(folded, token, matches, out int gaps))
        {
            return Math.Max(0.12, 0.34 - gaps * 0.012);
        }

        return null;
    }

    /// <summary>Where a word of the name starts with <paramref name="token"/>; -1 if none.</summary>
    private static int WordStart(string name, string folded, string token)
    {
        for (int at = folded.IndexOf(token, 1, StringComparison.Ordinal); at > 0; at = folded.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            if (IsWordStart(name, at))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>Each letter typed starts a word of the name, in order ("vsc": Visual Studio Code).</summary>
    private static bool Initials(string name, string folded, string token, List<(int, int)> matches)
    {
        var found = new List<(int, int)>();
        int at = 0;
        foreach (char c in token)
        {
            while (at < folded.Length && !(folded[at] == c && IsWordStart(name, at)))
            {
                at++;
            }

            if (at == folded.Length)
            {
                return false;
            }

            found.Add((at, 1));
            at++;
        }

        matches.AddRange(found);
        return true;
    }

    /// <summary>The letters typed appear in order, gaps between them counted.</summary>
    private static bool InOrder(string folded, string token, List<(int, int)> matches, out int gaps)
    {
        gaps = 0;
        var found = new List<(int, int)>();
        int at = 0;
        int last = -1;
        foreach (char c in token)
        {
            at = folded.IndexOf(c, at);
            if (at < 0)
            {
                return false;
            }

            if (last >= 0 && at > last + 1)
            {
                gaps += at - last - 1;
            }

            found.Add((at, 1));
            last = at;
            at++;
        }

        matches.AddRange(found);
        return true;
    }

    private static bool IsWordStart(string name, int at) =>
        at == 0
        || name[at - 1] is ' ' or '-' or '_' or '.' or '(' or '[' or '/' or '\\' or '+' or '&'
        || (char.IsUpper(name[at]) && char.IsLower(name[at - 1]))
        || (char.IsDigit(name[at]) && !char.IsDigit(name[at - 1]));

    /// <summary>Overlapping or touching ranges, joined, in order.</summary>
    private static List<(int Start, int Length)> Merge(List<(int Start, int Length)> ranges)
    {
        if (ranges.Count < 2)
        {
            return ranges;
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int Length)> { ranges[0] };
        foreach (var (start, length) in ranges.Skip(1))
        {
            var (lastStart, lastLength) = merged[^1];
            if (start <= lastStart + lastLength)
            {
                merged[^1] = (lastStart, Math.Max(lastLength, start + length - lastStart));
            }
            else
            {
                merged.Add((start, length));
            }
        }

        return merged;
    }

    private static char FoldChar(char c)
    {
        if (c < 128)
        {
            return char.ToLowerInvariant(c);
        }

        string decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        foreach (char part in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark)
            {
                return char.ToLowerInvariant(part);
            }
        }

        return char.ToLowerInvariant(c);
    }
}
