using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MyNotch.Modules.Clipboard;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using MyNotch.Localization;

namespace MyNotch.Modules.Notes.Editor;

/// <summary>The paragraph styles of the toolbar.</summary>
public enum NoteStyle
{
    Body,
    Title,
    Heading,
    Subheading,
}

/// <summary>What a paragraph is a part of.</summary>
public enum NoteList
{
    None,
    Bullets,
    Numbers,
    Checks,
}

/// <summary>The formatting where the caret is, for the toolbar to show (<c>null</c>: mixed in the selection).</summary>
public readonly record struct NoteFormat(bool Bold, bool Italic, bool Underline, bool Strike, float? Size, NoteStyle? Style, NoteList List);

/// <summary>
/// A text editor the way Word does it, in the notch: sizes, bold, italic, underline, struck, titles, bulleted and
/// numbered lists, and check lists (a click on the box ticks it). Kept as RTF.
/// </summary>
/// <remarks>
/// <para>
/// Lists are RichEdit's own. A check list is a paragraph starting with a round box and an en space: Enter goes on
/// with a new box (an empty one ends the list), Backspace just after a box removes it, Ctrl+Enter ticks it.
/// </para>
/// <para>
/// Markdown habits work too: "- " or "* " starts a list, "1. " a numbered one, "[] " a check list, "# ", "## " and
/// "### " a title. Pasting takes the text only (no fonts and colours from elsewhere); copying keeps the formatting.
/// </para>
/// </remarks>
public sealed partial class RichNoteEditor : RichEditBox
{
    public const float BodySize = 11;

