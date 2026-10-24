using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using MyNotch.Composition;
using MyNotch.Modules.Search.Everything;
using Windows.Foundation;
using MyNotch.Localization;

namespace MyNotch.Modules.Search;

/// <summary>
/// The search's results, in the drop under the notch: one line each, the chosen one lit by a highlight that glides from
/// line to line, new lines sliding in one after the other, and at the bottom what the keys do and who finds the files.
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

    private readonly List<SearchResultRow> _rows = [];
    private readonly Compositor _compositor;
    private readonly SpringChannel _highlightTop;
    private readonly SpringChannel _highlightShown;
    private ShellIcons? _icons;
    private int _count;

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

    /// <summary>Shows <paramref name="results"/>; the chosen line stays chosen if it is still there.</summary>
    public void Show(IReadOnlyList<SearchResult> results, string query, ShellIcons icons, string? confirming)
    {
        _icons = icons;
        string? chosen = SelectedResult?.Key;
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
            if (row.Show(results[i], icons, confirming == results[i].Key))
            {
                Arrive(row, arriving++);
            }
        }

        _count = results.Count;
        NoResults.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Text = Loc.F("Aucun résultat pour « {0} »", query.Trim());

        int keep = chosen is null ? -1 : results.ToList().FindIndex(result => result.Key == chosen);
        Choose(results.Count == 0 ? -1 : Math.Max(keep, 0), glide: keep >= 0);
    }

    /// <summary>The next or previous line (round the ends).</summary>
    public void Move(int step)
    {
        if (_count > 0)
        {
            Choose(((Selected + step) % _count + _count) % _count, glide: true);
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
        row.PointerEntered += (_, _) => Choose(index, glide: true);
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

            Choose(index, glide: true);
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

    private void Choose(int index, bool glide)
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
