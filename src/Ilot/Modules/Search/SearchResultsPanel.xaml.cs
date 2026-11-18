using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Ilot.Composition;
using Ilot.Modules.Search.Everything;
using Windows.Foundation;
using Ilot.Localization;

namespace Ilot.Modules.Search;

/// <summary>
/// The search's results, in the drop under the notch: one line each, the chosen one lit by a highlight that glides from
/// line to line, new lines sliding in one after the other, and at the bottom what the keys do and who finds the files.
/// As many lines show as the settings say; the rest scroll, the chosen line always kept in sight.
/// </summary>
public sealed partial class SearchResultsPanel : UserControl
{
    public const double PanelWidth = 660;
    public const double RowHeight = 50;
    private const double Top = 8;
    private const double Bottom = 4;
    private const double Footer = 34;

    // A new line slides in from the right, a beat after the one above it.
    private const float ArrivalShift = 14;
    private static readonly TimeSpan ArrivalStagger = TimeSpan.FromMilliseconds(18);

    /// <summary>
    /// How far the lines melt into the black where more lie beyond (see the fades); the chosen line is kept clear of it,
    /// so the next one peeks in.
    /// </summary>
    private const double Fade = 18;

    // The slim bar at the right edge: kept off the drop's rounded top and off the footer (its top margin, in the XAML),
    // never too short to see.
    private const double ThumbInset = 10;
    private const double SmallestThumb = 28;

    private readonly List<SearchResultRow> _rows = [];
    private readonly Compositor _compositor;
    private readonly SpringChannel _highlightTop;
    private readonly SpringChannel _highlightShown;

    /// <summary>
    /// What the fades and the bar read on the compositor, besides the scroll itself: « Scrollable », how far the list goes
    /// past what shows (0: it all shows); « Travel », how far the bar moves.
    /// </summary>
    private readonly CompositionPropertySet _scroll;

    private ShellIcons? _icons;
    private int _count;
    private int _lines = 1;

    /// <summary>The search the lines are for.</summary>
    private string? _query;

    /// <summary>A line was chosen by hand (arrows, pointer) for <see cref="_query"/>: it stays chosen as its results update.</summary>
    private bool _picked;

    /// <summary>Where the list was last sent (see <see cref="Reveal"/>), until it gets there; <c>null</c>: it is where it shows.</summary>
    private double? _aim;

