using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Ilot.Controls;
using Ilot.Localization;
using Ilot.Settings;
using Windows.UI;
using static Ilot.Modules.Study.StudyPaint;
using VirtualKey = Windows.System.VirtualKey;

namespace Ilot.Modules.Study;

/// <summary>A card answered in a revision: its choices in the order they were shown, the one picked, the rating given.</summary>
internal sealed record AnsweredCard(StudyCard Card, string[] Options, int? Picked, CardRating Rating);

/// <summary>
/// Where a revision is (see <see cref="ReviewView.Export"/>): its session, the card shown and how far it is answered, the
/// cards answered so far and the one looked back at. A sheet detached from the notch to its own window, or brought back,
/// goes on from it (see SheetState).
/// </summary>
internal sealed record ReviewState(
    ReviewSession Session, StudyCard? Current, bool Revealed, string[] Options, int? Picked, CardRating Suggested,
    IReadOnlyList<AnsweredCard> History, int? Looking, bool Cheered);

/// <summary>
/// A revision, in a course's sheet (under the notch, or detached in a window: see CourseSheet): what is left; a card, its question and its four choices (or
/// « Afficher la réponse »); once answered, the right choice green, a wrong pick red, the answer and its source, and how
/// well it came (offered, « Suivant »; or adjusted); the cards answered, looked back at (« Précédente »). Keys: 1 to 4,
/// Espace, ← and →. A choice answered flashes the card and makes a little sound.
/// </summary>
public sealed partial class ReviewView : UserControl
{
    private const int HistoryKept = 50;

    private readonly StudyViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly List<AnsweredCard> _history = [];
    private ReviewSession? _session;
    private StudyCard? _current;
    private bool _revealed;
    private string[] _options = [];
    private int? _picked; // The choice made on the card shown (-1: « Je ne sais pas »), none yet.
    private int? _looking; // The card answered looked back at; none: the one being answered.
    private bool _cheered;
    private CardRating _suggested = CardRating.Good;
    private Color _accent;

