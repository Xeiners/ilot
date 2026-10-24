using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyNotch.Localization;
using Windows.UI;

namespace MyNotch.Modules.Assistant;

/// <summary>
/// The conversations kept (see <see cref="AssistantHistory"/>), in the assistant's drop in place of the conversation: a
/// line each, latest first (its first question, when, how many messages); a click opens it to go on with it, its bin
/// forgets it. « Tout effacer » forgets them all, once confirmed. Back with the arrow (or Escape).
/// </summary>
public sealed partial class AssistantHistoryView : Grid
{
    private const double LineHeight = 50;
    private const double LineSpacing = 6;
    private const double HeaderHeight = 38;
    private const int MostInSight = 6;

    private static readonly SolidColorBrush Text = new(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Faint = new(Color.FromArgb(0x8F, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Line = new(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush LineHovered = new(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Clear = new(Microsoft.UI.Colors.Transparent);

    private readonly AssistantHistory _history;
    private readonly StackPanel _lines;
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _empty;
    private readonly Button _forgetAll;
    private readonly TextBlock _forgetAllText;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _confirmTimer;
    private bool _confirming;

    public AssistantHistoryView(AssistantHistory history)
    {
        _history = history;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Back, the title, and forgetting them all.
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = Small("", Loc.T("Retour à la conversation"));
        back.Click += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        header.Children.Add(back);
        var title = new TextBlock
        {
            Text = Loc.T("Historique"),
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        _forgetAllText = new TextBlock { FontSize = 12, Foreground = Faint };
        _forgetAll = new Button
        {
            Padding = new Thickness(10, 4, 10, 4),
            Background = Clear,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Content = _forgetAllText,
        };
        _forgetAll.Click += (_, _) => ForgetAll();
        Grid.SetColumn(_forgetAll, 2);
        header.Children.Add(_forgetAll);
        Children.Add(header);

        _lines = new StackPanel { Spacing = LineSpacing, Padding = new Thickness(0, 2, 8, 2) };
        _scroller = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _lines,
        };
        Grid.SetRow(_scroller, 1);
        Children.Add(_scroller);
        _empty = new TextBlock
        {
            Text = Loc.T("Aucune conversation pour l'instant : elles se gardent ici au fur et à mesure."),
            FontSize = 12.5,
            Foreground = Faint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 0, 0),
        };
        Grid.SetRow(_empty, 1);
        Children.Add(_empty);

        _confirmTimer = DispatcherQueue.CreateTimer();
        _confirmTimer.Interval = TimeSpan.FromSeconds(3);
        _confirmTimer.IsRepeating = false;
        _confirmTimer.Tick += (_, _) => Confirming(false);
        history.Changed += (_, _) =>
        {
            if (Visibility == Visibility.Visible)
            {
                Refresh();
            }
        };
        Confirming(false);
    }

    /// <summary>A conversation chosen: to open, to go on with it.</summary>
    public event EventHandler<SavedConversation>? Opened;

    /// <summary>Back to the conversation.</summary>
    public event EventHandler? Closed;

    /// <summary>How tall it would like to be: its header and its lines, up to <see cref="MostInSight"/> in sight.</summary>
    public double DesiredContentHeight
    {
        get
        {
            int lines = Math.Min(_history.Conversations.Count, MostInSight);
            return HeaderHeight + (lines == 0 ? 40 : lines * LineHeight + (lines - 1) * LineSpacing + 4);
        }
    }

    /// <summary>The list as the history is now (opened, or changed while open).</summary>
    public void Refresh()
    {
        _lines.Children.Clear();
        foreach (var conversation in _history.Conversations)
        {
            _lines.Children.Add(CreateLine(conversation));
        }

        bool none = _history.Conversations.Count == 0;
        _empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        _forgetAll.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        Confirming(false);
        _scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private Grid CreateLine(SavedConversation conversation)
    {
        var line = new Grid
        {
            Height = LineHeight,
            Padding = new Thickness(14, 0, 8, 0),
            ColumnSpacing = 10,
            CornerRadius = new CornerRadius(12),
            Background = Line,
        };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        words.Children.Add(new TextBlock
        {
            Text = conversation.Title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        words.Children.Add(new TextBlock
        {
            Text = $"{When(conversation.Updated)} · {Loc.Count(conversation.Messages.Count, "message", "messages")}",
            FontSize = 11.5,
            Foreground = Faint,
        });
        line.Children.Add(words);

        var forget = Small("", Loc.T("Oublier cette conversation"));
        forget.Opacity = 0;
        forget.Click += (_, _) => _history.Forget(conversation.Id);
        forget.Tapped += (_, e) => e.Handled = true; // Its own: not the line's.
        Grid.SetColumn(forget, 1);
        line.Children.Add(forget);

        AutomationProperties.SetName(line, conversation.Title);
        line.PointerEntered += (_, _) =>
        {
            line.Background = LineHovered;
            forget.Opacity = 1;
        };
        line.PointerExited += (_, _) =>
        {
            line.Background = Line;
            forget.Opacity = 0;
        };
        line.Tapped += (_, e) =>
        {
            e.Handled = true;
            Opened?.Invoke(this, conversation);
        };
        return line;
    }

    /// <summary>« Tout effacer »: asks once more (for a moment), then forgets them all.</summary>
    private void ForgetAll()
    {
        if (!_confirming)
        {
            Confirming(true);
            return;
        }

        _history.ForgetAll();
        Confirming(false);
    }

    private void Confirming(bool confirming)
    {
        _confirming = confirming;
        _confirmTimer.Stop();
        _forgetAllText.Text = confirming ? Loc.T("Vraiment tout effacer ?") : Loc.T("Tout effacer");
        _forgetAllText.Foreground = confirming ? new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x9A, 0x90)) : Faint;
        if (confirming)
        {
            _confirmTimer.Start();
        }
    }

    /// <summary>When it last went on: the time today, « Hier » and the time, else the day.</summary>
    private static string When(DateTimeOffset at)
    {
        var local = at.LocalDateTime;
        var today = DateTime.Today;
        string time = local.ToString("t", Loc.Culture);
        return local.Date == today ? time
            : local.Date == today.AddDays(-1) ? Loc.F("Hier, {0}", time)
            : local.ToString(local.Year == today.Year ? "d MMM" : "d MMM yyyy", Loc.Culture);
    }

    private static Button Small(string glyph, string name)
    {
        var button = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            Background = Clear,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = glyph, FontSize = 12, Foreground = Text },
        };
        ToolTipService.SetToolTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }
}