    public SearchResultsPanel()
    {
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        Width = PanelWidth;

        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        var state = _compositor.CreatePropertySet();
        _highlightTop = new SpringChannel(state, "Top", 0);
        _highlightShown = new SpringChannel(state, "Shown", 0);
        ElementCompositionPreview.SetIsTranslationEnabled(Highlight, true);
        var highlight = ElementCompositionPreview.GetElementVisual(Highlight);
        new CompositionExpression(_compositor, "Vector3(0, h.Top, 0)").With("h", state).Start(highlight, "Translation");
        new CompositionExpression(_compositor, "Clamp(h.Shown, 0, 1)").With("h", state).Start(highlight, "Opacity");

        // The fades and the bar follow the scroll on the compositor: nothing runs here while the list moves.
        _scroll = _compositor.CreatePropertySet();
        _scroll.InsertScalar("Scrollable", 0);
        _scroll.InsertScalar("Travel", 0);
        var scrolled = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(Scroller);
        new CompositionExpression(_compositor, "Clamp(-s.Translation.Y / fade, 0, 1)")
            .With("s", scrolled)
            .With("fade", Fade)
            .Start(ElementCompositionPreview.GetElementVisual(TopFade), "Opacity");
        new CompositionExpression(_compositor, "Clamp((p.Scrollable + s.Translation.Y) / fade, 0, 1)")
            .With("s", scrolled)
            .With("p", _scroll)
            .With("fade", Fade)
            .Start(ElementCompositionPreview.GetElementVisual(BottomFade), "Opacity");
        ElementCompositionPreview.SetIsTranslationEnabled(Thumb, true);
        new CompositionExpression(_compositor, "Vector3(0, Clamp(-s.Translation.Y / Max(p.Scrollable, 1), 0, 1) * p.Travel, 0)")
            .With("s", scrolled)
            .With("p", _scroll)
            .Start(ElementCompositionPreview.GetElementVisual(Thumb), "Translation");

        Scroller.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate)
            {
                _aim = null; // Got there (or the wheel took over and came to rest).
            }
        };
    }

    /// <summary>A line was clicked.</summary>
    public event EventHandler<SearchResult>? Invoked;

    /// <summary>An action was picked in a line's menu (a right click): open it, as administrator, in its folder, copy…</summary>
    internal event Action<SearchResult, SearchOpening>? ActionChosen;

    /// <summary>A line's menu went away: the typing goes on.</summary>
    public event EventHandler? MenuClosed;

    /// <summary>The line chosen (arrows, pointer); -1 with no results.</summary>
    public int Selected { get; private set; } = -1;

    internal SearchResult? SelectedResult => Selected >= 0 && Selected < _count ? _rows[Selected].Result : null;

    /// <summary>The drop's size for <paramref name="lines"/> lines (one, for "no results").</summary>
    public static Size SizeFor(int lines) => new(PanelWidth, Top + Math.Max(lines, 1) * RowHeight + Bottom + Footer);

    /// <summary>
    /// Shows <paramref name="results"/>, <paramref name="lines"/> at once (the drop is that tall: see <see cref="SizeFor"/>),
    /// the rest a scroll away. The first is chosen, at the top; only a line chosen by hand for this same search (its
    /// files coming in, a confirmation…) stays chosen, wherever it moved to. A letter more is a new search: the
    /// best answer first.
    /// </summary>
    public void Show(IReadOnlyList<SearchResult> results, int lines, string query, ShellIcons icons, string? confirming)
    {
        _icons = icons;
        if (query != _query)
        {
            _query = query;
            _picked = false;
        }

        string? chosen = _picked ? SelectedResult?.Key : null;
        int keep = -1;
        for (int i = 0; chosen is not null && i < results.Count; i++)
        {
            if (results[i].Key == chosen)
            {
                keep = i;
                break;
            }
        }

        _count = results.Count;
        _lines = Math.Max(1, lines);
        int selected = _count == 0 ? -1 : Math.Max(keep, 0);

        // Only the lines in sight once the list has settled slide in; the others just take their place.
        int first = selected < 0 ? 0 : (int)(OffsetShowing(selected, from: keep >= 0 ? null : 0) / RowHeight);
        while (_rows.Count < results.Count)
        {
            AddRow();
        }

        int arriving = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (i >= results.Count)
            {
                row.Visibility = Visibility.Collapsed;
                continue;
            }

            row.Visibility = Visibility.Visible;
            if (row.Show(results[i], icons, confirming == results[i].Key) && i >= first && i <= first + _lines)
            {
                Arrive(row, arriving++);
            }
        }

        NoResults.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Text = Loc.F("Aucun résultat pour « {0} »", query.Trim());

        // The bar: as long as the share of the list in sight, along the list's part of the drop.
        double track = Top + Math.Min(_count, _lines) * RowHeight + Bottom - 2 * ThumbInset;
        double thumb = Math.Min(track, Math.Max(SmallestThumb, track * _lines / Math.Max(_count, 1)));
        Thumb.Visibility = _count > _lines ? Visibility.Visible : Visibility.Collapsed;
        Thumb.Height = thumb;
        _scroll.InsertScalar("Scrollable", (float)Scrollable);
        _scroll.InsertScalar("Travel", (float)(track - thumb));

        if (keep < 0)
        {
            ScrollTo(0, animate: false); // A new list starts at its top.
        }

        Choose(selected, glide: keep >= 0);
    }

    /// <summary>The next or previous line (round the ends).</summary>
    public void Move(int step)
    {
        if (_count > 0)
        {
            _picked = true;
            Choose(((Selected + step) % _count + _count) % _count, glide: true);
        }
    }

    /// <summary>A page down (or up, <paramref name="direction"/> -1): the line a screen further, stopping at the ends.</summary>
    public void Page(int direction)
    {
        if (_count > 0)
        {
            _picked = true;
            Choose(Math.Clamp(Selected + direction * Math.Max(1, _lines - 1), 0, _count - 1), glide: true);
        }
    }

    /// <summary>Says how Everything is doing, at the bottom right.</summary>
    public void ShowEverything(EverythingStatus status, bool usersOwn)
    {
        (EverythingText.Text, EverythingGlyph.Opacity) = status switch
        {
            EverythingStatus.Ready => (Loc.T(usersOwn || EverythingEngine.IsFullSpeed ? "Boosté par Everything" : "Boosté par Everything · dossier perso"), 1.0),
            EverythingStatus.Starting => (Loc.T("Everything prépare l'index…"), 0.6),
            _ => (Loc.T("Everything indisponible"), 0.3),
        };
    }

    private void AddRow()
    {
        var row = new SearchResultRow();
        ElementCompositionPreview.SetIsTranslationEnabled(row, true);
        int index = _rows.Count;
        // The pointer moving chooses the line under it (not the list scrolling under a still pointer: the arrows keep
        // what they chose), and leaves the list where it is.
        row.PointerMoved += (_, _) =>
        {
            if (Selected != index)
            {
                _picked = true;
                Choose(index, glide: true, reveal: false);
            }
        };
        row.DoubleTapped += (_, e) => e.Handled = true; // A double click opens once, and does not pin the notch.
        row.Tapped += (_, e) =>
        {
            e.Handled = true;
            if (row.Result is { } result)
            {
                Invoked?.Invoke(this, result);
            }
        };
        row.ContextFlyout = CreateMenu(row, index);
        _rows.Add(row);
        Rows.Children.Add(row);
    }

    /// <summary>
    /// What the Explorer knows the result as: its path (a file, a folder, a local project, a desktop app), or for an app
    /// from the Store its place in the apps folder; <c>null</c> for the rest (a setting page, a command, a calculation…).
    /// </summary>
    internal static string? ShellPath(SearchResult result) =>
        result.Path ?? (result.Kind == SearchKind.Application ? result.Target : null);

    /// <summary>
    /// A right click on a line: what can be done with what it shows, filled in as it opens. Most of the Explorer's own
    /// menu, in the notch's style (its commands run as the Explorer runs them, see ShellMenu), and the whole of it
    /// behind « Afficher plus d'options », as in Windows 11.
    /// </summary>
    private MenuFlyout CreateMenu(SearchResultRow row, int index)
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (row.Result is not { } result)
            {
                return;
            }

            _picked = true;
            Choose(index, glide: true, reveal: false);
            if (result.Kind == SearchKind.Calculation)
            {
                Add(result, Loc.T("Copier le résultat"), "", SearchOpening.Open);
                return;
            }

            Add(result, result.Kind == SearchKind.Command ? Loc.T("Exécuter") : Loc.T("Ouvrir"), "", SearchOpening.Open);
            bool file = result.Kind == SearchKind.File && result.Path is not null;
            bool item = result.Path is not null && result.Kind is SearchKind.File or SearchKind.Folder or SearchKind.Project;
            if (file)
            {
                Add(result, Loc.T("Ouvrir avec…"), "", SearchOpening.OpenWith);
            }

            if (result.Kind is SearchKind.Application or SearchKind.File)
            {
                Add(result, Loc.T("Exécuter en tant qu'administrateur"), "", SearchOpening.Elevated);
            }

            if (result.Path is not null)
            {
                Add(result, Loc.T("Ouvrir l'emplacement"), "", SearchOpening.Reveal);
                menu.Items.Add(new MenuFlyoutSeparator());
                if (item)
                {
                    Add(result, Loc.T("Copier"), "", SearchOpening.CopyItem);
                }

                Add(result, Loc.T("Copier le chemin"), "", SearchOpening.CopyPath);
                if (item && result.Kind != SearchKind.Project)
                {
                    Add(result, Loc.T("Supprimer"), "", SearchOpening.Delete);
                }

                Add(result, Loc.T("Propriétés"), "", SearchOpening.Properties);
            }
            else if (result.Kind == SearchKind.Web)
            {
                Add(result, Loc.T("Copier le lien"), "", SearchOpening.CopyPath);
            }

            if (ShellPath(result) is not null)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                Add(result, Loc.T("Afficher plus d'options"), "", SearchOpening.MoreOptions);
            }
        };
        menu.Closed += (_, _) => MenuClosed?.Invoke(this, EventArgs.Empty);
        return menu;

        void Add(SearchResult result, string text, string glyph, SearchOpening opening)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => ActionChosen?.Invoke(result, opening);
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Line <paramref name="index"/> chosen (-1: none), the highlight gliding to it or there at once; with
    /// <paramref name="reveal"/>, the list scrolls to have it in sight (the keyboard; not the pointer, which is on it).
    /// </summary>
    private void Choose(int index, bool glide, bool reveal = true)
    {
        if (Selected >= 0 && Selected < _rows.Count)
        {
            _rows[Selected].Select(false);
        }

        Selected = index;
        if (index < 0)
        {
            _highlightShown.AnimateTo(0, Motion.Fade);
            return;
        }

        _rows[index].Select(true);
        double top = index * RowHeight;
        if (glide)
        {
            _highlightTop.AnimateTo(top, Motion.Select);
        }
        else
        {
            _highlightTop.Snap(top);
        }

        _highlightShown.AnimateTo(1, Motion.Fade);
        if (reveal)
        {
            ScrollTo(OffsetShowing(index), animate: glide);
        }
    }

    /// <summary>How far the list scrolls at most: the lines past what shows.</summary>
    private double Scrollable => Math.Max(0, _count - _lines) * RowHeight;

    /// <summary>
    /// Where the list must be for line <paramref name="index"/> to be in full sight, clear of the fades (the next line
    /// peeks in), as near as can be to <paramref name="from"/> (by default, where the list is or is going).
    /// </summary>
    private double OffsetShowing(int index, double? from = null)
    {
        // The line's top clear of the top fade (if a line is above it), its bottom clear of the bottom one (if one is below).
        double highest = index * RowHeight + Top - (index > 0 ? Fade : Top);
        double lowest = (index + 1 - _lines) * RowHeight + (index < _count - 1 ? Fade : Bottom) - Bottom;
        double offset = from ?? _aim ?? Scroller.VerticalOffset;
        return Math.Clamp(Math.Max(Math.Min(offset, highest), lowest), 0, Scrollable);
    }

    /// <summary>The list to <paramref name="offset"/>, gliding there or at once; nothing if it is there (or going there).</summary>
    private void ScrollTo(double offset, bool animate)
    {
        double now = Scroller.VerticalOffset;
        if (Math.Abs(offset - (_aim ?? now)) < 0.5)
        {
            return;
        }

        if (offset > Scroller.ScrollableHeight)
        {
            Scroller.UpdateLayout(); // The lines just came: the scroller learns how long the list is first.
        }

        _aim = Math.Abs(offset - now) < 0.5 ? null : offset;
        Scroller.ChangeView(null, offset, null, disableAnimation: !animate);
    }

    /// <summary>A line showing something new slides in and brightens, a beat after the one before.</summary>
    private void Arrive(SearchResultRow row, int order)
    {
        var visual = ElementCompositionPreview.GetElementVisual(row);
        var delay = ArrivalStagger * order;
        var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1));

        var opacity = _compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(1, 1, ease);
        opacity.Duration = TimeSpan.FromMilliseconds(220);
        opacity.DelayTime = delay;
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        var slide = _compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, new Vector3(ArrivalShift, 0, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, ease);
        slide.Duration = TimeSpan.FromMilliseconds(360);
        slide.DelayTime = delay;
        slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", slide);
    }

    private void OnEverythingClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using (Process.Start(new ProcessStartInfo("https://www.voidtools.com") { UseShellExecute = true }))
            {
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser: nothing to do.
        }
    }
}
