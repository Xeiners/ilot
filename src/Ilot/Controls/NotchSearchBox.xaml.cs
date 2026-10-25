using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Ilot.Composition;
using Ilot.Notch;
using Windows.UI;
using Ilot.Localization;

namespace Ilot.Controls;

/// <summary>
/// A search field kept out of the way, for the notch's tabs: a magnifier until clicked, then a pill that stretches
/// to the left (on the compositor).
/// </summary>
/// <remarks>
/// The notch never takes the keyboard on its own: opening the field takes it from the app in use (keeping the notch
/// open meanwhile), and gives it back when done. Escape gives up (and folds the field away); Enter and the left / right
/// arrows are left to the host (<see cref="Submitted"/>, <see cref="Navigated"/>). An empty field folds away once done
/// typing; one with a search in it stays, with its results.
/// </remarks>
public sealed partial class NotchSearchBox : UserControl
{
    private NotchContext? _context;
    private SpringChannel? _open;
    private SpringChannel? _focus;
    private IDisposable? _keyboard;

    public NotchSearchBox()
    {
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.

        // Somewhere to put the focus once done typing, so the caret goes away.
        IsTabStop = true;
        UseSystemFocusVisuals = false;
    }

    /// <summary>What it searches for, as shown to screen readers ("Rechercher dans le presse-papier"…).</summary>
    public string Label
    {
        get => AutomationProperties.GetName(SearchBox);
        set
        {
            AutomationProperties.SetName(SearchBox, value);
            AutomationProperties.SetName(SearchButton, value);
        }
    }

    public string Text => SearchBox.Text;

    public bool IsOpen { get; private set; }

    /// <summary>The field has the keyboard: the user is typing (or choosing with the arrows).</summary>
    public bool HasKeyboard => _keyboard is not null;

    /// <summary>The query changed.</summary>
    public event EventHandler? QueryChanged;

    /// <summary>Enter was pressed.</summary>
    public event EventHandler? Submitted;

    /// <summary>The left (-1) or right (+1) arrow was pressed.</summary>
    public event EventHandler<int>? Navigated;

    /// <summary>Done typing: the keyboard went back to the app in use (or the user clicked elsewhere).</summary>
    public event EventHandler? StoppedTyping;

    /// <summary>Space with nothing typed (a query never starts with one): a look at the chosen item, as Quick Look does.</summary>
    public event EventHandler? Peeked;

    /// <summary>Ties the field to the notch it lives in: the keyboard comes through it.</summary>
    public void Attach(NotchContext context)
    {
        _context = context;
        var compositor = context.Motion.Compositor;
        var state = compositor.CreatePropertySet();
        _open = new SpringChannel(state, "Open", 0);
        _focus = new SpringChannel(state, "Focus", 0);
        float width = (float)Width;
        float height = (float)Height;

        // The pill sits under the magnifier when folded, and stretches to the left as the field opens.
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2(height / 2);
        Bind("Vector2(w - Lerp(c, w, s.Open), 0)").Start(geometry, "Offset");
        Bind("Vector2(Lerp(c, w, s.Open), h)").Start(geometry, "Size");

        var fill = compositor.CreateShapeVisual();
        fill.Size = new Vector2(width, height);
        var fillShape = compositor.CreateSpriteShape(geometry);
        fillShape.FillBrush = compositor.CreateColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        fill.Shapes.Add(fillShape);

        // A hairline while typing.
        var ring = compositor.CreateShapeVisual();
        ring.Size = fill.Size;
        var ringShape = compositor.CreateSpriteShape(geometry);
        ringShape.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
        ringShape.StrokeThickness = 1;
        ring.Shapes.Add(ringShape);

        var pill = compositor.CreateContainerVisual();
        pill.Size = fill.Size;
        pill.Children.InsertAtTop(fill);
        pill.Children.InsertAtTop(ring);
        ElementCompositionPreview.SetElementChildVisual(SearchPill, pill);
        Bind("Clamp(s.Open * 1.5, 0, 1)").Start(fill, "Opacity");
        Bind("Clamp(s.Open, 0, 1) * Clamp(s.Focus, 0, 1)").Start(ring, "Opacity");

        // The text comes in once the pill has made room for it, and leaves first; never outside the pill.
        ElementCompositionPreview.SetIsTranslationEnabled(SearchInput, true);
        var input = ElementCompositionPreview.GetElementVisual(SearchInput);
        Bind("Clamp((s.Open - 0.45) * 1.8, 0, 1)").Start(input, "Opacity");
        Bind("Vector3((1 - Clamp(s.Open, 0, 1)) * 16, 0, 0)").Start(input, "Translation");
        var clip = compositor.CreateInsetClip();
        Bind("Max((w - c) * (1 - s.Open) - l - (1 - Clamp(s.Open, 0, 1)) * 16, 0)").With("l", SearchInput.Margin.Left).Start(clip, "LeftInset");
        input.Clip = clip;

        CompositionExpression Bind(string expression) => new CompositionExpression(compositor, expression)
            .With("s", state).With("w", width).With("h", height).With("c", (float)SearchButton.Width);
    }

