using CommunityToolkit.Mvvm.ComponentModel;

namespace MyNotch.Modules.Study;

/// <summary>Where a card is in its learning (see <see cref="StudyScheduler"/>).</summary>
public enum CardState
{
    /// <summary>Never seen.</summary>
    New = 0,

    /// <summary>Being learnt: seen again within minutes, a step at a time.</summary>
    Learning = 1,

    /// <summary>Known: seen again after days, more of them each time.</summary>
    Review = 2,

    /// <summary>Known, then forgotten: learnt again within minutes before going back to its days.</summary>
    Relearning = 3,
}

/// <summary>How well the answer came, once shown.</summary>
public enum CardRating
{
    Again = 1,
    Hard = 2,
    Good = 3,
    Easy = 4,
}

/// <summary>
/// A flashcard of a course: a precise question, its exact answer, where in the course it comes from, and where it is in
/// its learning (its state, when it is due, its interval and ease, as <see cref="StudyScheduler"/> keeps them).
/// </summary>
public sealed class StudyCard : ObservableObject
{
    public StudyCard(long id, long deckId, string question, string answer, string source, string chapter, DateTimeOffset created)
    {
        Id = id;
        DeckId = deckId;
        _question = question;
        _answer = answer;
        Source = source;
        Chapter = chapter;
        Created = created;
        Due = created;
    }

    public long Id { get; }

    public long DeckId { get; }

    /// <summary>Where in the course it comes from: « p. 12 », « diapo 4 ».</summary>
    public string Source { get; }

    /// <summary>The chapter it belongs to, as the course's outline names it.</summary>
    public string Chapter { get; }

    public DateTimeOffset Created { get; }

    private string _question;

    public string Question
    {
        get => _question;
        set => SetProperty(ref _question, value);
    }

    private string _answer;

    public string Answer
    {
        get => _answer;
        set => SetProperty(ref _answer, value);
    }

    /// <summary>
    /// The right answer in short, as one of four choices (« Le nerf axillaire »); empty: none yet (the answer is shown,
    /// then rated). See <see cref="Decoys"/>.
    /// </summary>
    public string Choice { get; set; } = "";

    /// <summary>Three wrong choices, plausible (the course's own terms), clearly wrong by the course.</summary>
    public IReadOnlyList<string> Decoys { get; set; } = [];

    /// <summary>It can be answered as a multiple choice: its answer and three decoys.</summary>
    public bool HasChoices => Choice.Length > 0 && Decoys.Count == 3;

    public CardState State { get; set; }

    /// <summary>The learning step it is at (Learning, Relearning).</summary>
    public int Step { get; set; }

    /// <summary>When it is to be seen again.</summary>
    public DateTimeOffset Due { get; set; }

    /// <summary>Its interval once known, in days.</summary>
    public int IntervalDays { get; set; }

    /// <summary>How much its interval grows at each « Bien » (2.5: two and a half times).</summary>
    public double Ease { get; set; } = StudyScheduler.StartingEase;

    /// <summary>How many times it was answered, and forgotten once known.</summary>
    public int Reps { get; set; }

    public int Lapses { get; set; }

    /// <summary>To be seen now (or seen for the first time).</summary>
    public bool IsDue(DateTimeOffset now) => State == CardState.New || Due <= now;

    /// <summary>Known well: in review, with an interval of three weeks or more.</summary>
    public bool IsMastered => State == CardState.Review && IntervalDays >= 21;
}
