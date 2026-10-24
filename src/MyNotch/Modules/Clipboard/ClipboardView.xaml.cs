using System.Collections.Specialized;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using MyNotch.Composition;
using MyNotch.Controls;
using MyNotch.Notch;
using MyNotch.Localization;

namespace MyNotch.Modules.Clipboard;

/// <summary>
/// Clipboard tab: one tile per entry, kept in step with the history. Tiles glide aside when one arrives, moves or
/// leaves, and pop in when new: implicit animations, played by the compositor.
/// </summary>
public sealed partial class ClipboardView : UserControl
{
    private const float TileCenterX = 70;
    private const float TileCenterY = 48;

    private readonly NotchContext _context;
    private readonly ImplicitAnimationCollection _reflow;
    private readonly CompositionAnimationGroup _show;
    private readonly CompositionAnimationGroup _hide;
    private readonly RowScroller _row;
    private SpringChannel? _confirm;
    private bool _confirming;
    private bool _pasting;
    private ClipboardCard? _selection;
    private NotchBubble? _preview;
    private IDisposable? _previewKeyboard;
    private string[] _words = [];

    public ClipboardView(ClipboardViewModel viewModel, ClipboardShortcut shortcut, NotchContext context)
    {
        ViewModel = viewModel;
        _context = context;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        shortcut.Pressed += (_, _) => OnShortcut();

        // Below the tab bar: the tiles' tops line up with the media artwork's.
        Scroller.Margin = new Thickness(0, context.ContentTop + 2, 0, 0);
        EmptyState.Margin = new Thickness(0, context.ContentTop + 22, 0, 0);
        NoResults.Margin = EmptyState.Margin;

        var compositor = context.Motion.Compositor;
        _reflow = ImplicitMotion.Reflow(compositor, Motion.Reflow);
        _show = ImplicitMotion.Show(compositor, Motion.Pop, fromScale: 0.82f);
        _hide = ImplicitMotion.Hide(compositor, toScale: 0.86f);

        foreach (var entry in viewModel.Entries)
        {
            Cards.Children.Add(CreateCard(entry));
        }

        ((INotifyCollectionChanged)viewModel.Entries).CollectionChanged += OnEntriesChanged;
        viewModel.Recorded += OnRecorded;

        // Opens with the notch, after the tab bar; slides and fades with the tab selection.
        ElementCompositionPreview.SetIsTranslationEnabled(LayoutRoot, true);
        var root = ElementCompositionPreview.GetElementVisual(LayoutRoot);
        context.Bind($"Clamp((n.Expansion - 0.45) * 2.4, 0, 1) * {NotchContext.TabOpacity}").Start(root, "Opacity");
        context.Bind($"Vector3({NotchContext.TabOffset}, (1 - n.Progress) * -8, 0)").Start(root, "Translation");

        context.ExpandedChanged += (_, _) => RefreshWhenVisible();
        context.SelectionChanged += (_, _) => RefreshWhenVisible();

        // The wheel scrolls the row sideways; touchpads pan it natively.
        _row = new RowScroller(Scroller, LayoutRoot); // Arrows at its ends, and the wheel, for a mouse.

        BindConfirmation(compositor);
        BindSearch();
    }

    public ClipboardViewModel ViewModel { get; }

    // ── Search ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The search field (see <see cref="NotchSearchBox"/>): typing narrows the tiles, Enter copies the chosen one (the
    /// first by default) and gives the keyboard back, ready for Ctrl+V; the arrows choose another.
    /// </summary>
    private void BindSearch()
    {
        Search.Attach(_context);
        Search.QueryChanged += (_, _) =>
        {
            ApplyFilter();
            _row.ScrollToStart();
        };
        Search.Submitted += async (_, _) =>
        {
            if ((_selection?.Visibility == Visibility.Visible ? _selection : VisibleCards().FirstOrDefault()) is { } chosen)
            {
                await chosen.CopyAsync(); // Gives the keyboard back once copied (see OnPicked).
            }
        };
        Search.Navigated += (_, step) => MoveSelection(step);
        Search.Peeked += (_, _) =>
        {
            if ((_selection?.Visibility == Visibility.Visible ? _selection : VisibleCards().FirstOrDefault()) is { } chosen)
            {
                Preview(chosen.Entry);
            }
        };
        Search.StoppedTyping += (_, _) =>
        {
            _pasting = false;
            Select(null);
        };

        PlaceSearch();
        ViewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ClipboardViewModel.HasEntries) when !ViewModel.HasEntries:
                    Search.Reset();
                    break;

