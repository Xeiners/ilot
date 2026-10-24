using System.Collections.Specialized;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Ilot.Composition;
using Ilot.Controls;
using Ilot.Localization;
using Windows.Foundation;
using Windows.UI;

namespace Ilot.Modules.Code;

/// <summary>
/// VS Code's recent projects in the search's drop (« code » typed, see SearchView): a list, latest first, each project
/// a line in the Code tiles' look (see <see cref="CodeRow"/>), filtered by the words after « code » (name, location or
/// stack: « code react »). The first match is chosen; the arrows choose another, Enter opens it, as a click does.
/// </summary>
public sealed partial class CodeShelf : Grid
{
    public const double ShelfWidth = 660;

    // The list: a title above, at most this many lines in sight (it scrolls past them), a little room below.
    private const double TitleHeight = 40;
    private const double LineSpacing = 6;
    private const int MostInSight = 6;
    private const double Bottom = 14;

    private readonly CodeViewModel _viewModel;
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _rows;
    private readonly StackPanel _message;
    private readonly TextBlock _messageText;
    private readonly TextBlock _count;
    private readonly Rectangle _topFade;
    private readonly Rectangle _bottomFade;
    private readonly ImplicitAnimationCollection _reflow;
    private readonly CompositionAnimationGroup _show;
    private CodeRow? _selection;
    private string[] _words = [];

    public CodeShelf(CodeViewModel viewModel)
    {
        _viewModel = viewModel;
        Width = ShelfWidth;
        VerticalAlignment = VerticalAlignment.Top;

        // A small title, as the search's sections have: what this is, and how many.
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(24, 14, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        title.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = Resource<Brush>("NotchSecondaryTextBrush") });
        title.Children.Add(new TextBlock { Text = Loc.T("Projets VS Code"), Style = Resource<Style>("NotchSubtitleTextStyle") });
        _count = new TextBlock { Style = Resource<Style>("NotchSubtitleTextStyle"), Opacity = 0.7 };
        title.Children.Add(_count);
        Children.Add(title);

        _rows = new StackPanel { Spacing = LineSpacing, Padding = new Thickness(16, 2, 16, 2) };
        _scroller = new ScrollViewer
        {
            Margin = new Thickness(0, TitleHeight, 0, Bottom - 2),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _rows,
        };
        var list = new Grid { VerticalAlignment = VerticalAlignment.Top };
        list.Children.Add(_scroller);
        _topFade = Fade(top: true);
        _bottomFade = Fade(top: false);
        list.Children.Add(_topFade);
        list.Children.Add(_bottomFade);
        Children.Add(list);
        _scroller.ViewChanged += (_, _) => UpdateFades();
        _scroller.SizeChanged += (_, _) => UpdateFades();

        // Nothing to show (VS Code missing, no project yet, nothing matches): said under the title.
        _messageText = new TextBlock { Style = Resource<Style>("NotchSubtitleTextStyle"), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, TextAlignment = TextAlignment.Center };
        _message = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, TitleHeight + 8, 0, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _message.Children.Add(new FontIcon { Glyph = "", FontSize = 20, Foreground = Resource<Brush>("NotchSecondaryTextBrush") });
        _message.Children.Add(_messageText);
        Children.Add(_message);

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _reflow = ImplicitMotion.Reflow(compositor, Motion.Reflow);
        _show = ImplicitMotion.Show(compositor, Motion.Pop, fromScale: 0.92f);
        foreach (var project in viewModel.Projects)
        {
            _rows.Children.Add(CreateRow(project));
        }

