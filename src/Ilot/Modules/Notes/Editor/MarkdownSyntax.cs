namespace Ilot.Modules.Notes.Editor;

/// <summary>What a line of Markdown is, from its first characters.</summary>
public enum LineKind
{
    Body,
    Heading1,
    Heading2,
    Heading3,

    /// <summary>"- item", "* item", "+ item".</summary>
    Bullet,

    /// <summary>"1. item", "1) item".</summary>
    Numbered,

    /// <summary>"- [ ] to do".</summary>
    Task,

    /// <summary>"- [x] done".</summary>
    DoneTask,

    /// <summary>"> quoted".</summary>
    Quote,

    /// <summary>The <c>```</c> line opening or closing a code block.</summary>
    Fence,

    /// <summary>A line inside a code block: taken as it is.</summary>
    Code,

    /// <summary>"---".</summary>
    Rule,
}

/// <summary>
/// A line: its kind, where its text starts (after the marker: indentation, "- [ ] ", "## "…) and how far it is
/// indented.
/// </summary>
public readonly record struct MarkdownLine(LineKind Kind, int MarkerLength, int Indent)
{
    public bool IsListItem => Kind is LineKind.Bullet or LineKind.Numbered or LineKind.Task or LineKind.DoneTask;

    public bool IsHeading => Kind is LineKind.Heading1 or LineKind.Heading2 or LineKind.Heading3;

    /// <summary>Text where <c>**bold**</c>, <c>*italic*</c>, <c>~~struck~~</c> and <c>`code`</c> count.</summary>
    public bool HasInlineStyles => Kind is not (LineKind.Code or LineKind.Fence or LineKind.Rule);
}

public enum InlineStyle
{
    /// <summary>The marks themselves (<c>**</c>, <c>`</c>…): faint.</summary>
    Mark,
    Bold,
    Italic,
    Strike,
    Code,
}

public readonly record struct InlineSpan(int Start, int Length, InlineStyle Style);

/// <summary>
/// Just enough Markdown for notes, read line by line: titles, lists, tasks, quotes, code blocks, and bold / italic
/// / struck / code inside a line. No nesting: the first mark that closes wins.
/// </summary>
public static class MarkdownSyntax
{
    /// <summary>Every line, code blocks taken into account (a <c>```</c> line opens one, the next closes it).</summary>
    public static MarkdownLine[] Parse(IReadOnlyList<string> lines)
    {
        var parsed = new MarkdownLine[lines.Count];
        bool inCode = false;
        for (int i = 0; i < lines.Count; i++)
        {
            parsed[i] = Parse(lines[i], inCode);
            if (parsed[i].Kind == LineKind.Fence)
            {
                inCode = !inCode;
            }
        }

        return parsed;
    }

    public static MarkdownLine Parse(string line, bool inCode)
    {
        int indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        var rest = line.AsSpan(indent);
        if (rest.StartsWith("```"))
        {
            return new(LineKind.Fence, line.Length, indent);
        }

        if (inCode)
        {
            return new(LineKind.Code, 0, indent);
        }

        // "# Title": up to six #, then a space (or nothing yet).
        int hashes = 0;
        while (hashes < rest.Length && hashes < 6 && rest[hashes] == '#')
        {
            hashes++;
        }

        if (hashes > 0 && (hashes == rest.Length || rest[hashes] == ' '))
        {
            var kind = hashes switch { 1 => LineKind.Heading1, 2 => LineKind.Heading2, _ => LineKind.Heading3 };
            return new(kind, indent + Math.Min(hashes + 1, rest.Length), indent);
        }

        if (IsRule(rest))
        {
            return new(LineKind.Rule, line.Length, indent);
        }

        if (rest.Length >= 2 && rest[0] is '-' or '*' or '+' && rest[1] == ' ')
        {
            // "- [ ] " and "- [x] ".
            if (rest.Length >= 5 && rest[2] == '[' && rest[3] is ' ' or 'x' or 'X' && rest[4] == ']' && (rest.Length == 5 || rest[5] == ' '))
            {
                var kind = rest[3] == ' ' ? LineKind.Task : LineKind.DoneTask;
                return new(kind, indent + Math.Min(6, rest.Length), indent);
            }

            return new(LineKind.Bullet, indent + 2, indent);
        }

        int digits = 0;
        while (digits < rest.Length && digits < 9 && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < rest.Length && rest[digits] is '.' or ')' && rest[digits + 1] == ' ')
        {
            return new(LineKind.Numbered, indent + digits + 2, indent);
        }

        if (rest.Length >= 1 && rest[0] == '>')
        {
            return new(LineKind.Quote, indent + (rest.Length >= 2 && rest[1] == ' ' ? 2 : 1), indent);
        }

        return new(LineKind.Body, 0, indent);
    }

    /// <summary>
    /// Bold, italic, struck and code spans in <paramref name="line"/> from <paramref name="start"/> on (after its
    /// marker), each with its marks.
    /// </summary>
    public static void Inline(string line, int start, List<InlineSpan> spans)
    {
        int i = start;
        while (i < line.Length)
        {
            char c = line[i];
            int end;
            if (c == '`' && (end = line.IndexOf('`', i + 1)) > i + 1)
            {
                Add(i, end + 1, 1, InlineStyle.Code);
                i = end + 1;
            }
            else if ((c is '*' or '_' or '~') && i + 1 < line.Length && line[i + 1] == c
                && (end = Closing(line, i + 2, new string(c, 2))) > 0)
            {
                Add(i, end + 2, 2, c == '~' ? InlineStyle.Strike : InlineStyle.Bold);
                i = end + 2;
            }
            else if (c is '*' or '_' && i + 1 < line.Length && line[i + 1] != ' ' && line[i + 1] != c
                && (c == '*' || i == 0 || !char.IsLetterOrDigit(line[i - 1]))
                && (end = Closing(line, i + 1, c.ToString())) > 0
                && (c == '*' || end + 1 >= line.Length || !char.IsLetterOrDigit(line[end + 1])))
            {
                Add(i, end + 1, 1, InlineStyle.Italic);
                i = end + 1;
            }
            else
            {
                i++;
            }
        }

        void Add(int from, int to, int mark, InlineStyle style)
        {
            spans.Add(new(from, mark, InlineStyle.Mark));
            spans.Add(new(from + mark, to - from - 2 * mark, style));
            spans.Add(new(to - mark, mark, InlineStyle.Mark));
        }
    }

    /// <summary>Where <paramref name="mark"/> closes a span opened just before <paramref name="from"/>; -1 if never.</summary>
    private static int Closing(string line, int from, string mark)
    {
        int end = line.IndexOf(mark, from, StringComparison.Ordinal);

        // Something between the marks, and no space just inside the closing one ("** not bold **").
        return end > from && line[end - 1] != ' ' ? end : -1;
    }

    private static bool IsRule(ReadOnlySpan<char> text)
    {
        var trimmed = text.TrimEnd(' ');
        if (trimmed.Length < 3 || trimmed[0] is not ('-' or '*' or '_'))
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (c != trimmed[0])
            {
                return false;
            }
        }

        return true;
    }
}
