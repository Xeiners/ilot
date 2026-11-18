namespace Ilot.Modules.Study;

/// <summary>How a revision session went: its cards, by how well each came.</summary>
public readonly record struct SessionScore(int Again, int Hard, int Good, int Easy)
{
    public int Cards => Again + Hard + Good + Easy;

    /// <summary>Known: « Bien » or « Facile ».</summary>
    public int Right => Good + Easy;

    /// <summary>Its grade out of 20: a card known counts whole, « Difficile » half, « À revoir » nothing.</summary>
    public double Grade => Cards == 0 ? 0 : (Good + Easy + Hard * 0.5) * 20 / Cards;

    /// <summary>One card more, rated <paramref name="rating"/>.</summary>
    public SessionScore With(CardRating rating) => rating switch
    {
        CardRating.Again => this with { Again = Again + 1 },
        CardRating.Hard => this with { Hard = Hard + 1 },
        CardRating.Easy => this with { Easy = Easy + 1 },
        _ => this with { Good = Good + 1 },
    };

    public static SessionScore operator +(SessionScore a, SessionScore b) =>
        new(a.Again + b.Again, a.Hard + b.Hard, a.Good + b.Good, a.Easy + b.Easy);
}

/// <summary>
/// A course's share of a finished session, as kept (a session on several courses gives one for each): which session, on
/// what (<see cref="ReviewSession.Scope"/>), when, how it went.
/// </summary>
public sealed record StudyResult(long Session, long DeckId, string Scope, DateTimeOffset Finished, SessionScore Score);

/// <summary>A session in a history: when it ended, how it went.</summary>
public sealed record SessionPoint(DateTimeOffset Finished, SessionScore Score);
