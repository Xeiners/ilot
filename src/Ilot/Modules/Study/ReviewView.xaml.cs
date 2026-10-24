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
        NewLabel.Text = " " + Loc.T("nouvelles");
        LearningLabel.Text = " " + Loc.T("à apprendre");
        ReviewLabel.Text = " " + Loc.T("à revoir");
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

    /// <summary>The same revision, with more cards (new ones made): what was answered stays.</summary>
    public void Extend(ReviewSession session)
    {
        _session = session;
        if (_current is null && _looking is null)
        {
            NextCard();
        }
    }

    /// <summary>The courses' cards changed (new ones came): nothing left to answer, the next one comes.</summary>
    public void CardsChanged()
    {
        if (_session is not null && _current is null && _looking is null)
        {
            NextCard();
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
        if (_session is not null)
        {
            Show(); // Its choices, in the colour.
        }
    }

    // ── The card being answered ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The next card due, its question only; or, none left, when the next one will be.</summary>
    private void NextCard()
    {
        if (_session is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        _current = _session.Next(now);
        _revealed = false;
        UpdateCounts();

        if (_current is not { } card)
        {
            FaceScroller.Visibility = Visibility.Collapsed;
            ShowAnswerButton.Visibility = RatingButtons.Visibility = NextCardButton.Visibility = Visibility.Collapsed;
            LookLabel.Visibility = ForwardButton.Visibility = ResumeButton.Visibility = Visibility.Collapsed;
            PreviousButton.Visibility = _history.Count > 0 ? Visibility.Visible : Visibility.Collapsed; // The last ones, still.
            _looking = null;
            DonePanel.Visibility = Visibility.Visible;
            DoneTitle.Text = _session.Answered > 0 ? Loc.T("Bravo, c'est fini pour l'instant") : Loc.T("Rien à revoir pour l'instant");
            if (_session.Answered > 0 && !_cheered)
            {
                _cheered = true; // Once a session.
                StudySounds.Play(_settings, StudySounds.Done);
            }

            DoneText.Text = _session.NextDue() is { } next && next > now
                ? Loc.F("Prochaine carte {0}. Pour aller plus loin : « Nouvelles cartes ».", StudyScheduler.When(next, now))
                : Loc.T("Pour aller plus loin : « Nouvelles cartes ».");
            Arrive(DonePanel, new Vector3(0, 8, 0));
            return;
        }

        _picked = null;
        _looking = null;
        _options = card.HasChoices ? [card.Choice, .. card.Decoys] : [];
        Random.Shared.Shuffle(_options);
        ShowLive();
        Arrive(Face, new Vector3(36, 0, 0));
    }

    /// <summary>What is left: new (within the session's share), being learnt, to review.</summary>
    private void UpdateCounts()
    {
        if (_session is null)
        {
            return;
        }

        var (fresh, learning, review) = _session.Remaining(DateTimeOffset.Now);
        NewCount.Text = fresh.ToString(Loc.Culture);
        LearningCount.Text = learning.ToString(Loc.Culture);
        ReviewCount.Text = review.ToString(Loc.Culture);
        Counts.Visibility = _current is null ? Visibility.Collapsed : Visibility.Visible;
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
        QuestionText.Text = card.Question;
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
            var text = new TextBlock
            {
                Text = options[i],
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = 14.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
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
            AutomationProperties.SetName(option, options[i]);
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

    /// <summary>The answer under the question, and its source.</summary>
    private void ShowAnswer(StudyCard card)
    {
        AnswerText.Text = card.Answer;
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
                if (_revealed)
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