    /// <summary>Opens the field (if needed) and takes the keyboard: ready to type.</summary>
    public void Open()
    {
        if (!IsOpen)
        {
            IsOpen = true;
            SearchInput.IsHitTestVisible = true;
            _open?.AnimateTo(1, Motion.Toggle);
            _context?.HideTabs(true); // Open, the field stretches over the tabs' row: they step aside meanwhile.
        }

        TakeKeyboard();
    }

    /// <summary>Done typing: the app that had the keyboard gets it back, and the notch may close again.</summary>
    public void ReturnKeyboard()
    {
        var keyboard = _keyboard;
        _keyboard = null;
        keyboard?.Dispose();
        StopTyping();
    }

    /// <summary>Back to a bare magnifier: keyboard returned, query cleared, field folded.</summary>
    public void Reset()
    {
        ReturnKeyboard();
        SearchBox.Text = "";
        Close();
    }

    private void TakeKeyboard()
    {
        if (_keyboard is not null || _context is null)
        {
            return;
        }

        _keyboard = _context.TakeKeyboard(lost: () =>
        {
            // The user clicked another app: it has the keyboard now, the field just lets go.
            _keyboard = null;
            StopTyping();
        });

        // Once the window has the keyboard, the caret must be in the field.
        DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
    }

    private void StopTyping()
    {
        _focus?.AnimateTo(0, Motion.Hover);
        if (SearchBox.FocusState != FocusState.Unfocused)
        {
            Focus(FocusState.Programmatic);
        }

        if (SearchBox.Text.Length == 0)
        {
            Close();
        }

        StoppedTyping?.Invoke(this, EventArgs.Empty);
    }

    private void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        SearchInput.IsHitTestVisible = false;
        _open?.AnimateTo(0, Motion.Fade);
        _context?.HideTabs(false);
    }

    /// <summary>The magnifier opens the field, or folds it away (and clears it) when it is open.</summary>
    private void OnSearchButtonClick(object sender, RoutedEventArgs e)
    {
        if (IsOpen)
        {
            Reset();
        }
        else
        {
            Open();
        }
    }

    private void OnSearchGotFocus(object sender, RoutedEventArgs e)
    {
        _focus?.AnimateTo(1, Motion.Hover);
        TakeKeyboard();
    }

    private void OnSearchLostFocus(object sender, RoutedEventArgs e) => _focus?.AnimateTo(0, Motion.Hover);

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                SearchBox.Text = "";
                ReturnKeyboard();
                break;

            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                Submitted?.Invoke(this, EventArgs.Empty);
                break;

            case Windows.System.VirtualKey.Left:
            case Windows.System.VirtualKey.Right:
                e.Handled = true;
                Navigated?.Invoke(this, e.Key == Windows.System.VirtualKey.Left ? -1 : 1);
                break;
        }
    }

    /// <summary>Space with nothing typed is not text (see <see cref="Peeked"/>), when someone listens: caught before the field.</summary>
    private void OnSearchPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space && SearchBox.Text.Length == 0 && Peeked is not null)
        {
            e.Handled = true;
            Peeked(this, EventArgs.Empty);
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        bool empty = SearchBox.Text.Length == 0;
        SearchHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        QueryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSearchClearClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        if (_keyboard is not null)
        {
            SearchBox.Focus(FocusState.Programmatic);
        }
    }
}