    /// <summary>The sizes the toolbar steps through, in points, like Word's.</summary>
    public static readonly float[] Sizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72];

    // Not the variable "Segoe UI Variable": RichEdit draws it too heavy.
    private const string TextFont = "Segoe UI";
    private const string DisplayFont = "Segoe UI";
    // Round boxes, as in Apple's Notes: a thin ring to do, a filled disc with a check once done.
    private const string BoxFont = "Segoe Fluent Icons";
    private const char Box = ''; // CircleRing
    private const char Ticked = ''; // CompletedSolid
    private const char OldBox = '☐'; // Notes from before (and pasted lists); "Plain" still writes these.
    private const char OldTicked = '☑';
    private const char Gap = ' '; // An en space between the box and the text.
    private const float CheckIndent = 16;
    private const float ListIndent = 22;

    private static readonly Color BoxInk = Color.FromArgb(255, 0x8E, 0x8E, 0x93);
    private static readonly Color TickedInk = Color.FromArgb(255, 0xFF, 0xC8, 0x57); // The amber of the open card.

    private int _loading;
    private Canvas? _layer;
    private BoxButton? _boxButton;
    private int _hotBox = -1;
    private (UIElement Origin, double Scale)? _clientSpace;

    public RichNoteEditor()
    {
        TextWrapping = TextWrapping.Wrap;
        IsTextPredictionEnabled = false;
        SelectionFlyout = null;
        ContextFlyout = CreateMenu();

        TextChanged += (_, _) =>
        {
            if (_loading == 0)
            {
                Edited?.Invoke(this, EventArgs.Empty);
            }
        };
        SelectionChanged += (_, _) => FormatChanged?.Invoke(this, EventArgs.Empty);
        Paste += OnPaste;
        PreviewKeyDown += OnPreviewKeyDown;
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), handledEventsToo: true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        PreviewKeyDown += (_, _) => HideBoxButton();

        // Another screen (another scale) or another template: the coordinates are read again.
        Loaded += (_, _) => _clientSpace = null;
        SizeChanged += (_, _) => _clientSpace = null;
    }

    /// <summary>The text or its formatting changed.</summary>
    public event EventHandler? Edited;

    /// <summary>The caret moved, or the formatting under it changed: the toolbar shows it again.</summary>
    public event EventHandler? FormatChanged;

    /// <summary>Escape: done typing.</summary>
    public event EventHandler? Escaped;

    /// <summary>The note, formatted.</summary>
    public string Rtf
    {
        get
        {
            Document.GetText(TextGetOptions.FormatRtf, out string rtf);
            return rtf;
        }
    }

    /// <summary>The note without formatting, lines separated by <c>\n</c>.</summary>
    public string Plain
    {
        get
        {
            Document.GetText(TextGetOptions.None, out string text);
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace(Gap, ' ')
                .Replace(Box, OldBox).Replace(Ticked, OldTicked);
            return text.TrimEnd('\n');
        }
    }

    public bool IsEmpty => Plain.Length == 0;

    /// <summary>Shows a note: RTF, or Markdown from before (turned into formatting). Nothing to undo.</summary>
    public void Load(string content)
    {
        HideBoxButton();
        _clientSpace = null;
        _loading++;
        try
        {
            if (content.StartsWith(@"{\rtf", StringComparison.Ordinal))
            {
                // Notes saved while the editor used the variable font take the plain one.
                Document.SetText(TextSetOptions.FormatRtf, content
                    .Replace("Segoe UI Variable Display", TextFont, StringComparison.Ordinal)
                    .Replace("Segoe UI Variable Text", TextFont, StringComparison.Ordinal));

                // Square boxes from before turn round.
                Document.GetText(TextGetOptions.None, out string text);
                foreach (int start in ParagraphStarts(0, text.Length).Where(HasBox))
                {
                    StyleBox(start);
                }
            }
            else
            {
                Document.SetText(TextSetOptions.None, "");
                ResetTyping();
                MarkdownImport.Into(this, content);
            }

            Document.ClearUndoRedoHistory();
            Document.Selection.SetRange(0, 0);
            ResetTyping();
            Document.Selection.ScrollIntoView(PointOptions.Start);
        }
        finally
        {
            // TextChanged comes a moment later: still loading then.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _loading--);
        }
    }

    // ── What the toolbar reads ──────────────────────────────────────────────────────────────────────────────

    public NoteFormat ReadFormat()
    {
        var selection = Document.Selection;
        var character = selection.CharacterFormat;
        float size = character.Size;
        bool known = size > 0;
        return new NoteFormat(
            character.Bold == FormatEffect.On,
            character.Italic == FormatEffect.On,
            character.Underline is not (UnderlineType.None or UnderlineType.Undefined),
            character.Strikethrough == FormatEffect.On,
            known ? size : null,
            known ? StyleOf(size, character.Weight) : null,
            ListAt(selection.StartPosition));
    }

    private static NoteStyle? StyleOf(float size, int weight) => (size, weight >= 600) switch
    {
        (22, true) => NoteStyle.Title,
        (17, true) => NoteStyle.Heading,
        (13.5f, true) => NoteStyle.Subheading,
        (BodySize, false) => NoteStyle.Body,
        _ => null,
    };

    private NoteList ListAt(int position)
    {
        var paragraph = Paragraph(position);
        if (HasBox(paragraph.Start))
        {
            return NoteList.Checks;
        }

        return paragraph.Range.ParagraphFormat.ListType switch
        {
            MarkerType.Bullet => NoteList.Bullets,
            MarkerType.None or MarkerType.Undefined => NoteList.None,
            _ => NoteList.Numbers,
        };
    }

    // ── What the toolbar does ───────────────────────────────────────────────────────────────────────────────

    public void ToggleBold() => Format(format => format.Bold = FormatEffect.Toggle);

    public void ToggleItalic() => Format(format => format.Italic = FormatEffect.Toggle);

    public void ToggleUnderline() => Format(format =>
        format.Underline = format.Underline is UnderlineType.None ? UnderlineType.Single : UnderlineType.None);

    public void ToggleStrike() => Format(format => format.Strikethrough = FormatEffect.Toggle);

    /// <summary>One size up (or down) in <see cref="Sizes"/>, from the size at the start of the selection.</summary>
    public void StepSize(int direction)
    {
        float current = Document.Selection.CharacterFormat.Size;
        if (current <= 0)
        {
            current = BodySize;
        }

        float next = direction > 0
            ? Sizes.FirstOrDefault(size => size > current + 0.1f, Sizes[^1])
            : Sizes.LastOrDefault(size => size < current - 0.1f, Sizes[0]);
        Format(format => format.Size = next);
    }

    /// <summary>Gives the paragraphs of the selection a style: text, title, heading or subheading.</summary>
    public void ApplyStyle(NoteStyle style)
    {
        var (font, size, weight, before, after) = style switch
        {
            NoteStyle.Title => (DisplayFont, 22f, 600, 10f, 4f),
            NoteStyle.Heading => (DisplayFont, 17f, 600, 8f, 3f),
            NoteStyle.Subheading => (TextFont, 13.5f, 600, 6f, 2f),
            _ => (TextFont, BodySize, 400, 0f, 0f),
        };

        var range = Document.Selection.GetClone();
        range.Expand(TextRangeUnit.Paragraph);
        Change(() =>
        {
            var character = range.CharacterFormat.GetClone();
            character.Name = font;
            character.Size = size;
            character.Bold = FormatEffect.Off;
            character.Weight = weight;
            range.CharacterFormat = character;
            var paragraph = range.ParagraphFormat.GetClone();
            paragraph.SpaceBefore = before;
            paragraph.SpaceAfter = after;
            range.ParagraphFormat = paragraph;

            // The boxes of a check list keep their own font.
            foreach (int start in ParagraphStarts(range.StartPosition, range.EndPosition).Where(HasBox))
            {
                StyleBox(start);
            }

            if (Document.Selection.Length == 0)
            {
                var typing = Document.Selection.CharacterFormat.GetClone();
                typing.Name = font;
                typing.Size = size;
                typing.Bold = FormatEffect.Off;
                typing.Weight = weight;
                Document.Selection.CharacterFormat = typing;
            }
        });
    }

    /// <summary>A bulleted, numbered or check list for the paragraphs of the selection; the same again removes it.</summary>
    public void ToggleList(NoteList list)
    {
        var selection = Document.Selection;
        var starts = ParagraphStarts(selection.StartPosition, selection.EndPosition);
        bool off = ListAt(selection.StartPosition) == list;
        Change(() =>
        {
            // From the last paragraph up: the positions above stay right as boxes come and go.
            foreach (int start in starts.AsEnumerable().Reverse())
            {
                bool boxed = HasBox(start);
                if (boxed && (off || list != NoteList.Checks))
                {
                    RemoveBox(start);
                }

                var paragraph = Paragraph(start).Range;
                var format = paragraph.ParagraphFormat.GetClone();
                if (off || list == NoteList.Checks)
                {
                    format.ListType = MarkerType.None;
                    format.SetIndents(0, 0, 0);
                }
                else
                {
                    format.ListType = list == NoteList.Bullets ? MarkerType.Bullet : MarkerType.Arabic;
                    format.ListStyle = MarkerStyle.Period;
                    format.ListStart = 1;
                    format.ListTab = 14;
                    format.SetIndents(-14, ListIndent, 0);
                }

                paragraph.ParagraphFormat = format;
                if (!off && list == NoteList.Checks && !boxed)
                {
                    AddBox(start);
                }
            }
        });
    }

    // ── Check lists ─────────────────────────────────────────────────────────────────────────────────────────

    private bool HasBox(int start)
    {
        var range = Document.GetRange(start, start + 2);
        range.GetText(TextGetOptions.None, out string text);
        // A plain space too: a list pasted, or typed elsewhere ("☐ Acheter du pain").
        return text.Length == 2 && text[0] is Box or Ticked or OldBox or OldTicked && text[1] is Gap or ' ';
    }

    private void AddBox(int start)
    {
        Document.GetRange(start, start).SetText(TextSetOptions.None, $"{Box}{Gap}");
        StyleBox(start);
        var gap = Document.GetRange(start + 1, start + 2).CharacterFormat.GetClone();
        gap.Strikethrough = FormatEffect.Off;
        Document.GetRange(start + 1, start + 2).CharacterFormat = gap;
        Indent(start, CheckIndent);
    }

    private void RemoveBox(int start)
    {
        Document.GetRange(start, start + 2).SetText(TextSetOptions.None, "");
        Indent(start, 0);
    }

    /// <summary>A hanging indent: the lines of a long item start under its text, not under its box.</summary>
    private void Indent(int start, float indent)
    {
        var paragraph = Paragraph(start).Range;
        var format = paragraph.ParagraphFormat.GetClone();
        format.SetIndents(-indent, indent, 0);
        paragraph.ParagraphFormat = format;
    }

    /// <summary>The box in its icon font, a grey ring while to do, an amber disc once done (a square one turns round).</summary>
    private void StyleBox(int start)
    {
        var box = Document.GetRange(start, start + 1);
        bool done = IsTicked(box);
        box.GetText(TextGetOptions.None, out string glyph);
        if (glyph[0] is OldBox or OldTicked)
        {
            box.SetText(TextSetOptions.None, (done ? Ticked : Box).ToString());
        }

        var format = box.CharacterFormat.GetClone();
        format.Name = BoxFont;
        format.Strikethrough = FormatEffect.Off;
        format.ForegroundColor = done ? TickedInk : BoxInk;
        box.CharacterFormat = format;
    }

    private static bool IsTicked(ITextRange box)
    {
        box.GetText(TextGetOptions.None, out string glyph);
        return glyph.Length > 0 && glyph[0] is Ticked or OldTicked;
    }

    /// <summary>Ticks the box at <paramref name="start"/> (or unticks it): its text is struck while done.</summary>
    private void Tick(int start)
    {
        var box = Document.GetRange(start, start + 1);
        bool done = !IsTicked(box);
        var selection = Document.Selection;
        int from = selection.StartPosition;
        int to = selection.EndPosition;
        Change(() =>
        {
            box.SetText(TextSetOptions.None, (done ? Ticked : Box).ToString());
            StyleBox(start);
            var paragraph = Paragraph(start);
            var text = Document.GetRange(start + 2, Math.Max(start + 2, paragraph.End));
            var format = text.CharacterFormat.GetClone();
            format.Strikethrough = done ? FormatEffect.On : FormatEffect.Off;
            text.CharacterFormat = format;
        });
        Document.Selection.SetRange(from, to);
    }

    // ── Keys ────────────────────────────────────────────────────────────────────────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        bool alt = IsDown(VirtualKey.Menu);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                Escaped?.Invoke(this, EventArgs.Empty);
                break;

            case VirtualKey.Enter when control && !alt:
                e.Handled = true;
                var start = Paragraph(Document.Selection.StartPosition).Start;
                if (HasBox(start))
                {
                    Tick(start);
                }
                else
                {
                    ToggleList(NoteList.Checks);
                }

                break;

            case VirtualKey.Enter when !control:
                e.Handled = NewParagraph(soft: shift);
                break;

            case VirtualKey.Back when !control:
                e.Handled = Unmark();
                break;

            case VirtualKey.Space when !control && !alt:
                e.Handled = AutoFormat();
                break;

            case VirtualKey.B or VirtualKey.I or VirtualKey.U when control && !alt:
                // RichEdit formats; the note has changed all the same.
                DispatcherQueue.TryEnqueue(() =>
                {
                    Edited?.Invoke(this, EventArgs.Empty);
                    FormatChanged?.Invoke(this, EventArgs.Empty);
                });
                break;
        }
    }

    /// <summary>
    /// Enter in a check list: a new box (an empty item ends the list). Enter on an empty bulleted or numbered item
    /// ends that list. Shift+Enter is a plain new paragraph (RichEdit's own line break is not kept).
    /// </summary>
    private bool NewParagraph(bool soft)
    {
        var selection = Document.Selection;
        var paragraph = Paragraph(selection.StartPosition);
        bool boxed = HasBox(paragraph.Start);
        string text = paragraph.Text;
        if (soft)
        {
            Change(() => selection.TypeText("\r"));
            return true;
        }

        if (boxed && selection.Length == 0 && text.Length <= 2)
        {
            Change(() => RemoveBox(paragraph.Start));
            return true;
        }

        if (!boxed && selection.Length == 0 && text.Length == 0 && ListAt(paragraph.Start) != NoteList.None)
        {
            ToggleList(ListAt(paragraph.Start));
            return true;
        }

        if (!boxed)
        {
            return false;
        }

        Change(() =>
        {
            selection.TypeText("\r");
            int start = Document.Selection.StartPosition;
            AddBox(start);
            Document.Selection.SetRange(start + 2, start + 2);
            var typing = Document.Selection.CharacterFormat.GetClone();
            typing.Strikethrough = FormatEffect.Off;
            Document.Selection.CharacterFormat = typing;
        });
        return true;
    }

    /// <summary>Backspace just after a box removes it; at the start of a list item, it leaves the list.</summary>
    private bool Unmark()
    {
        var selection = Document.Selection;
        if (selection.Length != 0)
        {
            return false;
        }

        int start = Paragraph(selection.StartPosition).Start;
        int offset = selection.StartPosition - start;
        if (offset == 2 && HasBox(start))
        {
            Change(() => RemoveBox(start));
            return true;
        }

        if (offset == 0 && ListAt(start) is NoteList.Bullets or NoteList.Numbers)
        {
            ToggleList(ListAt(start));
            return true;
        }

        return false;
    }

    /// <summary>Markdown habits, typed at the start of a paragraph and followed by a space.</summary>
    private bool AutoFormat()
    {
        var selection = Document.Selection;
        if (selection.Length != 0)
        {
            return false;
        }

        int start = Paragraph(selection.StartPosition).Start;
        int caret = selection.StartPosition;
        if (caret - start is < 1 or > 3)
        {
            return false;
        }

        Document.GetRange(start, caret).GetText(TextGetOptions.None, out string typed);
        Action? apply = typed switch
        {
            "-" or "*" or "•" => () => ToggleList(NoteList.Bullets),
            "1." or "1)" => () => ToggleList(NoteList.Numbers),
            "[]" or "[ ]" => () => ToggleList(NoteList.Checks),
            "#" => () => ApplyStyle(NoteStyle.Title),
            "##" => () => ApplyStyle(NoteStyle.Heading),
            "###" => () => ApplyStyle(NoteStyle.Subheading),
            _ => null,
        };
        if (apply is null)
        {
            return false;
        }

        Change(() =>
        {
            Document.GetRange(start, caret).SetText(TextSetOptions.None, "");
            Document.Selection.SetRange(start, start);
            apply();
        });
        return true;
    }

    // ── Pointer: ticking a box ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A click on a box ticks it: on the press, once RichEdit has put the caret there (it keeps the pointer captured,
    /// so the release does not reliably reach us). A double click selects a word instead, and ticks nothing more.
    /// </summary>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.KeyModifiers != VirtualKeyModifiers.None)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            var selection = Document.Selection;
            if (selection.Length != 0)
            {
                return;
            }

            int start = Paragraph(selection.StartPosition).Start;
            if (selection.StartPosition - start <= 1 && HasBox(start))
            {
                Tick(start);
                Document.Selection.SetRange(start + 2, start + 2);
            }
        });
    }

    // ── Pointer: a button over the box under it ─────────────────────────────────────────────────────────────
    //
    // RichEdit sets its own text cursor whatever ours, so a transparent button (with the hand) goes over the box the
    // mouse comes to, on a layer above the editor; a press on it ticks the box. Only while the mouse moves.

    /// <summary>The layer above the editor where the button over a box goes (the hand, and a click ticks it).</summary>
    public void UseBoxLayer(Canvas layer)
    {
        _layer = layer;
        _boxButton = new BoxButton();
        _boxButton.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (e.GetCurrentPoint(_boxButton).Properties.IsLeftButtonPressed && _hotBox >= 0 && HasBox(_hotBox))
            {
                Tick(_hotBox);
            }
        };
        _boxButton.PointerExited += (_, _) => HideBoxButton();
        _boxButton.PointerWheelChanged += (_, _) => HideBoxButton(); // The next notch of the wheel scrolls the text.
        layer.Children.Add(_boxButton);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_boxButton is null || _layer is null)
        {
            return;
        }

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && BoxUnder(e) is { } hit)
        {
            _hotBox = hit.Start;
            var corner = hit.Origin.TransformToVisual(_layer).TransformPoint(new(hit.Box.X, hit.Box.Y));
            Canvas.SetLeft(_boxButton, corner.X);
            Canvas.SetTop(_boxButton, corner.Y);
            _boxButton.Width = hit.Box.Width;
            _boxButton.Height = hit.Box.Height;
            _boxButton.Visibility = Visibility.Visible;
        }
        else
        {
            HideBoxButton();
        }
    }

    private void HideBoxButton()
    {
        if (_boxButton is not null)
        {
            _boxButton.Visibility = Visibility.Collapsed;
        }

        _hotBox = -1;
    }

    /// <summary>The box under the pointer: where its paragraph starts, and its glyph (a little wider) on <c>Origin</c>.</summary>
    private (int Start, UIElement Origin, Windows.Foundation.Rect Box)? BoxUnder(PointerRoutedEventArgs e)
    {
        if (ClientSpace() is not { } space)
        {
            return null;
        }

        var point = e.GetCurrentPoint(space.Origin).Position;
        var range = Document.GetRangeFromPoint(new(point.X * space.Scale, point.Y * space.Scale), PointOptions.ClientCoordinates);
        int start = Paragraph(range.StartPosition).Start;
        if (range.StartPosition - start > 1 || !HasBox(start))
        {
            return null;
        }

        Document.GetRange(start, start + 1).GetRect(PointOptions.ClientCoordinates, out var glyph, out _);
        const double Slack = 3;
        var box = new Windows.Foundation.Rect(
            glyph.X / space.Scale - Slack,
            glyph.Y / space.Scale - Slack,
            glyph.Width / space.Scale + 2 * Slack,
            glyph.Height / space.Scale + 2 * Slack);
        return box.Contains(point) ? (start, space.Origin, box) : null;
    }

    /// <summary>
    /// What RichEdit's "client" coordinates are measured from (its text view, or the whole editor with its padding),
    /// and whether in pixels: read off a box, which sits flush with the text's left edge and is one em wide.
    /// </summary>
    private (UIElement Origin, double Scale)? ClientSpace()
    {
        if (_clientSpace is { } known)
        {
            return known;
        }

        if ((GetTemplateChild("ContentElement") as ScrollViewer)?.Content is not UIElement view || XamlRoot is null)
        {
            return null;
        }

        Document.GetText(TextGetOptions.None, out string all);
        int start = ParagraphStarts(0, all.Length).FirstOrDefault(HasBox, -1);
        if (start < 0)
        {
            return null;
        }

        var box = Document.GetRange(start, start + 1);
        box.GetRect(PointOptions.ClientCoordinates, out var glyph, out _);
        double em = box.CharacterFormat.Size * 96 / 72;
        double raster = XamlRoot.RasterizationScale;
        double scale = em > 0 && Math.Abs(glyph.Width / em - raster) < Math.Abs(glyph.Width / em - 1) ? raster : 1;
        double viewLeft = view.TransformToVisual(this).TransformPoint(default).X;
        double left = glyph.X / scale;
        UIElement origin = Math.Abs(left - viewLeft) < Math.Abs(left) ? this : view;
        _clientSpace = (origin, scale);
        return _clientSpace;
    }

    /// <summary>The transparent button over a box: it has the hand.</summary>
    private sealed partial class BoxButton : Grid
    {
        public BoxButton()
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
            Visibility = Visibility.Collapsed;

            // A click here is the box's, not the notch's (which would fold away).
            Tapped += (_, e) => e.Handled = true;
            DoubleTapped += (_, e) => e.Handled = true;
        }
    }

    // ── Clipboard ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Pasting takes the text only, in the formatting where it lands.</summary>
    private void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        PasteText();
    }

    private async void PasteText()
    {
        string text;
        try
        {
            var content = WinClipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            text = await content.GetTextAsync();
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Notes] Nothing pasted: {ex.Message}");
            return;
        }

        var selection = Document.Selection;
        Change(() =>
        {
            selection.SetText(TextSetOptions.None, text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r'));
            selection.Collapse(false);
        });
        selection.ScrollIntoView(PointOptions.None);
    }

    /// <summary>A right click: the usual four.</summary>
    private MenuFlyout CreateMenu()
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        Add(Loc.T("Couper"), "", () => Document.Selection.Cut());
        Add(Loc.T("Copier"), "", () => Document.Selection.Copy());
        Add(Loc.T("Coller"), "", PasteText);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(Loc.T("Tout sélectionner"), "", () => Document.Selection.Expand(TextRangeUnit.Story));
        return menu;

        void Add(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One step of undo, then the toolbar and the note are told.</summary>
    internal void Change(Action change)
    {
        Document.BeginUndoGroup();
        try
        {
            change();
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Notes] Not formatted: {ex.Message}");
        }
        finally
        {
            Document.EndUndoGroup();
        }

        if (_loading == 0)
        {
            Edited?.Invoke(this, EventArgs.Empty);
        }

        FormatChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Format(Action<ITextCharacterFormat> change) => Change(() =>
    {
        var format = Document.Selection.CharacterFormat.GetClone();
        change(format);
        Document.Selection.CharacterFormat = format;
    });

    /// <summary>What the next character typed looks like: plain text.</summary>
    private void ResetTyping()
    {
        var format = Document.Selection.CharacterFormat.GetClone();
        format.Name = TextFont;
        format.Size = BodySize;
        format.Bold = FormatEffect.Off;
        format.Weight = 400;
        Document.Selection.CharacterFormat = format;
    }

    /// <summary>The paragraph holding <paramref name="position"/>: where it starts and ends (before its mark), its text.</summary>
    internal (ITextRange Range, int Start, int End, string Text) Paragraph(int position)
    {
        var range = Document.GetRange(position, position);
        range.Expand(TextRangeUnit.Paragraph);
        range.GetText(TextGetOptions.None, out string text);
        text = text.TrimEnd('\r', '\n');
        return (range, range.StartPosition, range.StartPosition + text.Length, text);
    }

    /// <summary>Where each paragraph touched by <c>[from, to]</c> starts.</summary>
    private List<int> ParagraphStarts(int from, int to)
    {
        var starts = new List<int>();
        Document.GetText(TextGetOptions.None, out string all);
        int position = from;
        while (true)
        {
            var range = Document.GetRange(position, position);
            range.Expand(TextRangeUnit.Paragraph);
            starts.Add(range.StartPosition);
            if (range.EndPosition <= position || range.EndPosition >= to || starts.Count > 10000)
            {
                break;
            }

            position = range.EndPosition;
            if (position >= all.Length)
            {
                break;
            }
        }

        return starts;
    }

    internal void ImportBox(int start, bool done)
    {
        AddBox(start);
        if (done)
        {
            Tick(start);
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
