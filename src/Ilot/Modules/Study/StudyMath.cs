using System.Text;
using System.Text.RegularExpressions;

namespace Ilot.Modules.Study;

/// <summary>How a piece of a formula is set: a variable (italic), upright (numbers, units, names), an operator (spaced),
/// a function's name (« sin », a thin space after), a big operator (∑, ∫).</summary>
internal enum MathFace
{
    Italic,
    Upright,
    Operator,
    Function,
    Large,
}

internal abstract record MathNode;

internal sealed record MathText(string Text, MathFace Face) : MathNode;

internal sealed record MathRow(IReadOnlyList<MathNode> Items) : MathNode;

internal sealed record MathScripts(MathNode Base, MathNode? Sub, MathNode? Sup) : MathNode;

internal sealed record MathFraction(MathNode Over, MathNode Under, bool Rule = true) : MathNode;

internal sealed record MathRoot(MathNode Body, MathNode? Index) : MathNode;

internal sealed record MathFence(string Open, MathNode Body, string Close) : MathNode;

internal sealed record MathAccent(MathNode Body, string Mark) : MathNode;

/// <summary>A piece of a text: words, or a formula (its LaTeX), set on a line of its own (<see cref="Display"/>) or within the words.</summary>
internal readonly record struct TextPiece(string Text, bool IsMath, bool Display);

/// <summary>
/// The formulas Notch writes (LaTeX, between « $ »): found in a text, read into a little tree (fractions, roots, indices and
/// exponents, brackets, accents, chemistry with \ce), and written out in one line of Unicode where a drawing cannot go (a
/// chip, a choice's name): « n(A)₀/a = n(B)_E/b ». Drawn by <see cref="StudyMathView"/>. What it does not know is kept, as
/// words: a formula always reads, never fails.
/// </summary>
internal static partial class StudyMath
{
    /// <summary>Whether <paramref name="text"/> holds a formula.</summary>
    public static bool Has(string text) =>
        (text.Contains('$', StringComparison.Ordinal) || text.Contains("\\(", StringComparison.Ordinal) || text.Contains("\\[", StringComparison.Ordinal))
        && Split(text).Any(piece => piece.IsMath);

    /// <summary>
    /// What a JSON escape made of a backslash Notch forgot to double: « \frac » read as a form feed and « rac », « \times »
    /// as a tab and « imes », « \neq » as a new line and « eq » (in a formula). Put back.
    /// </summary>
    public static string Repair(string text)
    {
        if (text.IndexOfAny(['\f', '\t', '\b', '\r', '\n']) < 0)
        {
            return text;
        }

        var repaired = new StringBuilder(text.Length + 8);
        bool inMath = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool letterNext = i + 1 < text.Length && text[i + 1] is >= 'a' and <= 'z';
            char before = i > 0 ? text[i - 1] : '\n';
            switch (c)
            {
                case '$' when before != '\\':
                    inMath = !inMath;
                    repaired.Append(c);
                    break;
                case '\f' or '\b' when letterNext:
                    repaired.Append('\\').Append(c == '\f' ? 'f' : 'b');
                    break;
                case '\t' when letterNext && before is not ('\n' or '\t'): // Not an indent.
                    repaired.Append("\\t");
                    break;
                case '\r' when letterNext:
                    repaired.Append("\\r");
                    break;
                case '\n' when inMath && letterNext:
                    repaired.Append("\\n");
                    break;
                case '\n':
                    inMath = false; // A formula never runs over two lines: a lone « $ » closes nothing.
                    repaired.Append(c);
                    break;
                default:
                    repaired.Append(c);
                    break;
            }
        }

