namespace Ilot.Modules.Study;

/// <summary>
/// A revision session: its cards drawn when it starts, shuffled, each answered once, in turn (« 3 sur 40 »); then its
/// grade (<see cref="Score"/>). On a course, every card it has (made since: in the next session, with the others); on a
/// section's courses or on all of them, those due (as Anki has them). A card answered when it was due moves on in its
/// learning (<see cref="StudyScheduler"/>); one answered again before its time counts for the grade only, its next time
/// left as it was.
/// </summary>
public sealed class ReviewSession
{
    // A card being learnt may come this much ahead of its time (and still move on in its learning).
    private static readonly TimeSpan LearnAhead = TimeSpan.FromMinutes(20);

    private readonly List<StudyCard> _cards;
    private readonly List<(StudyCard Card, CardRating Rating)> _answers = [];

    /// <param name="decks">The courses revised.</param>
    /// <param name="scope">What its grade is kept with (see <see cref="ScopeOf"/>).</param>
    /// <param name="whole">Every card of the courses (a course's), rather than those due.</param>
    public ReviewSession(IReadOnlyList<StudyDeck> decks, string scope, bool whole)
    {
        Decks = decks;
        Scope = scope;
        Whole = whole;
        _cards = Draw(decks, whole, DateTimeOffset.Now);
    }

    public IReadOnlyList<StudyDeck> Decks { get; }

    /// <summary>What it is on: « deck:12 » (a course), « section:Biologie » (a section's courses), « all ».</summary>
    public string Scope { get; }

    /// <summary>Every card of its courses, not only those due.</summary>
    public bool Whole { get; }

    /// <summary>What a session on <paramref name="deck"/> (or, <c>null</c>, on a section's courses, or on all) is on.</summary>
    public static string ScopeOf(StudyDeck? deck, string? section) =>
        deck is not null ? $"deck:{deck.Id}" : section is { Length: > 0 } ? $"section:{section}" : "all";

    /// <summary>The same courses, drawn again (shuffled again, the cards made meanwhile in).</summary>
    public ReviewSession Again() => new(Decks, Scope, Whole);

    /// <summary>Its cards (answered, and to answer).</summary>
    public int Total => _cards.Count;

    /// <summary>Cards answered so far.</summary>
    public int Answered => _answers.Count;

    /// <summary>All its cards answered.</summary>
    public bool IsFinished => Total > 0 && Answered >= Total;

    /// <summary>How it went, so far.</summary>
    public SessionScore Score => _answers.Aggregate(default(SessionScore), (score, answer) => score.With(answer.Rating));

    /// <summary>How the cards of the course <paramref name="deckId"/> went.</summary>
    public SessionScore ScoreOf(long deckId) =>
        _answers.Where(answer => answer.Card.DeckId == deckId).Aggregate(default(SessionScore), (score, answer) => score.With(answer.Rating));

    /// <summary>The next card to answer; <c>null</c> once all are. A card deleted meanwhile is left out.</summary>
    public StudyCard? Next()
    {
        while (Answered < _cards.Count)
        {
            var card = _cards[Answered];
            if (Decks.Any(deck => deck.Id == card.DeckId && deck.Cards.Contains(card)))
            {
                return card;
            }

            _cards.RemoveAt(Answered);
        }

        return null;
    }

    /// <summary>The cards a session started now would have (0: none to revise).</summary>
    public int Following(DateTimeOffset now) => Draw(Decks, Whole, now).Count;

    /// <summary>When the next card will be due, once there is none now; <c>null</c> when the course has none at all.</summary>
    public DateTimeOffset? NextDue() => Decks.SelectMany(deck => deck.Cards).Where(card => card.State != CardState.New).Select(card => (DateTimeOffset?)card.Due).Min();

    /// <summary><paramref name="card"/> answered <paramref name="rating"/>: it moves on in its learning if it was due.</summary>
    public void Answer(StudyCard card, CardRating rating, DateTimeOffset now)
    {
        if (IsDue(card, now))
        {
            StudyScheduler.Rate(card, rating, now);
        }

        _answers.Add((card, rating));
    }

    /// <summary>To be seen now: new, being learnt (its minutes nearly up), or due for review.</summary>
    private static bool IsDue(StudyCard card, DateTimeOffset now) => card.State switch
    {
        CardState.New => true,
        CardState.Learning or CardState.Relearning => card.Due <= now + LearnAhead,
        _ => card.Due <= now,
    };

    private static List<StudyCard> Draw(IReadOnlyList<StudyDeck> decks, bool whole, DateTimeOffset now)
    {
        var cards = decks.SelectMany(deck => deck.Cards).Where(card => whole || IsDue(card, now)).ToArray();
        Random.Shared.Shuffle(cards);
        return [.. cards];
    }
}