        viewModel.Projects.CollectionChanged += OnProjectsChanged;
        viewModel.PropertyChanged += (_, _) => ShowMessage();
        ShowMessage();
    }

    /// <summary>The drop's size for what the list shows now: as tall as its lines, up to <see cref="MostInSight"/> of them.</summary>
    public Size Fit()
    {
        int lines = Math.Min(VisibleRows().Count(), MostInSight);
        double list = lines == 0 ? 64 : lines * CodeRow.RowHeight + (lines - 1) * LineSpacing + 4;
        _scroller.Height = Math.Max(list, 0);
        return new Size(ShelfWidth, TitleHeight + list + Bottom);
    }

    /// <summary>
    /// The projects for <paramref name="words"/> (the search's words after « code »; none: all of them, latest first):
    /// the others leave and the list closes up; the first one is chosen.
    /// </summary>
    public void Show(string[] words)
    {
        bool changed = !words.SequenceEqual(_words);
        _words = words;
        foreach (var row in _rows.Children.OfType<CodeRow>())
        {
            Filter(row);
        }

        if (changed)
        {
            _scroller.ChangeView(null, 0, null, disableAnimation: true);
        }

        Select(VisibleRows().FirstOrDefault());
        ShowMessage();
    }

    /// <summary>The arrows: the next or the previous line is chosen.</summary>
    public void Move(int step)
    {
        var rows = VisibleRows().ToList();
        if (rows.Count > 0)
        {
            int index = _selection is null ? -1 : rows.IndexOf(_selection);
            Select(rows[Math.Clamp(index + step, 0, rows.Count - 1)]);
        }
    }

    /// <summary>Enter: the chosen project opens in VS Code; <c>false</c> if there is none.</summary>
    public bool OpenChosen()
    {
        if ((_selection?.Visibility == Visibility.Visible ? _selection : VisibleRows().FirstOrDefault()) is not { } chosen)
        {
            return false;
        }

        chosen.Open();
        return true;
    }

    /// <summary>The list changed (projects read again, a filter): the drop is told its new size.</summary>
    public event EventHandler? Changed;

    private IEnumerable<CodeRow> VisibleRows() =>
        _rows.Children.OfType<CodeRow>().Where(row => row.Visibility == Visibility.Visible);

    private void Filter(CodeRow row)
    {
        var project = row.Project;
        bool match = _words.Length == 0 || TextSearch.Matches($"{project.Name} {project.Place} {project.Label}", _words);
        row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
        row.Highlight(_words);
    }

    private void Select(CodeRow? row)
    {
        if (row != _selection)
        {
            _selection?.SetSelected(false);
            _selection = row;
            row?.SetSelected(true);
        }
    }

    /// <summary>How many there are, or why there is none: VS Code missing, no project yet, nothing matching the words.</summary>
    private void ShowMessage()
    {
        int shown = VisibleRows().Count();
        string? message = _viewModel.Message
            ?? (shown == 0 && _words.Length > 0 ? Loc.F("Aucun projet ne correspond à « {0} ».", string.Join(' ', _words)) : null);
        _messageText.Text = message ?? "";
        _message.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        _scroller.Visibility = message is null ? Visibility.Visible : Visibility.Collapsed;
        _count.Text = message is null ? Loc.Count(shown, "projet", "projets") : "";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>An end of the list fades into the black where there are more lines past it (no scroll bar).</summary>
    private void UpdateFades()
    {
        _topFade.Opacity = _scroller.VerticalOffset > 1 ? 1 : 0;
        _bottomFade.Opacity = _scroller.VerticalOffset < _scroller.ScrollableHeight - 1 ? 1 : 0;
    }

    private Rectangle Fade(bool top)
    {
        var fill = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        fill.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(top ? (byte)0xFF : (byte)0, 0, 0, 0) });
        fill.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(top ? (byte)0 : (byte)0xFF, 0, 0, 0) });
        return new Rectangle
        {
            Height = 22,
            Margin = top ? new Thickness(0, TitleHeight, 0, 0) : new Thickness(0, 0, 0, Bottom - 2),
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Opacity = 0,
            Fill = fill,
        };
    }

    private CodeRow CreateRow(RecentProject project)
    {
        var row = new CodeRow(project, _viewModel);
        var visual = ElementCompositionPreview.GetElementVisual(row);
        visual.CenterPoint = new Vector3((float)(ShelfWidth / 2 - 16), (float)(CodeRow.RowHeight / 2), 0);
        ElementCompositionPreview.SetImplicitShowAnimation(row, _show);

        // Glide on layout changes only once placed: attached any sooner, a new line would fly in from the corner.
        row.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => visual.ImplicitAnimations = _reflow);
        Filter(row);
        return row;
    }

    /// <summary>The list is replaced as a whole when the projects changed: it starts over at its top.</summary>
    private void OnProjectsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                for (int i = 0; i < e.NewItems!.Count; i++)
                {
                    _rows.Children.Insert(e.NewStartingIndex + i, CreateRow((RecentProject)e.NewItems[i]!));
                }

                break;

            default:
                _rows.Children.Clear();
                _scroller.ChangeView(null, 0, null, disableAnimation: true);
                foreach (var project in _viewModel.Projects)
                {
                    _rows.Children.Add(CreateRow(project));
                }

                break;
        }

        Select(VisibleRows().FirstOrDefault());
        ShowMessage();
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];
}