        return repaired.ToString();
    }

    /// <summary>
    /// A formula set out on several lines (« $$ », its LaTeX, « $$ ») gathered on one, as each line of a sheet is read on its own.
    /// </summary>
    public static string JoinDisplays(string text) =>
        Multiline().Replace(text, match => $"$${match.Groups["body"].Value.ReplaceLineEndings(" ")}$$");

    /// <summary>
    /// The words and the formulas of <paramref name="text"/>, in order: « $…$ » and « \(…\) » within the words, « $$…$$ »
    /// and « \[…\] » on a line of their own, like a formula with a fraction, a root or a sum (too tall for a line of words).
    /// A « $ » that opens nothing (a price) stays a word.
    /// </summary>
    public static IReadOnlyList<TextPiece> Split(string text)
    {
        var pieces = new List<TextPiece>();
        var words = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '$')
            {
                words.Append('$');
                i += 2;
                continue;
            }

            (int start, int end, int skip, bool display) found = default;
            if (c == '$' && i + 1 < text.Length && text[i + 1] == '$')
            {
                int close = text.IndexOf("$$", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    found = (i + 2, close, 2, true);
                }
            }
            else if (c == '$')
            {
                int close = ClosingDollar(text, i + 1);
                if (close > i + 1 && !char.IsWhiteSpace(text[i + 1]) && !char.IsWhiteSpace(text[close - 1]))
                {
                    found = (i + 1, close, 1, false);
                }
            }
            else if (c == '\\' && i + 1 < text.Length && text[i + 1] is '(' or '[')
            {
                string end = text[i + 1] == '(' ? "\\)" : "\\]";
                int close = text.IndexOf(end, i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    found = (i + 2, close, 2, text[i + 1] == '[');
                }
            }

            if (found.end == 0)
            {
                words.Append(c);
                i++;
                continue;
            }

            if (words.Length > 0)
            {
                pieces.Add(new TextPiece(words.ToString(), false, false));
                words.Clear();
            }

            string latex = text[found.start..found.end].Trim();
            pieces.Add(new TextPiece(latex, true, found.display || Tall().IsMatch(latex)));
            i = found.end + found.skip;
        }

        if (words.Length > 0)
        {
            pieces.Add(new TextPiece(words.ToString(), false, false));
        }

        return pieces;
    }

    /// <summary>The « $ » closing a formula opened just before <paramref name="from"/>, on the same line; -1 if none.</summary>
    private static int ClosingDollar(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\n':
                    return -1;
                case '\\':
                    i++; // « \$ » and the like: escaped.
                    break;
                case '$':
                    return i + 1 < text.Length && char.IsDigit(text[i + 1]) ? -1 : i; // « 5$10 »: money, not math.
            }
        }

        return -1;
    }

    /// <summary><paramref name="text"/> in one line, its formulas in Unicode (« n(A)₀/a »): for a chip, a choice's name, a copy.</summary>
    public static string Plain(string text)
    {
        if (!text.Contains('$', StringComparison.Ordinal) && !text.Contains('\\', StringComparison.Ordinal))
        {
            return text;
        }

        var plain = new StringBuilder();
        foreach (var piece in Split(Repair(text)))
        {
            plain.Append(piece.IsMath ? Linear(Parse(piece.Text)) : piece.Text);
        }

        return plain.ToString();
    }

    /// <summary>A formula on its own (a mind map's), LaTeX or already in Unicode: in one line of Unicode.</summary>
    public static string PlainFormula(string formula)
    {
        string trimmed = formula.Trim().Trim('$').Trim();
        return LooksLikeLatex(trimmed) ? Linear(Parse(Repair(trimmed))) : trimmed;
    }

    /// <summary>A formula that needs reading as LaTeX: a command, or braced indices and exponents.</summary>
    public static bool LooksLikeLatex(string formula) =>
        Command().IsMatch(formula) || Script().IsMatch(formula);

    // ── Reading ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The tree of a formula's LaTeX. Never fails: what it does not know comes out as words.</summary>
    public static MathNode Parse(string latex)
    {
        var reader = new Reader(latex);
        try
        {
            return Row(reader, Stop.End);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return new MathText(latex, MathFace.Upright);
        }
    }

    private enum Stop
    {
        End,
        Brace,
        Bracket,
        Right,
    }

    private sealed class Reader(string text)
    {
        public string Text { get; } = text;

        public int At { get; set; }

        public bool AtEnd => At >= Text.Length;

        public char Current => Text[At];

        public bool Next(string word) => string.CompareOrdinal(Text, At, word, 0, word.Length) == 0
            && (At + word.Length >= Text.Length || !char.IsAsciiLetter(Text[At + word.Length]));

        public void SkipSpaces()
        {
            while (!AtEnd && char.IsWhiteSpace(Current))
            {
                At++;
            }
        }
    }

    private static MathNode Row(Reader reader, Stop stop)
    {
        var items = new List<MathNode>();
        while (!reader.AtEnd)
        {
            char c = reader.Current;
            if ((stop == Stop.Brace && c == '}') || (stop == Stop.Bracket && c == ']'))
            {
                reader.At++;
                break;
            }

            if (stop == Stop.Right && reader.Next("\\right"))
            {
                break;
            }

            if (c == '}' || char.IsWhiteSpace(c))
            {
                reader.At++; // A stray brace; spaces mean nothing in a formula.
                continue;
            }

            if (c is '^' or '_')
            {
                reader.At++;
                var script = Argument(reader) ?? new MathText("", MathFace.Upright);
                Attach(items, c == '_' ? script : null, c == '^' ? script : null);
                continue;
            }

            if (c == '\'')
            {
                reader.At++;
                items.Add(new MathText("′", MathFace.Upright));
                continue;
            }

            if (Atom(reader, single: false) is { } atom)
            {
                items.Add(atom);
            }
        }

        return items.Count == 1 ? items[0] : new MathRow(items);
    }

    /// <summary>An index or an exponent onto what comes before (an empty base if nothing does).</summary>
    private static void Attach(List<MathNode> items, MathNode? sub, MathNode? sup)
    {
        if (items.Count > 0 && items[^1] is MathScripts scripts && (sub is null || scripts.Sub is null) && (sup is null || scripts.Sup is null))
        {
            items[^1] = scripts with { Sub = scripts.Sub ?? sub, Sup = scripts.Sup ?? sup };
            return;
        }

        MathNode baseNode = new MathText("", MathFace.Upright);
        if (items.Count > 0)
        {
            baseNode = items[^1];
            items.RemoveAt(items.Count - 1);
        }

        items.Add(new MathScripts(baseNode, sub, sup));
    }

    /// <summary>A command's argument, an index's or an exponent's: a group in braces, a command, or one character.</summary>
    private static MathNode? Argument(Reader reader)
    {
        reader.SkipSpaces();
        if (reader.AtEnd)
        {
            return null;
        }

        if (reader.Current == '{')
        {
            reader.At++;
            return Row(reader, Stop.Brace);
        }

        return Atom(reader, single: true);
    }

    /// <summary>The text of a group in braces as it is (« \text{mol} »), or of one character.</summary>
    private static string Raw(Reader reader)
    {
        reader.SkipSpaces();
        if (reader.AtEnd)
        {
            return "";
        }

        if (reader.Current != '{')
        {
            return reader.Text[reader.At++].ToString();
        }

        int depth = 0;
        int start = reader.At + 1;
        for (; !reader.AtEnd; reader.At++)
        {
            if (reader.Current == '{')
            {
                depth++;
            }
            else if (reader.Current == '}' && --depth == 0)
            {
                reader.At++;
                return reader.Text[start..(reader.At - 1)];
            }
        }

        return reader.Text[start..];
    }

    private static MathNode? Atom(Reader reader, bool single)
    {
        char c = reader.Current;
        if (c == '{')
        {
            reader.At++;
            return Row(reader, Stop.Brace);
        }

        if (c == '\\')
        {
            return Command(reader);
        }

        reader.At++;
        if (char.IsAsciiDigit(c))
        {
            if (single)
            {
                return new MathText(c.ToString(), MathFace.Upright);
            }

            int start = reader.At - 1;
            while (!reader.AtEnd && (char.IsAsciiDigit(reader.Current)
                || (reader.Current is '.' or ',' && reader.At + 1 < reader.Text.Length && char.IsAsciiDigit(reader.Text[reader.At + 1]))))
            {
                reader.At++;
            }

            return new MathText(reader.Text[start..reader.At], MathFace.Upright);
        }

        return c switch
        {
            '-' => new MathText("−", MathFace.Operator),
            '+' or '=' or '<' or '>' => new MathText(c.ToString(), MathFace.Operator),
            '*' => new MathText("∗", MathFace.Operator),
            '~' => new MathText(" ", MathFace.Upright),
            '&' => new MathText("\u2003", MathFace.Upright),
            _ when char.IsAsciiLetter(c) || c is >= 'α' and <= 'ω' => new MathText(c.ToString(), MathFace.Italic),
            _ when Operators.Contains(c) => new MathText(c.ToString(), MathFace.Operator),
            _ => new MathText(c.ToString(), MathFace.Upright),
        };
    }

    private static MathNode? Command(Reader reader)
    {
        reader.At++; // The backslash.
        if (reader.AtEnd)
        {
            return null;
        }

        if (!char.IsAsciiLetter(reader.Current))
        {
            char symbol = reader.Text[reader.At++];
            return symbol switch
            {
                ',' => new MathText("\u2009", MathFace.Upright),
                ';' or ':' or '>' or ' ' => new MathText("\u2005", MathFace.Upright),
                '!' => null,
                '\\' => new MathText("\n", MathFace.Upright), // A new line (« aligned », « cases »).
                '|' => new MathText("‖", MathFace.Upright),
                _ => new MathText(symbol.ToString(), MathFace.Upright), // \{ \} \% \$ \_ \& \#
            };
        }

        int start = reader.At;
        while (!reader.AtEnd && char.IsAsciiLetter(reader.Current))
        {
            reader.At++;
        }

        string name = reader.Text[start..reader.At];
        if (!reader.AtEnd && reader.Current == '*')
        {
            reader.At++; // « \operatorname* », « \begin{align*} »: the same here.
        }

        switch (name)
        {
            case "frac" or "dfrac" or "tfrac" or "cfrac":
                return new MathFraction(Argument(reader) ?? Empty, Argument(reader) ?? Empty);
            case "binom" or "dbinom" or "tbinom":
                return new MathFence("(", new MathFraction(Argument(reader) ?? Empty, Argument(reader) ?? Empty, Rule: false), ")");
            case "sqrt":
                MathNode? index = null;
                reader.SkipSpaces();
                if (!reader.AtEnd && reader.Current == '[')
                {
                    reader.At++;
                    index = Row(reader, Stop.Bracket);
                }

                return new MathRoot(Argument(reader) ?? Empty, index);
            case "left":
                string open = Delimiter(reader);
                var body = Row(reader, Stop.Right);
                string close = "";
                if (reader.Next("\\right"))
                {
                    reader.At += "\\right".Length;
                    close = Delimiter(reader);
                }

                return new MathFence(open, body, close);
            case "right":
                Delimiter(reader);
                return null;
            case "big" or "Big" or "bigg" or "Bigg" or "bigl" or "bigr" or "Bigl" or "Bigr" or "biggl" or "biggr" or "Biggl" or "Biggr" or "bigm" or "Bigm":
            case "displaystyle" or "textstyle" or "scriptstyle" or "limits" or "nolimits" or "nonumber" or "notag" or "middle":
                return null;
            case "label" or "color" or "hspace" or "vspace" or "tag":
                Raw(reader);
                return null;
            case "textcolor":
                Raw(reader);
                return Argument(reader);
            case "text" or "textrm" or "textup" or "textnormal" or "mbox" or "textsf" or "texttt" or "textbf" or "textit" or "emph":
                return new MathText(Raw(reader), MathFace.Upright);
            case "mathrm" or "mathbf" or "mathsf" or "mathtt" or "boldsymbol" or "bm" or "operatorname" or "mathup":
                return Upright(Argument(reader) ?? Empty);
            case "mathit":
                return Argument(reader);
            case "mathbb":
                return new MathText(string.Concat(Raw(reader).Select(letter => DoubleStruck.GetValueOrDefault(letter, letter))), MathFace.Upright);
            case "mathcal" or "mathscr" or "mathfrak":
                return new MathText(Raw(reader), MathFace.Italic);
            case "vec":
                return new MathAccent(Argument(reader) ?? Empty, "\u20D7");
            case "overrightarrow" or "overleftrightarrow":
                return new MathAccent(Argument(reader) ?? Empty, "→");
            case "bar" or "overline":
                return new MathAccent(Argument(reader) ?? Empty, "\u0305");
            case "hat" or "widehat":
                return new MathAccent(Argument(reader) ?? Empty, "\u0302");
            case "tilde" or "widetilde":
                return new MathAccent(Argument(reader) ?? Empty, "\u0303");
            case "dot":
                return new MathAccent(Argument(reader) ?? Empty, "\u0307");
            case "ddot":
                return new MathAccent(Argument(reader) ?? Empty, "\u0308");
            case "underline" or "underbrace" or "overbrace" or "boxed" or "phantom":
                return Argument(reader);
            case "ce" or "pu":
                return Chemistry(Raw(reader));
            case "begin":
                string environment = Raw(reader);
                if (environment is "array" or "tabular")
                {
                    Raw(reader); // Its columns.
                }

                return null;
            case "end":
                Raw(reader);
                return null;
            case "quad":
                return new MathText("\u2003", MathFace.Upright);
            case "qquad":
                return new MathText("\u2003\u2003", MathFace.Upright);
        }

        if (Functions.Contains(name))
        {
            return new MathText(name, MathFace.Function);
        }

        if (Large.TryGetValue(name, out string? large))
        {
            return new MathText(large, MathFace.Large);
        }

        if (Symbols.TryGetValue(name, out string? sign))
        {
            var face = sign.Length == 1 && Operators.Contains(sign[0]) ? MathFace.Operator
                : sign.Length == 1 && sign[0] is >= 'α' and <= 'ω' ? MathFace.Italic
                : MathFace.Upright;
            return new MathText(sign, face);
        }

        return new MathText(name, MathFace.Upright); // Unknown: its name, as a word.
    }

    private static readonly MathText Empty = new("", MathFace.Upright);

    /// <summary>A bracket after \left or \right (« . »: none).</summary>
    private static string Delimiter(Reader reader)
    {
        reader.SkipSpaces();
        if (reader.AtEnd)
        {
            return "";
        }

        char c = reader.Text[reader.At++];
        if (c == '.')
        {
            return "";
        }

        if (c != '\\' || reader.AtEnd)
        {
            return c.ToString();
        }

        if (!char.IsAsciiLetter(reader.Current))
        {
            char escaped = reader.Text[reader.At++];
            return escaped == '|' ? "‖" : escaped.ToString();
        }

        int start = reader.At;
        while (!reader.AtEnd && char.IsAsciiLetter(reader.Current))
        {
            reader.At++;
        }

        return reader.Text[start..reader.At] switch
        {
            "langle" => "⟨",
            "rangle" => "⟩",
            "lbrace" => "{",
            "rbrace" => "}",
            "lbrack" => "[",
            "rbrack" => "]",
            "vert" or "lvert" or "rvert" => "|",
            "Vert" or "lVert" or "rVert" => "‖",
            "lfloor" => "⌊",
            "rfloor" => "⌋",
            "lceil" => "⌈",
            "rceil" => "⌉",
            _ => "",
        };
    }

    /// <summary>Everything upright (\mathrm, \mathbf, \operatorname): units, names.</summary>
    private static MathNode Upright(MathNode node) => node switch
    {
        MathText { Face: MathFace.Italic } text => text with { Face = MathFace.Upright },
        MathRow row => new MathRow(MergeLetters(row.Items.Select(Upright).ToList())),
        MathScripts scripts => new MathScripts(Upright(scripts.Base), scripts.Sub, scripts.Sup),
        _ => node,
    };

    /// <summary>Letters side by side, upright, as one word (« mol », « kg »).</summary>
    private static List<MathNode> MergeLetters(List<MathNode> items)
    {
        var merged = new List<MathNode>();
        foreach (var item in items)
        {
            if (item is MathText { Face: MathFace.Upright } text && merged.Count > 0 && merged[^1] is MathText { Face: MathFace.Upright } last
                && text.Text.Length > 0 && char.IsLetterOrDigit(text.Text[0]) && last.Text.Length > 0 && char.IsLetterOrDigit(last.Text[^1]))
            {
                merged[^1] = last with { Text = last.Text + text.Text };
            }
            else
            {
                merged.Add(item);
            }
        }

        return merged;
    }

    /// <summary>
    /// Chemistry (\ce{…}): elements upright, their counts as indices (H₂O), charges as exponents (SO₄²⁻, Na⁺), the arrows
    /// (« -> », « &lt;=> »), a « + » between species spaced.
    /// </summary>
    private static MathNode Chemistry(string formula)
    {
        var items = new List<MathNode>();
        int i = 0;
        while (i < formula.Length)
        {
            char c = formula[i];
            string rest = formula[i..];
            if (rest.StartsWith("<=>", StringComparison.Ordinal) || rest.StartsWith("<->", StringComparison.Ordinal))
            {
                items.Add(new MathText(rest[1] == '=' ? "⇌" : "↔", MathFace.Operator));
                i += 3;
            }
            else if (rest.StartsWith("->", StringComparison.Ordinal) || rest.StartsWith("<-", StringComparison.Ordinal))
            {
                items.Add(new MathText(rest[0] == '-' ? "→" : "←", MathFace.Operator));
                i += 2;
            }
            else if (c == '^')
            {
                i++;
                int start = i;
                if (i < formula.Length && formula[i] == '{')
                {
                    int close = formula.IndexOf('}', i);
                    close = close < 0 ? formula.Length : close;
                    Attach(items, null, new MathText(formula[(i + 1)..close].Replace('-', '−'), MathFace.Upright));
                    i = Math.Min(close + 1, formula.Length);
                }
                else
                {
                    while (i < formula.Length && (char.IsAsciiDigit(formula[i]) || formula[i] is '+' or '-'))
                    {
                        i++;
                    }

                    Attach(items, null, new MathText(formula[start..i].Replace('-', '−'), MathFace.Upright));
                }
            }
            else if (char.IsAsciiDigit(c) && items.Count > 0 && EndsSpecies(items[^1]))
            {
                int start = i;
                while (i < formula.Length && char.IsAsciiDigit(formula[i]))
                {
                    i++;
                }

                Attach(items, new MathText(formula[start..i], MathFace.Upright), null);
            }
            else if (c is '+' or '-' && items.Count > 0 && EndsSpecies(items[^1]) && formula[i - 1] != ' '
                && (i + 1 >= formula.Length || formula[i + 1] is ' ' or '}' or ')'))
            {
                Attach(items, null, new MathText(c == '-' ? "−" : "+", MathFace.Upright)); // A charge: Na+, Cl-.
                i++;
            }
            else if (c == '+')
            {
                items.Add(new MathText("+", MathFace.Operator));
                i++;
            }
            else if (c == ' ')
            {
                i++;
            }
            else
            {
                items.Add(new MathText(c.ToString(), MathFace.Upright));
                i++;
            }
        }

        return new MathRow(MergeLetters(items));
    }

    /// <summary>Whether a chemical species ends here (an element, a bracket, an index): a number after it is a count.</summary>
    private static bool EndsSpecies(MathNode node) => node switch
    {
        MathText text => text.Text.Length > 0 && (char.IsAsciiLetter(text.Text[^1]) || text.Text[^1] is ')' or ']'),
        MathScripts => true,
        _ => false,
    };

    /// <summary>Whether a formula is taller than a line of words: a fraction, a root, a big operator.</summary>
    public static bool IsTall(MathNode node) => node switch
    {
        MathFraction or MathRoot or MathText { Face: MathFace.Large } => true,
        MathRow row => row.Items.Any(IsTall),
        MathScripts scripts => IsTall(scripts.Base) || (scripts.Sub is { } sub && IsTall(sub)) || (scripts.Sup is { } sup && IsTall(sup)),
        MathFence fence => IsTall(fence.Body),
        MathAccent accent => IsTall(accent.Body),
        _ => false,
    };

    /// <summary>A formula's lines (« \\ » in « aligned », « cases »), each on its own; one if it has a single line.</summary>
    public static IReadOnlyList<MathNode> Lines(MathNode node)
    {
        if (node is not MathRow row || !row.Items.Any(item => item is MathText { Text: "\n" }))
        {
            return [node];
        }

        var lines = new List<MathNode>();
        var line = new List<MathNode>();
        foreach (var item in row.Items.Append(new MathText("\n", MathFace.Upright)))
        {
            if (item is MathText { Text: "\n" })
            {
                if (line.Count > 0)
                {
                    lines.Add(line.Count == 1 ? line[0] : new MathRow([.. line]));
                }

                line.Clear();
            }
            else
            {
                line.Add(item);
            }
        }

        return lines.Count == 0 ? [node] : lines;
    }

    // ── One line of Unicode ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A formula in one line of Unicode: « n(A)₀/a = (C_B × V_B,E)/b », « 10⁻³ », « √(x + 1) ».</summary>
    public static string Linear(MathNode node)
    {
        var line = new StringBuilder();
        Write(line, node);
        return Spaces().Replace(line.ToString(), " ").Trim();
    }

    private static void Write(StringBuilder line, MathNode node)
    {
        switch (node)
        {
            case MathText text:
                line.Append(text.Text == "\n" ? " ; " : text.Face == MathFace.Operator ? $" {text.Text} " : text.Text);
                break;
            case MathRow row:
                WriteRow(line, row.Items);
                break;
            case MathScripts scripts:
                Write(line, scripts.Base);
                if (scripts.Sub is { } sub)
                {
                    string index = Linear(sub);
                    line.Append(Convert(index, Subscripts) ?? "_" + Wrap(index));
                }

                if (scripts.Sup is { } sup)
                {
                    string power = Linear(sup);
                    line.Append(Convert(power, Superscripts) ?? "^" + Wrap(power));
                }

                break;
            case MathFraction { Rule: false } pair:
                line.Append(Linear(pair.Over)).Append(", ").Append(Linear(pair.Under));
                break;
            case MathFraction fraction:
                string under = Linear(fraction.Under);
                line.Append(Wrap(Linear(fraction.Over))).Append('/').Append(IsAtom(under) ? under : $"({under})");
                break;
            case MathRoot root:
                string degree = root.Index is null ? "" : Linear(root.Index);
                line.Append(degree switch
                {
                    "" or "2" => "√",
                    "3" => "∛",
                    "4" => "∜",
                    _ => (Convert(degree, Superscripts) ?? degree) + "√",
                });
                line.Append(Wrap(Linear(root.Body)));
                break;
            case MathFence fence:
                line.Append(fence.Open).Append(Linear(fence.Body)).Append(fence.Close);
                break;
            case MathAccent accent:
                string marked = Linear(accent.Body);
                line.Append(marked).Append(accent.Mark == "→" ? "\u20D7" : accent.Mark);
                break;
        }
    }

    private static void WriteRow(StringBuilder line, IReadOnlyList<MathNode> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item is MathText { Face: MathFace.Operator } op && IsUnary(items, i))
            {
                line.Append(op.Text); // « −x », « (−1) »: a sign, not spaced.
                continue;
            }

            if (IsFunction(item) && line.Length > 0 && char.IsLetterOrDigit(line[^1]))
            {
                line.Append(' ');
            }

            Write(line, item);
            if (IsFunction(item) && i + 1 < items.Count && !StartsWithBracket(items[i + 1]))
            {
                line.Append(' ');
            }
        }
    }

    /// <summary>A function's name or a big operator (with its limits, if any): set apart from its neighbours.</summary>
    public static bool IsFunction(MathNode node) => node switch
    {
        MathText { Face: MathFace.Function or MathFace.Large } => true,
        MathScripts { Base: MathText { Face: MathFace.Function or MathFace.Large } } => true,
        _ => false,
    };

    /// <summary>A « − » or « + » with nothing before it to act on: a sign.</summary>
    private static bool IsUnary(IReadOnlyList<MathNode> items, int index) =>
        items[index] is MathText { Text: "−" or "+" or "±" or "∓" }
        && (index == 0 || items[index - 1] is MathText { Face: MathFace.Operator } or MathText { Text: "(" or "[" or "{" or "," or ";" or "\n" });

    private static bool StartsWithBracket(MathNode node) => node switch
    {
        MathText text => text.Text.StartsWith('('),
        MathFence => true,
        MathScripts scripts => StartsWithBracket(scripts.Base),
        _ => false,
    };

    /// <summary>
    /// A denominator that needs no brackets: a number, or one symbol with its index or exponent (« a », « V_A », « 10³ »);
    /// « 5C » takes them (« 7L/(5C) », not 7L/5 × C).
    /// </summary>
    private static bool IsAtom(string text)
    {
        if (text.Length <= 1 || text.All(c => char.IsDigit(c) || c is '.' or ','))
        {
            return true;
        }

        if (text[0] == '(' && text[^1] == ')' && text.IndexOf(')') == text.Length - 1)
        {
            return true;
        }

        // One letter, then only its index or exponent.
        int script = text.IndexOfAny(['_', '^']);
        string head = script < 0 ? text : text[..script];
        return head.Length > 0 && char.IsLetter(head[0]) && head.Skip(1).All(c => !char.IsLetterOrDigit(c) || Subscripts.ContainsValue(c) || Superscripts.ContainsValue(c));
    }

    /// <summary>In brackets if it is more than one term (« (C_A × V_A)/a »), as it is if not (« n(A)₀/a »).</summary>
    private static string Wrap(string text)
    {
        int depth = 0;
        foreach (char c in text)
        {
            depth += c is '(' or '[' or '{' ? 1 : c is ')' or ']' or '}' ? -1 : 0;
            if (depth == 0 && (c == ' ' || Operators.Contains(c) || c is '/' or '-' or ','))
            {
                return $"({text})";
            }
        }

        return text;
    }

    /// <summary><paramref name="text"/> in index or exponent characters; <c>null</c> if one of them has none.</summary>
    private static string? Convert(string text, IReadOnlyDictionary<char, char> map)
    {
        if (text.Length == 0)
        {
            return "";
        }

        // Small letters read badly beyond one (« dₚᵣₒₚ »): a word stays « _prop »; one letter (xᵢ) and digits (H₂O, 10⁻³) go small.
        if (text.Count(char.IsLetter) > 1)
        {
            return null;
        }

        var converted = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!map.TryGetValue(c, out char small))
            {
                return null;
            }

            converted.Append(small);
        }

        return converted.ToString();
    }

    /// <summary>Whether a whole text converts to index characters (H₂O) — else it is drawn small.</summary>
    public static string? AsSubscript(string text) => Convert(text, Subscripts);

    /// <summary>Whether a whole text converts to exponent characters (10⁻³) — else it is drawn small.</summary>
    public static string? AsSuperscript(string text) => Convert(text, Superscripts);

    // ── Tables ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Signs set with space around them (relations, operations, arrows).</summary>
    private static readonly HashSet<char> Operators =
    [
        '+', '−', '=', '<', '>', '×', '·', '÷', '±', '∓', '≤', '≥', '≠', '≈', '≃', '≅', '≡', '∼', '∝', '→', '←', '↔', '⇒',
        '⇐', '⇔', '⇌', '⟶', '⟹', '⟺', '↦', '∈', '∉', '⊂', '⊆', '⊃', '⊇', '∪', '∩', '∧', '∨', '⊕', '⊗', '∘', '≪', '≫', '∗', '∣', '⊥', '∥',
    ];

    private static readonly HashSet<string> Functions =
    [
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "coth", "ln", "log", "lg",
        "exp", "lim", "limsup", "liminf", "sup", "inf", "max", "min", "det", "dim", "ker", "deg", "arg", "gcd", "Pr", "mod", "bmod",
        "pmod", "sgn", "ch", "sh", "th", "Re", "Im", "argmax", "argmin",
    ];

    private static readonly Dictionary<string, string> Large = new()
    {
        ["sum"] = "∑", ["prod"] = "∏", ["coprod"] = "∐", ["int"] = "∫", ["iint"] = "∬", ["iiint"] = "∭", ["oint"] = "∮",
        ["bigcup"] = "⋃", ["bigcap"] = "⋂", ["bigoplus"] = "⨁", ["bigotimes"] = "⨂",
    };

    private static readonly Dictionary<string, string> Symbols = new()
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε", ["varepsilon"] = "ε", ["zeta"] = "ζ",
        ["eta"] = "η", ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ",
        ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π", ["varpi"] = "ϖ", ["rho"] = "ρ", ["varrho"] = "ϱ", ["sigma"] = "σ",
        ["varsigma"] = "ς", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "φ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ",
        ["omega"] = "ω", ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π",
        ["Sigma"] = "Σ", ["Upsilon"] = "Υ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["times"] = "×", ["cdot"] = "·", ["cdotp"] = "·", ["div"] = "÷", ["pm"] = "±", ["mp"] = "∓", ["ast"] = "∗", ["star"] = "⋆",
        ["circ"] = "∘", ["bullet"] = "•", ["oplus"] = "⊕", ["otimes"] = "⊗", ["wedge"] = "∧", ["land"] = "∧", ["vee"] = "∨",
        ["lor"] = "∨", ["neg"] = "¬", ["lnot"] = "¬", ["cup"] = "∪", ["cap"] = "∩", ["setminus"] = "∖",
        ["leq"] = "≤", ["le"] = "≤", ["leqslant"] = "≤", ["geq"] = "≥", ["ge"] = "≥", ["geqslant"] = "≥", ["neq"] = "≠", ["ne"] = "≠",
        ["approx"] = "≈", ["simeq"] = "≃", ["cong"] = "≅", ["equiv"] = "≡", ["sim"] = "∼", ["propto"] = "∝", ["ll"] = "≪", ["gg"] = "≫",
        ["in"] = "∈", ["notin"] = "∉", ["ni"] = "∋", ["subset"] = "⊂", ["subseteq"] = "⊆", ["supset"] = "⊃", ["supseteq"] = "⊇",
        ["perp"] = "⊥", ["parallel"] = "∥", ["mid"] = "∣",
        ["to"] = "→", ["rightarrow"] = "→", ["longrightarrow"] = "⟶", ["leftarrow"] = "←", ["gets"] = "←", ["longleftarrow"] = "⟵",
        ["leftrightarrow"] = "↔", ["Rightarrow"] = "⇒", ["implies"] = "⟹", ["Longrightarrow"] = "⟹", ["Leftarrow"] = "⇐",
        ["Leftrightarrow"] = "⇔", ["iff"] = "⟺", ["Longleftrightarrow"] = "⟺", ["rightleftharpoons"] = "⇌", ["mapsto"] = "↦",
        ["uparrow"] = "↑", ["downarrow"] = "↓", ["nearrow"] = "↗", ["searrow"] = "↘",
        ["infty"] = "∞", ["partial"] = "∂", ["nabla"] = "∇", ["forall"] = "∀", ["exists"] = "∃", ["nexists"] = "∄",
        ["emptyset"] = "∅", ["varnothing"] = "∅", ["angle"] = "∠", ["degree"] = "°", ["circledast"] = "⊛", ["prime"] = "′",
        ["ell"] = "ℓ", ["hbar"] = "ℏ", ["Re"] = "ℜ", ["Im"] = "ℑ", ["aleph"] = "ℵ", ["triangle"] = "△", ["square"] = "□",
        ["therefore"] = "∴", ["because"] = "∵", ["ldots"] = "…", ["dots"] = "…", ["cdots"] = "⋯", ["vdots"] = "⋮", ["ddots"] = "⋱",
        ["langle"] = "⟨", ["rangle"] = "⟩", ["lbrace"] = "{", ["rbrace"] = "}", ["lfloor"] = "⌊", ["rfloor"] = "⌋",
        ["lceil"] = "⌈", ["rceil"] = "⌉", ["vert"] = "|", ["Vert"] = "‖", ["lvert"] = "|", ["rvert"] = "|", ["backslash"] = "\\",
        ["percent"] = "%", ["euro"] = "€", ["checkmark"] = "✓", ["dagger"] = "†", ["S"] = "§", ["copyright"] = "©",
    };

    private static readonly Dictionary<char, char> DoubleStruck = new()
    {
        ['N'] = 'ℕ', ['Z'] = 'ℤ', ['Q'] = 'ℚ', ['R'] = 'ℝ', ['C'] = 'ℂ', ['P'] = 'ℙ', ['H'] = 'ℍ',
    };

    private static readonly Dictionary<char, char> Subscripts = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄', ['5'] = '₅', ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉',
        ['+'] = '₊', ['−'] = '₋', ['-'] = '₋', ['='] = '₌', ['('] = '₍', [')'] = '₎',
        ['a'] = 'ₐ', ['e'] = 'ₑ', ['h'] = 'ₕ', ['i'] = 'ᵢ', ['j'] = 'ⱼ', ['k'] = 'ₖ', ['l'] = 'ₗ', ['m'] = 'ₘ', ['n'] = 'ₙ',
        ['o'] = 'ₒ', ['p'] = 'ₚ', ['r'] = 'ᵣ', ['s'] = 'ₛ', ['t'] = 'ₜ', ['u'] = 'ᵤ', ['v'] = 'ᵥ', ['x'] = 'ₓ',
    };

    private static readonly Dictionary<char, char> Superscripts = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴', ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹',
        ['+'] = '⁺', ['−'] = '⁻', ['-'] = '⁻', ['='] = '⁼', ['('] = '⁽', [')'] = '⁾', ['n'] = 'ⁿ', ['i'] = 'ⁱ', ['x'] = 'ˣ',
        ['′'] = '′', ['°'] = '°', ['∗'] = '*',
    };

    // A formula too tall for a line of words: a fraction, a root, a sum, an integral, a limit, several lines.
    [GeneratedRegex(@"\\(?:[dtc]?frac|[dt]?binom|sqrt|sum|prod|i*int|oint|lim|begin|over(?![a-z]))(?![a-zA-Z])")]
    private static partial Regex Tall();

    [GeneratedRegex(@"\\[a-zA-Z]+")]
    private static partial Regex Command();

    [GeneratedRegex(@"[\^_](?:\{|[\w+\-−])")]
    private static partial Regex Script();

    [GeneratedRegex(@"\$\$[ \t]*\r?\n(?<body>(?:(?!\$\$).)*?)\r?\n[ \t]*\$\$", RegexOptions.Singleline)]
    private static partial Regex Multiline();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex Spaces();
}