                case nameof(ClipboardViewModel.CanClear):
                    PlaceSearch();
                    break;
            }
        };
    }

    /// <summary>Right next to the clear button, or at the end of the row when there is nothing to clear.</summary>
    private void PlaceSearch() =>
        Search.Margin = new Thickness(0, 1, ViewModel.CanClear ? ClearButton.Margin.Right + ClearButton.Width + 4 : ClearButton.Margin.Right, 0);

    /// <summary>
    /// The shortcut (Windows + V by default), the way Windows does it: the notch opens on the clipboard, keyboard
    /// ready, the latest copy chosen; the arrows choose another, typing narrows the list, Enter (or a click) pastes
    /// into the app in use, Escape (or the shortcut again) closes.
    /// </summary>
    private void OnShortcut()
    {
        if (_pasting && _context.IsVisible)
        {
            Search.Reset();
            return;
        }

        _pasting = true;
        _context.Show();
        Search.Open();
        Select(VisibleCards().FirstOrDefault());
    }

    private IEnumerable<ClipboardCard> VisibleCards() =>
        Cards.Children.OfType<ClipboardCard>().Where(card => card.Visibility == Visibility.Visible);

    private void Select(ClipboardCard? card)
    {
        if (card == _selection)
        {
            return;
        }

        _selection?.SetSelected(false);
        _selection = card;
        card?.SetSelected(true);
    }

    private void MoveSelection(int step)
    {
        var cards = VisibleCards().ToList();
        if (cards.Count > 0)
        {
            int index = _selection is null ? -1 : cards.IndexOf(_selection);
            Select(cards[Math.Clamp(index + step, 0, cards.Count - 1)]);
        }
    }

    /// <summary>A tile was copied (Enter, or a click): back to the app in use, and pasted there after the shortcut.</summary>
    private void OnPicked()
    {
        bool paste = _pasting;
        Search.ReturnKeyboard();
        if (paste)
        {
            _ = SystemClipboard.PasteAsync();
        }
    }

    /// <summary>Only the tiles that match stay; the others leave and the row closes up behind them.</summary>
    private void ApplyFilter()
    {
        _words = TextSearch.Words(Search.Text);
        int shown = 0;
        foreach (var card in Cards.Children.OfType<ClipboardCard>())
        {
            shown += Filter(card) ? 1 : 0;
        }

        bool nothing = _words.Length > 0 && shown == 0 && ViewModel.HasEntries;
        NoResults.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;
        if (nothing)
        {
            NoResultsText.Text = Loc.F("Rien ne correspond à « {0} ».", Search.Text.Trim());
        }

        // Choosing with the keyboard: the first match is the one Enter takes.
        if (Search.HasKeyboard && (_pasting || _selection is not null))
        {
            Select(VisibleCards().FirstOrDefault());
        }
    }

    private bool Filter(ClipboardCard card)
    {
        bool match = _words.Length == 0 || ClipboardSearch.Matches(card.Entry, _words);
        card.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
        card.Highlight(_words);
        return match;
    }

    // ── Preview ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A copy, larger, in a drop under the notch, to touch up and copy again (see <see cref="ClipboardPreview"/>): its
    /// eye, or Space in the search (Windows + V, then Space: the latest copy). It has the keyboard meanwhile, so the notch
    /// stays open; closed, the keyboard goes back to the app in use.
    /// </summary>
    private void Preview(ClipboardEntry entry)
    {
        ClosePreview();
        var preview = new ClipboardPreview(entry, ViewModel);
        var bubble = _context.ShowBubble(preview, ClipboardPreview.SizeFor(entry), ClipboardPreview.Minimum);
        _preview = bubble;
        preview.CloseRequested += (_, _) =>
        {
            if (_preview == bubble)
            {
                ClosePreview();
            }
        };
        preview.SizeWanted += (_, size) => bubble.Resize(size);
        _previewKeyboard = _context.TakeKeyboard(lost: () =>
        {
            // Another app was clicked: it has the keyboard, the preview goes.
            _previewKeyboard = null;
            ClosePreview();
        });
        DispatcherQueue.TryEnqueue(preview.TakeFocus);
    }

    private void ClosePreview()
    {
        var bubble = _preview;
        var keyboard = _previewKeyboard;
        _preview = null;
        _previewKeyboard = null;
        bubble?.Dispose();
        keyboard?.Dispose();
    }

    // ── Clearing ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asking before clearing: the row of tiles steps back and fades while the question rises into its place
    /// (<c>c.Confirm</c>, a spring on the compositor).
    /// </summary>
    private void BindConfirmation(Compositor compositor)
    {
        var state = compositor.CreatePropertySet();
        _confirm = new SpringChannel(state, "Confirm", 0);
        ConfirmPanel.Margin = new Thickness(0, _context.ContentTop + 20, 0, 0);

        var row = ElementCompositionPreview.GetElementVisual(Scroller);
        Scroller.SizeChanged += (_, e) => row.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        Bind("1 - Clamp(c.Confirm, 0, 1)").Start(row, "Opacity");
        Bind("Vector3(Lerp(1, 0.94, c.Confirm), Lerp(1, 0.94, c.Confirm), 1)").Start(row, "Scale");

        ElementCompositionPreview.SetIsTranslationEnabled(ConfirmPanel, true);
        var panel = ElementCompositionPreview.GetElementVisual(ConfirmPanel);
        Bind("Clamp(c.Confirm, 0, 1)").Start(panel, "Opacity");
        Bind("Vector3(0, (1 - c.Confirm) * 10, 0)").Start(panel, "Translation");
        Bind("Clamp(1 - c.Confirm, 0, 1)").Start(ElementCompositionPreview.GetElementVisual(ClearButton), "Opacity");

        // Nothing left to clear, or the tab is out of sight: the question goes away.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClipboardViewModel.CanClear) && !ViewModel.CanClear)
            {
                AskToClear(false);
            }
        };

        CompositionExpression Bind(string expression) => new CompositionExpression(compositor, expression).With("c", state);
    }

    private void AskToClear(bool ask, bool animate = true)
    {
        if (ask == _confirming)
        {
            return;
        }

        _confirming = ask;
        Scroller.IsHitTestVisible = !ask;
        ClearButton.IsHitTestVisible = !ask;
        ConfirmPanel.IsHitTestVisible = ask;
        if (animate)
        {
            _confirm!.AnimateTo(ask ? 1 : 0, Motion.Toggle);
        }
        else
        {
            _confirm!.Snap(ask ? 1 : 0);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => AskToClear(true);

    private void OnCancelClearClick(object sender, RoutedEventArgs e) => AskToClear(false);

    /// <summary>The tiles leave one after the other as the row comes back, with only the pinned ones.</summary>
    private void OnConfirmClearClick(object sender, RoutedEventArgs e)
    {
        ViewModel.Clear();
        AskToClear(false);
    }

    // ── Tiles ───────────────────────────────────────────────────────────────────────────────────────────────

    private ClipboardCard CreateCard(ClipboardEntry entry)
    {
        var card = new ClipboardCard(entry, ViewModel);
        var visual = ElementCompositionPreview.GetElementVisual(card);
        visual.CenterPoint = new Vector3(TileCenterX, TileCenterY, 0);
        ElementCompositionPreview.SetImplicitShowAnimation(card, _show);
        ElementCompositionPreview.SetImplicitHideAnimation(card, _hide);

        // Glide on layout changes only once placed: attached any sooner, a new tile would fly in from the corner.
        card.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => visual.ImplicitAnimations = _reflow);

        // Copied from a search: back to the app, ready to paste.
        card.Copied += (_, _) => OnPicked();
        card.PreviewRequested += (_, _) => Preview(card.Entry);
        if (_words.Length > 0)
        {
            Filter(card);
        }

        return card;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                for (int i = 0; i < e.NewItems!.Count; i++)
                {
                    Cards.Children.Insert(e.NewStartingIndex + i, CreateCard((ClipboardEntry)e.NewItems[i]!));
                }

                break;

            case NotifyCollectionChangedAction.Remove:
                for (int i = 0; i < e.OldItems!.Count; i++)
                {
                    Cards.Children.RemoveAt(e.OldStartingIndex);
                }

                break;

            case NotifyCollectionChangedAction.Move:
                Cards.Children.Move((uint)e.OldStartingIndex, (uint)e.NewStartingIndex);
                break;

            default:
                Cards.Children.Clear();
                foreach (var entry in ViewModel.Entries)
                {
                    Cards.Children.Add(CreateCard(entry));
                }

                break;
        }

        if (_words.Length > 0)
        {
            ApplyFilter();
        }
    }

    /// <summary>A new copy: the closed notch announces it, and the row is back at its start for next time.</summary>
    private void OnRecorded(object? sender, ClipboardEntry entry)
    {
        if (_context.IsVisible)
        {
            return;
        }

        _row.ScrollToStart(animate: false);
        if (ViewModel.SignalsCopies && !_context.IsExpanded)
        {
            // A colour lights its icon up in that colour.
            var tint = entry.Kind == ClipboardKind.Color ? entry.Color with { A = 255 } : (Windows.UI.Color?)null;
            _context.Announce(entry.KindGlyph, entry.CopiedMessage, tint);
        }
    }

    /// <summary>"How long ago" is brought up to date when the tab comes into view, not on a timer.</summary>
    private void RefreshWhenVisible()
    {
        if (!_context.IsVisible)
        {
            // A question left unanswered is dropped: the next visit starts with the tiles. So is a preview.
            ClosePreview();
            AskToClear(false, animate: false);
            Search.Reset();
            return;
        }

        var now = DateTimeOffset.Now;
        foreach (var child in Cards.Children)
        {
            (child as ClipboardCard)?.Refresh(now);
        }
    }
}
