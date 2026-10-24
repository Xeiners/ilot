namespace Ilot.Modules.Study;

/// <summary>
/// A revision session on a course (or on all of them): the cards due, the way Anki deals them. The cards being learnt
/// whose minutes are up first, then the ones due for review, then new ones (at most <see cref="NewPerSession"/>); when
/// nothing else is left, a card being learnt comes a little ahead of its time rather than leaving the user waiting.
/// </summary>
public sealed class ReviewSession(IReadOnlyList<StudyDeck> decks)
{
    /// <summary>New cards met in one session, at most: more would leave too many to learn at once.</summary>
    public const int NewPerSession = 20;

    // A card being learnt may come this much ahead of its time, once nothing else is left.
    private static readonly TimeSpan LearnAhead = TimeSpan.FromMinutes(20);

    private int _newSeen;

    public IReadOnlyList<StudyDeck> Decks { get; } = decks;

    /// <summary>Cards answered in this session.</summary>
    public int Answered { get; private set; }

    private IEnumerable<StudyCard> Cards => Decks.SelectMany(deck => deck.Cards);

    /// <summary>The next card to answer; <c>null</c> when there is none for now.</summary>
    public StudyCard? Next(DateTimeOffset now)
    {
        var cards = Cards.ToList();
        var learning = cards.Where(card => card.State is CardState.Learning or CardState.Relearning).OrderBy(card => card.Due).ToList();
        if (learning.FirstOrDefault(card => card.Due <= now) is { } learnt)
        {
            return learnt;
        }

        if (cards.Where(card => card.State == CardState.Review && card.Due <= now).OrderBy(card => card.Due).FirstOrDefault() is { } review)
        {
            return review;
        }

        if (_newSeen < NewPerSession && cards.Where(card => card.State == CardState.New).OrderBy(card => card.Id).FirstOrDefault() is { } fresh)
        {
            return fresh;
        }

        return learning.FirstOrDefault(card => card.Due <= now + LearnAhead);
    }

    /// <summary>What is left to see now: new (within the session's share), being learnt, to review.</summary>
    public (int New, int Learning, int Review) Remaining(DateTimeOffset now)
    {
        var cards = Cards.ToList();
        return (
            Math.Min(cards.Count(card => card.State == CardState.New), NewPerSession - _newSeen),
            cards.Count(card => card.State is CardState.Learning or CardState.Relearning && card.Due <= now + LearnAhead),
            cards.Count(card => card.State == CardState.Review && card.Due <= now));
    }

    /// <summary>When the next card will be due, once there is none now; <c>null</c> when the course has none at all.</summary>
    public DateTimeOffset? NextDue() => Cards.Where(card => card.State != CardState.New).Select(card => (DateTimeOffset?)card.Due).Min();

    /// <summary><paramref name="card"/> answered <paramref name="rating"/>.</summary>
    public void Answer(StudyCard card, CardRating rating, DateTimeOffset now)
    {
        if (card.State == CardState.New)
        {
            _newSeen++;
        }

        StudyScheduler.Rate(card, rating, now);
        Answered++;
    }
}
