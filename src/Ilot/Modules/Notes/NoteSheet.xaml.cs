using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Ilot.Composition;
using Ilot.Controls;
using Ilot.Localization;
using Ilot.Modules.Notes.Editor;
using Ilot.Windowing;

namespace Ilot.Modules.Notes;

/// <summary>Where a note's sheet is (see <see cref="NoteSheet.Export"/>): the note, and the caret (or the selection) in its text.</summary>
public sealed record NoteState(Note Note, int SelectionStart, int SelectionEnd);

/// <summary>
/// A note open: its title, its text with a toolbar the way Word has it, its counts; « Copier », « Supprimer ». Each sheet
/// has its own note: the one in the bubble under the notch (see NotesView), the one detached in a window of its own (see
/// DetachedWindow): « Détacher », « Rattacher ». What is typed is taken a moment after the last keystroke, and saved.
/// </summary>
public sealed partial class NoteSheet : UserControl, IDetachableSheet
{
    private const string CopyGlyph = "\uE8C8";
    private const string DoneGlyph = "\uE73E";
    private const string WarningGlyph = "\uE7BA";
    private static readonly TimeSpan ConfirmTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CopiedTime = TimeSpan.FromSeconds(1.6);
    private static readonly TimeSpan CaptureDelay = TimeSpan.FromMilliseconds(250);

    private readonly Compositor _compositor;
    private readonly SpringChannel _saved;
    private readonly DispatcherQueueTimer _deleteTimer;
    private readonly DispatcherQueueTimer _copyTimer;
    private readonly DispatcherQueueTimer _captureTimer;
    private readonly Brush _chosenBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private readonly Brush _clearBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private Note? _note;
    private string? _baseline;
    private (int Start, int End)? _caret; // Where the caret goes once the text is in (a note detached, or brought back).
    private bool _showing;
    private bool _confirmingDelete;

    /// <param name="detached">In a window of its own: « Rattacher » rather than « Détacher ».</param>
    internal NoteSheet(NotesViewModel viewModel, bool detached)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.

        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(Header, true);
        ElementCompositionPreview.SetIsTranslationEnabled(EditorArea, true);

        // "Enregistré" comes in softly once a change is written, and goes as soon as the text changes again.
        var state = _compositor.CreatePropertySet();
        _saved = new SpringChannel(state, "Saved", 0);
        new CompositionExpression(_compositor, "Clamp(s.Saved, 0, 1)").With("s", state)
            .Start(ElementCompositionPreview.GetElementVisual(SavedBadge), "Opacity");

        _deleteTimer = CreateTimer(ConfirmTime, ResetDelete);
        _copyTimer = CreateTimer(CopiedTime, () =>
        {
            CopyIcon.Glyph = CopyGlyph;
            CopyText.Text = Loc.T("Copier");
        });
        _captureTimer = CreateTimer(CaptureDelay, Capture);

