using Microsoft.UI.Text;

namespace MyNotch.Modules.Notes.Editor;

/// <summary>
/// Turns a note written in Markdown (before the editor became a rich one) into formatting: titles, lists, check
/// lists, quotes, code, bold, italic and struck text. The marks themselves go.
/// </summary>
internal static class MarkdownImport
{
    private const string CodeFont = "Cascadia Mono";

    public static void Into(RichNoteEditor editor, string markdown)
    {
        var source = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var parsed = MarkdownSyntax.Parse(source);

        // The text without its marks, line by line (code fences go), with the inline styles moved to match.
        var lines = new List<(string Text, MarkdownLine Info, List<InlineSpan> Spans)>();
        var spans = new List<InlineSpan>();
        for (int i = 0; i < source.Length; i++)
        {
            var info = parsed[i];
            if (info.Kind == LineKind.Fence)
            {
                continue;
            }

            string line = source[i];
            int from = info.Kind is LineKind.Code or LineKind.Rule ? 0 : Math.Min(info.MarkerLength, line.Length);
            spans.Clear();
            if (info.HasInlineStyles)
            {
                MarkdownSyntax.Inline(line, from, spans);
            }

            var text = new System.Text.StringBuilder();
            var moved = new List<InlineSpan>();
            int position = from;
            foreach (var span in spans)
            {
                text.Append(line, position, span.Start - position);
                if (span.Style != InlineStyle.Mark)
                {
                    moved.Add(span with { Start = text.Length });
                    text.Append(line, span.Start, span.Length);
                }

                position = span.Start + span.Length;
            }

            text.Append(line, position, line.Length - position);
            lines.Add((info.Kind == LineKind.Rule ? "———" : text.ToString(), info, moved));
        }

        var document = editor.Document;
        document.SetText(TextSetOptions.None, string.Join("\r", lines.Select(line => line.Text)));

        // From the last line up: a box added to a line moves the ones below, never the ones above.
        var starts = new int[lines.Count];
        for (int i = 1; i < lines.Count; i++)
        {
            starts[i] = starts[i - 1] + lines[i - 1].Text.Length + 1;
        }

        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var (text, info, moved) = lines[i];
            int start = starts[i];
            foreach (var span in moved)
            {
                var range = document.GetRange(start + span.Start, start + span.Start + span.Length);
                var format = range.CharacterFormat.GetClone();
                switch (span.Style)
                {
                    case InlineStyle.Bold:
                        format.Bold = FormatEffect.On;
                        break;
                    case InlineStyle.Italic:
                        format.Italic = FormatEffect.On;
                        break;
                    case InlineStyle.Strike:
                        format.Strikethrough = FormatEffect.On;
                        break;
                    default:
                        format.Name = CodeFont;
                        break;
                }

                range.CharacterFormat = format;
            }

            var whole = document.GetRange(start, start + text.Length);
            switch (info.Kind)
            {
                case LineKind.Quote:
                    var quote = whole.CharacterFormat.GetClone();
                    quote.Italic = FormatEffect.On;
                    whole.CharacterFormat = quote;
                    break;

                case LineKind.Code:
                    var code = whole.CharacterFormat.GetClone();
                    code.Name = CodeFont;
                    code.Size = 10;
                    whole.CharacterFormat = code;
                    break;
            }

            document.Selection.SetRange(start, start);
            switch (info.Kind)
            {
                case LineKind.Heading1:
                    editor.ApplyStyle(NoteStyle.Title);
                    break;
                case LineKind.Heading2:
                    editor.ApplyStyle(NoteStyle.Heading);
                    break;
                case LineKind.Heading3:
                    editor.ApplyStyle(NoteStyle.Subheading);
                    break;
                case LineKind.Bullet:
                    editor.ToggleList(NoteList.Bullets);
                    break;
                case LineKind.Numbered:
                    editor.ToggleList(NoteList.Numbers);
                    break;
                case LineKind.Task or LineKind.DoneTask:
                    editor.ImportBox(start, done: info.Kind == LineKind.DoneTask);
                    break;
            }
        }
    }
}