    public ReviewView(StudyViewModel viewModel, AppSettings settings)
    {
        _viewModel = viewModel;
        _settings = settings;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        ElementCompositionPreview.GetElementVisual(FaceFlash).Opacity = 0; // Lit only by a flash (see Flash).

        // Revising takes the keys once the user is at it (Espace, 1 to 4).
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => Focus(FocusState.Programmatic)), handledEventsToo: true);
        Tint(Accent(settings));
    }

    /// <summary>A card was rated (the course's numbers changed).</summary>
    public event EventHandler? CardAnswered;

    /// <summary>Échap: the host closes the revision.</summary>
    public event EventHandler? Escaped;

    /// <summary>The courses revised (one, or all of them for everything due).</summary>
    public IReadOnlyList<StudyDeck> Decks => _session?.Decks ?? [];

    /// <summary>A new revision on <paramref name="session"/>: from its first card, nothing answered yet.</summary>
    public void Start(ReviewSession session)
    {
        _session = session;
        _cheered = false;
        _history.Clear();
        _looking = null;
        NextCard();
    }

    /// <summary>
    /// More cards made: nothing answered yet, the session starts again with them; else they come in the next session
    /// (the end of this one offers it).
    /// </summary>
    public void Extend(ReviewSession session)
    {
        if (_session is null || _session.Answered == 0)
        {
            Start(session);
        }
        else if (_current is null && _looking is null)
        {
            NextCard();
        }
    }

    /// <summary>The courses' cards changed (new ones came): a session that had none starts with them.</summary>
    public void CardsChanged()
    {
        if (_session is not { } session || _current is not null || _looking is not null || _grades)
        {
            return;
        }

        if (session.Answered == 0)
        {
            Start(session.Again());
        }
        else
        {
            NextCard(); // The end of the session, the next one offered now.
        }
    }

    /// <summary>Shown again as it is (the sheet back on its revision): the card being answered, or the one looked at.</summary>
    public void Show()
    {
        if (_looking is { } index)
        {
            Look(index);
        }
        else if (_current is null)
        {
            NextCard();
        }
        else
        {
            ShowLive();
        }
    }

    /// <summary>Where the revision is, to go on elsewhere (see <see cref="Import"/>).</summary>
    internal ReviewState? Export() => _session is null ? null
        : new ReviewState(_session, _current, _revealed, _options, _picked, _suggested, [.. _history], _looking, _cheered);

    /// <summary>The revision goes on from <paramref name="state"/>, exactly where it was.</summary>
    internal void Import(ReviewState state)
    {
        _session = state.Session;
        _current = state.Current;
        _revealed = state.Revealed;
        _options = state.Options;
        _picked = state.Picked;
        _suggested = state.Suggested;
        _history.Clear();
        _history.AddRange(state.History);
        _looking = state.Looking;
        _cheered = state.Cheered;
        UpdateCounts();
        Show();
    }

    /// <summary>The card in <paramref name="accent"/> (the mascot's colour): lit from above, its edge, its chapter, its choices.</summary>
    public void Tint(Color accent)
    {
        _accent = accent;
        Face.Background = Linear(new Windows.Foundation.Point(0.5, 0), new Windows.Foundation.Point(0.5, 1),
            (0, Mix(CardBase, accent, 0.24)), (0.6, Mix(CardBase, accent, 0.08)), (1, CardBase));
        Face.BorderBrush = Linear(new Windows.Foundation.Point(0.5, 0), new Windows.Foundation.Point(0.5, 1),
            (0, Alpha(accent, 0x99)), (0.5, Alpha(accent, 0x30)), (1, Alpha(accent, 0x14)));
        Face.BorderThickness = new Thickness(1.5);
        ChapterText.Foreground = new SolidColorBrush(Mix(accent, Microsoft.UI.Colors.White, 0.35));
        ProgressFill.Background = new SolidColorBrush(accent);
        if (_session is not null)
        {
            Show(); // Its choices, in the colour.
        }
    }

    // ── The card being answered ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The session's next card, its question only; or, all answered, how the session went.</summary>
    private void NextCard()
    {
        if (_session is null)
        {
            return;
        }

        _current = _session.Next();
        _revealed = false;
        UpdateCounts();

        if (_current is not { } card)
        {
            ShowDone();
            return;
        }

        _picked = null;
        _looking = null;
        _options = card.HasChoices ? [card.Choice, .. card.Decoys] : [];
        Random.Shared.Shuffle(_options);
        ShowLive();
        Arrive(Face, new Vector3(36, 0, 0));
    }

    /// <summary>Where the session is: « Carte 3 sur 20 », its bar filled with the cards answered.</summary>
    private void UpdateCounts()
    {
        if (_session is not { } session)
        {
            return;
        }

        int shown = Math.Min(session.Answered + 1, session.Total);
        ProgressText.Text = Loc.F("Carte {0} sur {1}", shown, session.Total);
        ProgressFill.Width = session.Total == 0 ? 0 : 180.0 * session.Answered / session.Total;
        Counts.Visibility = _current is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── The session done ─────────────────────────────────────────────────────────────────────────────────────

    private const int HistoryShown = 12;

    /// <summary>
    /// The session done: its grade (kept, once), its cards by how they came, how it compares with the session before,
    /// the sessions before as bars; then the next session, when there are cards for one, else when the next card is
    /// due. Nothing answered (nothing to revise): the sessions before, still.
    /// </summary>
    private void ShowDone()
    {
        if (_session is not { } session)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        HideGrades();
        FaceScroller.Visibility = Visibility.Collapsed;
        ShowAnswerButton.Visibility = RatingButtons.Visibility = NextCardButton.Visibility = Visibility.Collapsed;
        LookLabel.Visibility = ForwardButton.Visibility = ResumeButton.Visibility = Visibility.Collapsed;
        PreviousButton.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed; // The last ones, still.
        _looking = null;
        bool done = session.Answered > 0;
        if (done && !_cheered)
        {
            _cheered = true; // Once a session: its grade kept, its sound.
            _viewModel.Record(session);
            StudySounds.Play(_settings, StudySounds.Done);
        }

        var history = _viewModel.History(session.Scope);
        var score = session.Score;
        GradeText.Visibility = Tally.Visibility = TrendText.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        DoneIcon.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        if (done)
        {
            GradeValue.Text = Grade(score.Grade);
            GradeValue.Foreground = new SolidColorBrush(GradeColor(score.Grade));
            DoneTitle.Text = Loc.F("Session terminée · {0}", Loc.Count(score.Cards, "carte", "cartes"));
            ShowTally(score);

            // This session is the history's last: the one before it, to compare with.
            if (history.Count >= 2)
            {
                double change = score.Grade - history[^2].Score.Grade;
                TrendText.Text = Math.Abs(change) < 0.25
                    ? Loc.T("Comme la session d'avant")
                    : Loc.F("{0} depuis la session d'avant", (change > 0 ? "+" : "−") + Grade(Math.Abs(change)));
                TrendText.Foreground = new SolidColorBrush(Math.Abs(change) < 0.25 ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : change > 0 ? Right : Wrong);
            }
            else
            {
                TrendText.Text = Loc.T("Première session notée : les suivantes montreront vos progrès");
                TrendText.Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
            }
        }
        else
        {
            DoneTitle.Text = Loc.T("Rien à revoir pour l'instant");
        }

        ShowHistory(history, done);

        // Again: the same cards shuffled (and those made since); or, on everything due, what is due now.
        int following = session.Following(now);
        NextSessionButton.Visibility = following > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextSessionText.Text = session.Whole
            ? Loc.F("Recommencer · {0}", Loc.Count(following, "carte", "cartes"))
            : Loc.F("Session suivante · {0}", Loc.Count(following, "carte", "cartes"));
        DoneText.Text = session.Whole
            ? Loc.T("Les mêmes cartes, dans un autre ordre. Ou « Nouvelles cartes » : elles s'ajoutent aux autres.")
            : following > 0 ? ""
            : session.NextDue() is { } next && next > now
                ? Loc.F("Prochaine carte {0}. Pour aller plus loin : « Nouvelles cartes ».", StudyScheduler.When(next, now))
                : Loc.T("Pour aller plus loin : « Nouvelles cartes ».");
        DoneText.Visibility = DoneText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        DonePanel.ChangeView(null, 0, null, disableAnimation: true);
        Arrive(DonePanel, new Vector3(0, 8, 0));
    }

    /// <summary>A grade, as the notch's culture writes it: « 16,5 ».</summary>
    private static string Grade(double grade) => (Math.Round(grade * 2) / 2).ToString("0.#", Loc.Culture);

    /// <summary>Green from 14, orange from 10, red under.</summary>
    private static Color GradeColor(double grade) =>
        grade >= 14 ? Right : grade >= 10 ? Color.FromArgb(0xFF, 0xFF, 0xB8, 0x6B) : Wrong;

    /// <summary>The cards by how they came: « ● 16 bien », « ● 2 difficiles », « ● 2 à revoir » (those there are).</summary>
    private void ShowTally(SessionScore score)
    {
        Tally.Children.Clear();
        foreach (var (count, color, one, many) in new[]
        {
            (score.Right, Right, "bien", "bien"),
            (score.Hard, Color.FromArgb(0xFF, 0xFF, 0xB8, 0x6B), "difficile", "difficiles"),
            (score.Again, Wrong, "à revoir", "à revoir"),
        })
        {
            if (count == 0)
            {
                continue;
            }

            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            item.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock
            {
                Text = Loc.Count(count, one, many),
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
            });
            Tally.Children.Add(item);
        }
    }

    /// <summary>
    /// The last sessions as bars (their grade out of 20; the one just done in the full colour, its grade above it), and
    /// their average and best. None yet: nothing.
    /// </summary>
    private void ShowHistory(IReadOnlyList<SessionPoint> history, bool done)
    {
        HistoryArea.Visibility = history.Count > (done ? 1 : 0) ? Visibility.Visible : Visibility.Collapsed;
        Bars(HistoryChart, [.. history.TakeLast(HistoryShown)], done);
        if (history.Count == 0)
        {
            return;
        }

        double average = history.TakeLast(5).Average(point => point.Score.Grade);
        double best = history.Max(point => point.Score.Grade);
        HistoryText.Text = Loc.F("Moyenne des 5 dernières : {0}/20 · Meilleure : {1}/20 · {2}",
            Grade(average), Grade(best), Loc.Count(history.Count, "session", "sessions"));
    }

    /// <summary>
    /// Sessions as bars in <paramref name="chart"/>, the oldest left: their grade out of 20, above each; the last one in
    /// its grade's colour when <paramref name="lastLit"/> (the session just done). Hovered: when, the grade, its cards.
    /// </summary>
    private void Bars(Grid chart, IReadOnlyList<SessionPoint> shown, bool lastLit)
    {
        chart.Children.Clear();
        chart.ColumnDefinitions.Clear();
        const double Tallest = 52;
        for (int i = 0; i < shown.Count; i++)
        {
            var point = shown[i];
            bool last = lastLit && i == shown.Count - 1;
            chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(shown.Count > 16 ? 18 : 26) });
            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Spacing = 3 };
            column.Children.Add(new TextBlock
            {
                Text = Grade(point.Score.Grade),
                FontSize = 9.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(last ? (byte)0xE6 : (byte)0x80, 0xFF, 0xFF, 0xFF)),
            });
            column.Children.Add(new Border
            {
                Width = shown.Count > 16 ? 10 : 14,
                Height = 3 + Tallest * point.Score.Grade / 20,
                CornerRadius = new CornerRadius(4, 4, 2, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(last ? GradeColor(point.Score.Grade) : Alpha(_accent, 0x66)),
            });
            ToolTipService.SetToolTip(column, Loc.F("{0} · {1}/20 · {2}",
                point.Finished.ToString("d MMM, HH:mm", Loc.Culture), Grade(point.Score.Grade), Loc.Count(point.Score.Cards, "carte", "cartes")));
            Grid.SetColumn(column, i);
            chart.Children.Add(column);
        }
    }

    private void OnNextSessionClick(object sender, RoutedEventArgs e) => NextSession();

    /// <summary>Another session on the same courses, from its first card: the same cards (and the new ones), shuffled.</summary>
    private void NextSession()
    {
        if (_session is { } session && NextSessionButton.Visibility == Visibility.Visible)
        {
            Start(session.Again());
        }
    }

    // ── Every grade ──────────────────────────────────────────────────────────────────────────────────────────

    private const int GradesCharted = 30;
    private bool _grades;

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_grades)
        {
            Show(); // Back to the session, as it was.
        }
        else
        {
            ShowGrades();
        }
    }

    /// <summary>The grades shown no more: the button says « Historique » again.</summary>
    private void HideGrades()
    {
        _grades = false;
        GradesPanel.Visibility = Visibility.Collapsed;
        HistoryButtonText.Text = Loc.T("Historique");
        HistoryGlyph.Glyph = "";
        HistoryButton.Visibility = _session is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Every grade on what is revised (the session left as it is, under it): their average, best and last, the last
    /// <see cref="GradesCharted"/> as bars, then each, the latest first — when, the grade, its cards by how they came, the
    /// change since the one before.
    /// </summary>
    private void ShowGrades()
    {
        if (_session is not { } session)
        {
            return;
        }

        _grades = true;
        FaceScroller.Visibility = DonePanel.Visibility = Counts.Visibility = Visibility.Collapsed;
        PreviousButton.Visibility = LookLabel.Visibility = ForwardButton.Visibility = Visibility.Collapsed;
        ShowAnswerButton.Visibility = RatingButtons.Visibility = NextCardButton.Visibility = ResumeButton.Visibility = Visibility.Collapsed;
        HistoryButtonText.Text = Loc.T("Revenir");
        HistoryGlyph.Glyph = "";
        HistoryButton.Visibility = Visibility.Visible;

        var history = _viewModel.History(session.Scope);
        GradesEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GradesStats.Visibility = GradesChart.Visibility = history.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        GradesStats.Children.Clear();
        GradesList.Children.Clear();
        if (history.Count > 0)
        {
            GradesStats.Children.Add(Stat(Loc.T("Moyenne"), history.Average(point => point.Score.Grade)));
            GradesStats.Children.Add(Stat(Loc.T("Meilleure"), history.Max(point => point.Score.Grade)));
            GradesStats.Children.Add(Stat(Loc.T("Dernière"), history[^1].Score.Grade));
            Bars(GradesChart, [.. history.TakeLast(GradesCharted)], lastLit: true);
            for (int i = history.Count - 1; i >= 0; i--)
            {
                GradesList.Children.Add(GradeRow(history[i], i > 0 ? history[i - 1] : null));
            }
        }

        GradesPanel.Visibility = Visibility.Visible;
        GradesPanel.ChangeView(null, 0, null, disableAnimation: true);
        Arrive(GradesPanel, new Vector3(0, 8, 0));
    }

    /// <summary>A figure of the history: « Moyenne », « 14,5/20 ».</summary>
    private static Border Stat(string label, double grade)
    {
        var text = new TextBlock { FontFamily = new FontFamily("Segoe UI Variable Text"), FontSize = 12.5 };
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = label + "  ", Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) });
        text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
        {
            Text = Grade(grade) + "/20",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(GradeColor(grade)),
        });
        return new Border
        {
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            Child = text,
        };
    }

    /// <summary>A session in the history: when, its cards by how they came, the change since <paramref name="before"/>, its grade.</summary>
    private static Border GradeRow(SessionPoint point, SessionPoint? before)
    {
        var score = point.Score;
        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = point.Finished.ToString("dddd d MMMM · HH:mm", Loc.Culture),
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF)),
        });
        var parts = new List<string> { Loc.Count(score.Cards, "carte", "cartes"), Loc.Count(score.Right, "bien", "bien") };
        if (score.Hard > 0)
        {
            parts.Add(Loc.Count(score.Hard, "difficile", "difficiles"));
        }

        if (score.Again > 0)
        {
            parts.Add(Loc.Count(score.Again, "à revoir", "à revoir"));
        }

        left.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", parts),
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        row.Children.Add(left);

        if (before is not null)
        {
            double change = score.Grade - before.Score.Grade;
            var trend = new TextBlock
            {
                Text = Math.Abs(change) < 0.25 ? "=" : (change > 0 ? "+" : "−") + Grade(Math.Abs(change)),
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Math.Abs(change) < 0.25 ? Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF) : change > 0 ? Right : Wrong),
            };
            Grid.SetColumn(trend, 1);
            row.Children.Add(trend);
        }

        var grade = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grade.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = Grade(score.Grade), FontSize = 20, Foreground = new SolidColorBrush(GradeColor(score.Grade)) });
        grade.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "/20", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)) });
        Grid.SetColumn(grade, 2);
        row.Children.Add(grade);

        return new Border
        {
            Padding = new Thickness(16, 10, 16, 10),
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = row,
        };
    }

    /// <summary>
    /// The card being answered, as it is now: its question and its choices, to answer; or, answered, the choices marked,
    /// the answer, and the ratings. Also on coming back from a card looked back at.
    /// </summary>
    private void ShowLive()
    {
        if (_current is not { } card)
        {
            return;
        }

        _looking = null;
        HideGrades();
        DonePanel.Visibility = Visibility.Collapsed;
        FaceScroller.Visibility = Visibility.Visible;
        Counts.Visibility = Visibility.Visible;
        LookLabel.Visibility = ForwardButton.Visibility = ResumeButton.Visibility = Visibility.Collapsed;
        PreviousButton.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowQuestion(card, _options, interactive: !_revealed);
        FaceScroller.ChangeView(null, 0, null, disableAnimation: true);
        if (_revealed)
        {
            MarkOptions(card, _options, _picked);
            ShowAnswer(card);
            ShowRatings(card);
        }
        else
        {
            AnswerArea.Visibility = Visibility.Collapsed;
            ShowAnswerButton.Visibility = Visibility.Visible;
            RatingButtons.Visibility = NextCardButton.Visibility = Visibility.Collapsed;
            AnswerBar.Orientation = Orientation.Vertical;
        }
    }

    /// <summary>A card's chapter, question and choices (to answer, or only shown).</summary>
    private void ShowQuestion(StudyCard card, string[] options, bool interactive)
    {
        ChapterText.Text = card.Chapter.ToUpper(Loc.Culture);
        ChapterText.Visibility = card.Chapter.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowText(QuestionText, QuestionMath, card.Question);
        ShowOptions(card, options, interactive);
    }

    /// <summary>
    /// A card's choices in the order given, each with its number (1 to 4); the answer button becomes « Je ne sais pas ».
    /// Without them, the answer is asked for and rated as ever.
    /// </summary>
    private void ShowOptions(StudyCard card, string[] options, bool interactive)
    {
        Options.Children.Clear();
        bool choices = card.HasChoices && options.Length == 4;
        Options.Visibility = choices ? Visibility.Visible : Visibility.Collapsed;
        ShowAnswerText.Text = Loc.T(choices ? "Je ne sais pas" : "Afficher la réponse");
        SpaceBadge.Visibility = choices ? Visibility.Collapsed : Visibility.Visible;
        ShowAnswerButton.Background = new SolidColorBrush(choices ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        ShowAnswerButton.Foreground = new SolidColorBrush(choices ? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x11, 0x11, 0x14));
        if (!choices)
        {
            return;
        }

        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var number = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Alpha(_accent, 0x70)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(Loc.Culture),
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var font = new FontFamily("Segoe UI Variable Text");
            FrameworkElement text = StudyMath.Has(options[i])
                ? StudyMathView.Render(options[i], new MathStyle(font, 14.5, Microsoft.UI.Text.FontWeights.Normal, new SolidColorBrush(Microsoft.UI.Colors.White), 21, Selectable: false))
                : new TextBlock { Text = options[i], FontFamily = font, FontSize = 14.5, TextWrapping = TextWrapping.Wrap };
            text.VerticalAlignment = VerticalAlignment.Center;
            var row = new Grid { ColumnSpacing = 12, Margin = new Thickness(14, 10, 16, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(text, 1);
            row.Children.Add(number);
            row.Children.Add(text);
            var option = new MotionButton
            {
                Style = (Style)Application.Current.Resources["NotchCardButtonStyle"],
                Background = new SolidColorBrush(Alpha(_accent, 0x22)),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                AllowFocusOnInteraction = false,
                HoverScale = 0.015,
                PressScale = 0.03,
                HaloRestScale = 1,
                HaloOpacity = 0.06,
                Content = row,
            };
            AutomationProperties.SetName(option, StudyMath.Plain(options[i]));
            option.IsHitTestVisible = interactive;
            option.Click += (_, _) => Choose(index);
            Options.Children.Add(option);
        }
    }

    /// <summary>
    /// A choice made: the right one lights green, a wrong pick red; then the whole answer, and « Bien » (right) or
    /// « À revoir » (wrong) offered for Espace.
    /// </summary>
    private void Choose(int index)
    {
        if (_current is not { HasChoices: true } card || _revealed || _looking is not null || index < 0 || index >= _options.Length)
        {
            return;
        }

        bool right = _options[index] == card.Choice;
        _picked = index;
        MarkOptions(card, _options, index);
        Flash(right);
        StudySounds.Play(_settings, right ? StudySounds.Right : StudySounds.Wrong);
        Reveal(right ? CardRating.Good : CardRating.Again);
    }

    /// <summary>
    /// The choices answered, all still there: the right one green, <paramref name="picked"/> red if it was wrong, the
    /// others faded; a little closer together, to leave the answer its room.
    /// </summary>
    private void MarkOptions(StudyCard card, string[] options, int? picked)
    {
        for (int i = 0; i < Options.Children.Count && i < options.Length; i++)
        {
            var option = (MotionButton)Options.Children[i];
            bool right = options[i] == card.Choice;
            option.IsHitTestVisible = false;
            option.Background = new SolidColorBrush(right ? Alpha(Right, 0x40)
                : i == picked ? Alpha(Wrong, 0x40)
                : Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
            option.Opacity = right || i == picked ? 1 : 0.5;
            if (option.Content is FrameworkElement row)
            {
                row.Margin = new Thickness(14, 7, 16, 7);
            }
        }
    }

    private void OnShowAnswerClick(object sender, RoutedEventArgs e)
    {
        if (_current is { HasChoices: true } card)
        {
            _picked = -1;
            MarkOptions(card, _options, -1); // « Je ne sais pas »: the right one shown, nothing picked.
            Reveal(CardRating.Again);
        }
        else
        {
            Reveal(CardRating.Good);
        }
    }

    /// <summary>
    /// The answer under the question, its source, and the ratings with when each would bring the card back;
    /// <paramref name="suggested"/> stands out (and is what Espace gives).
    /// </summary>
    private void Reveal(CardRating suggested)
    {
        if (_current is not { } card || _revealed)
        {
            return;
        }

        _revealed = true;
        _suggested = suggested;
        ShowAnswer(card);
        ShowRatings(card);
        Arrive(AnswerArea, new Vector3(0, 8, 0));
        Arrive(RatingButtons, new Vector3(0, 8, 0), 60);

        // A long answer below the card's fold: the card scrolls to it, once laid out.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_revealed && FaceScroller.ScrollableHeight > 0)
            {
                AnswerArea.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 1 });
            }
        });
    }

    /// <summary>A card's words in their block; drawn with their formulas instead, in the same style, when they have some.</summary>
    private static void ShowText(TextBlock block, ContentPresenter math, string text)
    {
        bool formulas = StudyMath.Has(text);
        block.Text = formulas ? "" : text;
        block.Visibility = formulas ? Visibility.Collapsed : Visibility.Visible;
        math.Content = formulas
            ? StudyMathView.Render(text, new MathStyle(block.FontFamily, block.FontSize, block.FontWeight, block.Foreground, block.LineHeight, Centered: true))
            : null;
        math.Visibility = formulas ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The answer under the question, and its source.</summary>
    private void ShowAnswer(StudyCard card)
    {
        ShowText(AnswerText, AnswerMath, card.Answer);
        SourceText.Text = card.Source.Length > 0 ? Loc.F("Source : {0}", card.Source) : "";
        SourceText.Visibility = card.Source.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AnswerArea.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The ratings, with when each would bring the card back, the one offered standing out; after a choice, on one line,
    /// small, after « Ajuster : », then « Suivant ».
    /// </summary>
    private void ShowRatings(StudyCard card)
    {
        foreach (var (button, rating) in new[] { (AgainButton, CardRating.Again), (HardButton, CardRating.Hard), (GoodButton, CardRating.Good), (EasyButton, CardRating.Easy) })
        {
            button.Opacity = rating == _suggested ? 1 : 0.6;
        }

        var now = DateTimeOffset.Now;
        AgainSpan.Text = StudyScheduler.Preview(card, CardRating.Again, now);
        HardSpan.Text = StudyScheduler.Preview(card, CardRating.Hard, now);
        GoodSpan.Text = StudyScheduler.Preview(card, CardRating.Good, now);
        EasySpan.Text = StudyScheduler.Preview(card, CardRating.Easy, now);
        ShowAnswerButton.Visibility = Visibility.Collapsed;
        RatingButtons.Visibility = Visibility.Visible;
        bool choices = card.HasChoices;
        AnswerBar.Orientation = choices ? Orientation.Horizontal : Orientation.Vertical;
        AnswerBar.Spacing = choices ? 0 : 10;
        NextCardButton.Height = choices ? 38 : 44;
        NextCardButton.MinWidth = choices ? 170 : 260;
        NextCardButton.Margin = new Thickness(choices ? 10 : 0, 0, 0, 0);
        NextCardButton.Visibility = choices ? Visibility.Visible : Visibility.Collapsed;
        AdjustLabel.Visibility = choices ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, span) in new[] { (AgainButton, AgainSpan), (HardButton, HardSpan), (GoodButton, GoodSpan), (EasyButton, EasySpan) })
        {
            // Small, to adjust the rating, after a choice; full, the rating to give, after an answer shown.
            button.Height = choices ? 30 : 52;
            button.MinWidth = choices ? 0 : 118;
            button.Padding = new Thickness(choices ? 12 : 14, 0, choices ? 12 : 14, 0);
            button.FontSize = choices ? 11.5 : 12.5;
            span.Visibility = choices ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnNextCardClick(object sender, RoutedEventArgs e) => Rate(_suggested);

    private void OnRateClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int rating))
        {
            Rate((CardRating)rating);
        }
    }

    /// <summary>The card rated: it goes into the history, its next time is set and kept, the next card comes.</summary>
    private void Rate(CardRating rating)
    {
        if (_current is not { } card || !_revealed || _session is null)
        {
            return;
        }

        _history.Add(new AnsweredCard(card, _options, _picked, rating));
        if (_history.Count > HistoryKept)
        {
            _history.RemoveAt(0);
        }

        _session.Answer(card, rating, DateTimeOffset.Now);
        if (_session.Decks.FirstOrDefault(deck => deck.Id == card.DeckId) is { } deck)
        {
            _viewModel.Answered(deck, card);
        }

        CardAnswered?.Invoke(this, EventArgs.Empty);
        NextCard();
    }

    /// <summary>A flash on the card, green (right) or red (wrong), that fades: a choice answered.</summary>
    private void Flash(bool right)
    {
        var color = right ? Right : Wrong;
        FaceFlash.BorderBrush = new SolidColorBrush(Alpha(color, 0xD9));
        FaceFlash.Background = Radial(new Windows.Foundation.Point(0.5, 0.5), 0.75, 0.75, (0, Alpha(color, 0x10)), (0.7, Alpha(color, 0x1C)), (1, Alpha(color, 0x30)));
        var visual = ElementCompositionPreview.GetElementVisual(FaceFlash);
        var compositor = visual.Compositor;
        var flash = compositor.CreateScalarKeyFrameAnimation();
        flash.InsertKeyFrame(0, 0);
        flash.InsertKeyFrame(0.18f, 1, compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1)));
        flash.InsertKeyFrame(1, 0, compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.2f, 1)));
        flash.Duration = TimeSpan.FromMilliseconds(1100);
        visual.StartAnimation("Opacity", flash);
    }

    // ── The cards answered, looked back at ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A card answered in this session, looked back at (read only): its question, its choices as they were shown and
    /// marked as they were answered, its answer; « Précédente » further back, « Suivante » and « Reprendre » forward.
    /// </summary>
    private void Look(int index)
    {
        if (index < 0 || index >= _history.Count)
        {
            return;
        }

        _looking = index;
        var answered = _history[index];
        HideGrades();
        HistoryButton.Visibility = Visibility.Collapsed; // « Suivante » is there.
        DonePanel.Visibility = Visibility.Collapsed;
        FaceScroller.Visibility = Visibility.Visible;
        Counts.Visibility = Visibility.Collapsed;
        LookLabel.Text = Loc.F("Déjà répondue · {0}", Loc.T(answered.Rating switch
        {
            CardRating.Again => "À revoir",
            CardRating.Hard => "Difficile",
            CardRating.Easy => "Facile",
            _ => "Bien",
        }));
        LookLabel.Visibility = Visibility.Visible;
        PreviousButton.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
        ForwardButton.Visibility = index < _history.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        ShowQuestion(answered.Card, answered.Options, interactive: false);
        MarkOptions(answered.Card, answered.Options, answered.Picked);
        ShowAnswer(answered.Card);
        ShowAnswerButton.Visibility = RatingButtons.Visibility = NextCardButton.Visibility = Visibility.Collapsed;
        AnswerBar.Orientation = Orientation.Vertical;
        ResumeButton.Visibility = Visibility.Visible;
        FaceScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        Look((_looking ?? _history.Count) - 1);
        Arrive(Face, new Vector3(-36, 0, 0));
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (_looking is { } index && index + 1 < _history.Count)
        {
            Look(index + 1);
            Arrive(Face, new Vector3(36, 0, 0));
        }
        else
        {
            Resume();
        }
    }

    private void OnResumeClick(object sender, RoutedEventArgs e) => Resume();

    /// <summary>Back to the card being answered, as it was left.</summary>
    private void Resume()
    {
        if (_current is null)
        {
            NextCard(); // None left: as before looking back.
            return;
        }

        ShowLive();
        Arrive(Face, new Vector3(36, 0, 0));
    }

    // ── Keys ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Before the answer: 1 to 4 pick a choice (Espace or Entrée shows the answer of a card without choices). After it:
    /// Espace or Entrée give the rating offered, 1 to 4 any. ← and →: the cards answered. Échap: done.
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_grades)
        {
            if (e.Key == VirtualKey.Escape)
            {
                Show(); // Échap: back to the session, not out of it.
                e.Handled = true;
            }

            return;
        }

        int number = e.Key switch
        {
            VirtualKey.Number1 or VirtualKey.NumberPad1 => 1,
            VirtualKey.Number2 or VirtualKey.NumberPad2 => 2,
            VirtualKey.Number3 or VirtualKey.NumberPad3 => 3,
            VirtualKey.Number4 or VirtualKey.NumberPad4 => 4,
            _ => 0,
        };
        if (_looking is not null && e.Key is VirtualKey.Space or VirtualKey.Enter)
        {
            Resume();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Left when PreviousButton.Visibility == Visibility.Visible:
                OnPreviousClick(this, new RoutedEventArgs());
                break;

            case VirtualKey.Right when _looking is not null:
                OnForwardClick(this, new RoutedEventArgs());
                break;

            case VirtualKey.Space:
            case VirtualKey.Enter:
                if (_current is null)
                {
                    NextSession(); // The session done: the next one, if there are cards for it.
                }
                else if (_revealed)
                {
                    Rate(_suggested);
                }
                else if (_current is { HasChoices: false })
                {
                    Reveal(CardRating.Good);
                }

                break;

            case not VirtualKey.Escape when number > 0 && _looking is null:
                if (_revealed)
                {
                    Rate((CardRating)number);
                }
                else if (_current is { HasChoices: true })
                {
                    Choose(number - 1);
                }

                break;

            case VirtualKey.Escape:
                Escaped?.Invoke(this, EventArgs.Empty);
                break;

            default:
                return;
        }

        e.Handled = true;
    }
}