        TitleBox.GotFocus += (_, _) => KeyboardWanted?.Invoke(this, TitleBox);
        TitleBox.TextChanged += OnTitleChanged;
        TitleBox.KeyDown += OnTitleKeyDown;
        Editor.GotFocus += (_, _) => KeyboardWanted?.Invoke(this, Editor);
        Editor.Edited += OnEdited;
        Editor.FormatChanged += (_, _) => ShowFormat();
        Editor.Escaped += (_, _) => KeyboardDone?.Invoke(this, EventArgs.Empty);
        Editor.UseBoxLayer(BoxLayer);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressedAnywhere), handledEventsToo: true);

        string move = Loc.T(detached ? "Rattacher à la notch" : "Détacher dans une fenêtre");
        MoveIcon.Glyph = detached ? "\uE73F" : "\uE8A7";
        ToolTipService.SetToolTip(MoveButton, move);
        AutomationProperties.SetName(MoveButton, move);

        viewModel.PropertyChanged += OnViewModelChanged;
    }

    public NotesViewModel ViewModel { get; }

    /// <summary>The note open; <c>null</c>: none (the sheet is put away).</summary>
    public Note? Note => _note;

    /// <summary>A note opened, or none any more (closed, deleted): the host shows the sheet, or puts it away.</summary>
    public event EventHandler? NoteChanged;

    /// <summary>The note's title changed (a window's title follows it).</summary>
    public event EventHandler? TitleChanged;

    /// <summary>« Fermer la note ».</summary>
    public event EventHandler? CloseRequested;

    /// <summary>« Détacher » in the notch, « Rattacher » in its window.</summary>
    public event EventHandler? MoveRequested;

    /// <summary>A field was clicked: the notch must take the keyboard from the app in use (in a window, it has it).</summary>
    public event EventHandler<Control>? KeyboardWanted;

    /// <summary>Échap: done typing.</summary>
    public event EventHandler? KeyboardDone;

    /// <summary>
    /// <paramref name="note"/> in the sheet (the one before is left: saved, or forgotten if nothing was written in it);
    /// the caret where <paramref name="caret"/> says, if it says.
    /// </summary>
    public void Open(Note note, (int Start, int End)? caret = null)
    {
        if (note == _note)
        {
            return;
        }

        bool switching = _note is not null;
        Capture();
        if (_note is { } before)
        {
            ViewModel.Leave(before);
        }

        _note = note;
        _caret = caret;
        ResetDelete();
        _saved.AnimateTo(0, Motion.Fade);
        NoteChanged?.Invoke(this, EventArgs.Empty); // The host shows the sheet: the editor is laid out there.

        // Not before the editor is on screen: RichEdit would not find the boxes' icon font yet (empty squares).
        Editor.Loaded -= FillWhenLoaded;
        if (Editor.IsLoaded)
        {
            Fill(note);
        }
        else
        {
            _baseline = null; // Nothing to capture meanwhile.
            Editor.Loaded += FillWhenLoaded;
        }

        if (switching)
        {
            Rise(Header, 0);
            Rise(EditorArea, 40);
        }
    }

    /// <summary>A blank note (the one open, if it is blank already), its title ready to type.</summary>
    public void New()
    {
        if (_note is not { IsEmpty: true })
        {
            Open(ViewModel.Create());
        }

        TitleBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// The sheet put away: its note left (saved, or forgotten if blank); or, <paramref name="leave"/> false, only written,
    /// as it goes on elsewhere (detached, or brought back).
    /// </summary>
    public void Close(bool leave = true)
    {
        if (_note is not { } note)
        {
            return;
        }

        Capture();
        ResetDelete();
        Editor.Loaded -= FillWhenLoaded;
        _note = null;
        _baseline = null;
        _caret = null;
        if (leave)
        {
            ViewModel.Leave(note);
        }
        else
        {
            ViewModel.Flush();
        }

        NoteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where the sheet is, to go on elsewhere (see <see cref="Import"/>); <c>null</c> with no note open.</summary>
    public NoteState? Export()
    {
        if (_note is not { } note)
        {
            return null;
        }

        Capture();
        if (_caret is { } caret)
        {
            return new NoteState(note, caret.Start, caret.End); // Not filled in yet.
        }

        var selection = Editor.Document.Selection;
        return new NoteState(note, selection.StartPosition, selection.EndPosition);
    }

    /// <summary>The note goes on from <paramref name="state"/>: the caret where it was.</summary>
    public void Import(NoteState state) => Open(state.Note, (state.SelectionStart, state.SelectionEnd));

    /// <summary>Done typing: what was typed is taken and written, and the caret goes away.</summary>
    public void StopTyping()
    {
        Capture();
        ViewModel.Flush();
        if (Editor.FocusState != FocusState.Unfocused || TitleBox.FocusState != FocusState.Unfocused)
        {
            Focus(FocusState.Programmatic);
        }
    }

    /// <summary>For good (its window closed): its note left, and the notes no longer followed.</summary>
    public void Release()
    {
        Close();
        ViewModel.PropertyChanged -= OnViewModelChanged;
    }

    public void AddWindowTools(UIElement pin, UIElement minimize)
    {
        Actions.Children.Insert(Actions.Children.IndexOf(MoveButton), pin);
        Actions.Children.Insert(Actions.Children.IndexOf(CloseButton), minimize);
    }

    /// <summary>The caret back in the text, unless a field has it already (the title…).</summary>
    public void FocusContent()
    {
        if (_note is not null && XamlRoot is { } root && FocusManager.GetFocusedElement(root) is not (TextBox or RichEditBox))
        {
            Editor.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Nothing it paints reaches the corners.</summary>
    public void RoundCorners(double radius)
    {
    }

    private void OnMoveClick(object sender, RoutedEventArgs e) => MoveRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotesViewModel.SaveText) && _note is not null)
        {
            ShowSaved();
        }
    }

    // ── The note in the fields ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The note into the fields: as it was opened, nothing to save yet; the caret where it was, if known.</summary>
    private void Fill(Note note)
    {
        _showing = true;
        try
        {
            TitleBox.Text = note.Title;
            Editor.Load(note.Content);
            _baseline = Editor.Rtf;
        }
        finally
        {
            _showing = false;
        }

        UpdateHints();
        ShowFormat();
        ShowCount();
        if (_caret is { } caret)
        {
            _caret = null;
            Editor.Document.Selection.SetRange(caret.Start, caret.End);
            Editor.Document.Selection.ScrollIntoView(Microsoft.UI.Text.PointOptions.None);
        }
    }

    private void FillWhenLoaded(object sender, RoutedEventArgs e)
    {
        Editor.Loaded -= FillWhenLoaded;
        if (_note is { } note)
        {
            Fill(note);
        }
    }

    private void Rise(UIElement element, int delay)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1));
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = TimeSpan.FromMilliseconds(260);
        fade.DelayTime = TimeSpan.FromMilliseconds(delay);
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        var slide = _compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, new Vector3(0, 8, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, ease);
        slide.Duration = TimeSpan.FromMilliseconds(380);
        slide.DelayTime = fade.DelayTime;
        slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation", slide);
    }

    private void ShowSaved()
    {
        string text = ViewModel.SaveText;
        if (text.Length > 0)
        {
            SavedText.Text = text;
            SavedIcon.Glyph = ViewModel.SaveFailed ? WarningGlyph : DoneGlyph;
            var brush = (Brush)Application.Current.Resources[ViewModel.SaveFailed ? "NotchDangerBrush" : "NotchSecondaryTextBrush"];
            SavedIcon.Foreground = brush;
            SavedText.Foreground = brush;
        }

        _saved.AnimateTo(text.Length > 0 ? 1 : 0, Motion.Fade);
    }

    private void ShowCount() => CountText.Text = NotesViewModel.CountText(_note?.Plain ?? "");

    private void UpdateHints()
    {
        TitleHint.Visibility = TitleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EditorHint.Visibility = Editor.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Typing ──────────────────────────────────────────────────────────────────────────────────────────────

    private void OnTitleChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHints();
        if (!_showing && _note is { } note)
        {
            ViewModel.SetTitle(note, TitleBox.Text);
            TitleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The text changed: it is taken in a moment after the last keystroke (reading it formatted costs a little).</summary>
    private void OnEdited(object? sender, EventArgs e)
    {
        UpdateHints();
        _captureTimer.Stop();
        _captureTimer.Start();
    }

    /// <summary>The text as it is now into the note, unless it is just as it was opened.</summary>
    private void Capture()
    {
        _captureTimer.Stop();
        if (_note is not { } note || _showing || _baseline is null)
        {
            return;
        }

        string rtf = Editor.Rtf;
        if (rtf != _baseline)
        {
            _baseline = rtf;
            ViewModel.SetContent(note, rtf, Editor.Plain);
            ShowCount();
        }
    }

    /// <summary>Enter (or ↓) goes on to the text; Escape is done.</summary>
    private void OnTitleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Down:
                e.Handled = true;
                Editor.Focus(FocusState.Programmatic);
                break;

            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                KeyboardDone?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    // ── Toolbar ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The toolbar shows what is under the caret: its style, size, emphasis and list.</summary>
    private void ShowFormat()
    {
        if (_note is null)
        {
            return;
        }

        var format = Editor.ReadFormat();
        Choose(BodyStyleButton, format.Style == NoteStyle.Body);
        Choose(TitleStyleButton, format.Style == NoteStyle.Title);
        Choose(HeadingStyleButton, format.Style == NoteStyle.Heading);
        Choose(SubheadingStyleButton, format.Style == NoteStyle.Subheading);
        Choose(BoldButton, format.Bold);
        Choose(ItalicButton, format.Italic);
        Choose(UnderlineButton, format.Underline);
        Choose(StrikeButton, format.Strike);
        Choose(BulletsButton, format.List == NoteList.Bullets);
        Choose(NumbersButton, format.List == NoteList.Numbers);
        Choose(ChecksButton, format.List == NoteList.Checks);
        SizeText.Text = format.Size is { } size ? size.ToString("0.#", Loc.Culture) : "–";

        void Choose(MotionButton button, bool chosen) => button.Background = chosen ? _chosenBrush : _clearBrush;
    }

    private void OnStyleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<NoteStyle>(tag, out var style))
        {
            Editor.ApplyStyle(style);
        }
    }

    private void OnSizeDownClick(object sender, RoutedEventArgs e) => Editor.StepSize(-1);

    private void OnSizeUpClick(object sender, RoutedEventArgs e) => Editor.StepSize(1);

    private void OnBoldClick(object sender, RoutedEventArgs e) => Editor.ToggleBold();

    private void OnItalicClick(object sender, RoutedEventArgs e) => Editor.ToggleItalic();

    private void OnUnderlineClick(object sender, RoutedEventArgs e) => Editor.ToggleUnderline();

    private void OnStrikeClick(object sender, RoutedEventArgs e) => Editor.ToggleStrike();

    private void OnBulletsClick(object sender, RoutedEventArgs e) => Editor.ToggleList(NoteList.Bullets);

    private void OnNumbersClick(object sender, RoutedEventArgs e) => Editor.ToggleList(NoteList.Numbers);

    private void OnChecksClick(object sender, RoutedEventArgs e) => Editor.ToggleList(NoteList.Checks);

    private void OnUndoClick(object sender, RoutedEventArgs e) => Editor.Document.Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => Editor.Document.Redo();

    // ── Actions ─────────────────────────────────────────────────────────────────────────────────────────────

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_note is not { } note)
        {
            return;
        }

        Capture();
        if (await ViewModel.CopyAsync(note, Editor.Rtf, Editor.Plain))
        {
            CopyIcon.Glyph = DoneGlyph;
            CopyText.Text = Loc.T("Copié");
            _copyTimer.Stop();
            _copyTimer.Start();
        }
    }

    /// <summary>The first click asks (the pill turns red for a moment), the second deletes. A blank note just goes.</summary>
    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        Capture();
        if (_note is not { } note)
        {
            return;
        }

        if (_confirmingDelete || note.IsEmpty)
        {
            ResetDelete();
            KeyboardDone?.Invoke(this, EventArgs.Empty);
            Editor.Loaded -= FillWhenLoaded;
            _note = null;
            _baseline = null;
            _caret = null;
            ViewModel.Delete(note);
            NoteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _confirmingDelete = true;
        DeleteText.Text = Loc.T("Confirmer");
        DeleteButton.Background = (Brush)Application.Current.Resources["NotchDangerBrush"];
        _deleteTimer.Stop();
        _deleteTimer.Start();
    }

    private void ResetDelete()
    {
        _deleteTimer.Stop();
        if (_confirmingDelete)
        {
            _confirmingDelete = false;
            DeleteText.Text = Loc.T("Supprimer");
            DeleteButton.Background = (Brush)Application.Current.Resources["NotchPillBrush"];
        }
    }

    /// <summary>A click anywhere but on "Confirmer" gives up a delete waiting for its confirmation.</summary>
    private void OnPointerPressedAnywhere(object sender, PointerRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element == DeleteButton)
            {
                return;
            }
        }

        ResetDelete();
    }

    private DispatcherQueueTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }
}
