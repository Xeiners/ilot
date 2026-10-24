using MyNotch.Localization;

namespace MyNotch.Modules.Study;

/// <summary>
/// Spaced repetition, the way Anki does it (SM-2): a new card is learnt in short steps (1 min, then 10 min), then comes
/// back after a day, and each time it is known its interval grows by its ease (2.5 to start); forgotten, it is learnt
/// again (10 min) and comes back sooner, its ease lowered. « Difficile » and « Facile » slow it down or push it further.
/// A day starts at 4 a.m.: a card due « tomorrow » is there from the morning, not at the hour it was last seen.
/// </summary>
public static class StudyScheduler
{
    public const double StartingEase = 2.5;
    private const double LeastEase = 1.3;
    private const double EasyBonus = 1.3;
    private const double HardFactor = 1.2;
    private const int GraduatingDays = 1;
    private const int EasyDays = 4;
    private const int LongestDays = 365;
    private const int DayStartsAt = 4;

    private static readonly TimeSpan[] LearningSteps = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)];
    private static readonly TimeSpan RelearningStep = TimeSpan.FromMinutes(10);

    /// <summary>The card answered <paramref name="rating"/> at <paramref name="now"/>: when it comes back, and how.</summary>
    public static void Rate(StudyCard card, CardRating rating, DateTimeOffset now)
    {
        card.Reps++;
        switch (card.State)
        {
            case CardState.New:
            case CardState.Learning:
                Learn(card, rating, now);
                break;

            case CardState.Relearning:
                Relearn(card, rating, now);
                break;

            default:
                Review(card, rating, now);
                break;
        }
    }

    /// <summary>How long until the card comes back if answered <paramref name="rating"/>: « 1 min », « 10 min », « 4 j ».</summary>
    public static string Preview(StudyCard card, CardRating rating, DateTimeOffset now)
    {
        var trial = new StudyCard(0, 0, "", "", "", "", now)
        {
            State = card.State,
            Step = card.Step,
            Due = card.Due,
            IntervalDays = card.IntervalDays,
            Ease = card.Ease,
        };
        Rate(trial, rating, now);
        return Span(trial.Due - now);
    }

    /// <summary>A delay as the rating buttons show it.</summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return Loc.T("< 1 min");
        }

        if (span < TimeSpan.FromHours(1))
        {
            return Loc.F("{0} min", Math.Round(span.TotalMinutes));
        }

        if (span < TimeSpan.FromHours(20))
        {
            return Loc.F("{0} h", Math.Round(span.TotalHours));
        }

        double days = Math.Max(1, Math.Round(span.TotalDays));
        return days < 31 ? Loc.F("{0} j", days)
            : days < 365 ? Loc.F("{0} mois", Math.Round(days / 30.4, 1).ToString("0.#", Loc.Culture))
            : Loc.F("{0} an", Math.Round(days / 365, 1).ToString("0.#", Loc.Culture));
    }

    /// <summary>When something due comes: « dans 10 min », « demain », « dans 3 jours ».</summary>
    public static string When(DateTimeOffset due, DateTimeOffset now)
    {
        var span = due - now;
        if (span < TimeSpan.FromHours(1))
        {
            return Loc.F("dans {0} min", Math.Max(1, Math.Round(span.TotalMinutes)));
        }

        if (span < TimeSpan.FromHours(20))
        {
            return Loc.F("dans {0} h", Math.Round(span.TotalHours));
        }

        int days = (due.LocalDateTime.Date - now.LocalDateTime.Date).Days;
        return days <= 1 ? Loc.T("demain") : Loc.F("dans {0} jours", days);
    }

    private static void Learn(StudyCard card, CardRating rating, DateTimeOffset now)
    {
        card.State = CardState.Learning;
        switch (rating)
        {
            case CardRating.Again:
                card.Step = 0;
                card.Due = now + LearningSteps[0];
                break;

            case CardRating.Hard:
                // The step again, a little longer (between the first two, at the first).
                var step = LearningSteps[Math.Min(card.Step, LearningSteps.Length - 1)];
                card.Due = now + (card.Step == 0 ? (LearningSteps[0] + LearningSteps[1]) / 2 : step);
                break;

            case CardRating.Good:
                if (++card.Step >= LearningSteps.Length)
                {
                    Graduate(card, GraduatingDays, now);
                }
                else
                {
                    card.Due = now + LearningSteps[card.Step];
                }

                break;

            case CardRating.Easy:
                Graduate(card, EasyDays, now);
                break;
        }
    }

    private static void Relearn(StudyCard card, CardRating rating, DateTimeOffset now)
    {
        switch (rating)
        {
            case CardRating.Again:
                card.Due = now + RelearningStep;
                break;

            case CardRating.Hard:
                card.Due = now + RelearningStep * 1.5;
                break;

            default:
                card.State = CardState.Review;
                card.Step = 0;
                card.Due = DayStart(now, Math.Max(1, card.IntervalDays));
                break;
        }
    }

    private static void Review(StudyCard card, CardRating rating, DateTimeOffset now)
    {
        int interval = Math.Max(1, card.IntervalDays);
        switch (rating)
        {
            case CardRating.Again:
                card.Lapses++;
                card.Ease = Math.Max(LeastEase, card.Ease - 0.2);
                card.IntervalDays = Math.Max(1, (int)Math.Round(interval * 0.2, MidpointRounding.AwayFromZero));
                card.State = CardState.Relearning;
                card.Step = 0;
                card.Due = now + RelearningStep;
                return;

            case CardRating.Hard:
                card.Ease = Math.Max(LeastEase, card.Ease - 0.15);
                card.IntervalDays = Grow(interval, interval * HardFactor);
                break;

            case CardRating.Good:
                card.IntervalDays = Grow(interval, interval * card.Ease);
                break;

            case CardRating.Easy:
                card.IntervalDays = Grow(interval, interval * card.Ease * EasyBonus);
                card.Ease += 0.15;
                break;
        }

        card.Due = DayStart(now, card.IntervalDays);
    }

    private static void Graduate(StudyCard card, int days, DateTimeOffset now)
    {
        card.State = CardState.Review;
        card.Step = 0;
        card.IntervalDays = days;
        card.Due = DayStart(now, days);
    }

    /// <summary>At least a day more than before, at most a year.</summary>
    private static int Grow(int interval, double grown) => Math.Min(LongestDays, Math.Max(interval + 1, (int)Math.Round(grown, MidpointRounding.AwayFromZero)));

    /// <summary>The start (4 a.m., local) of the day <paramref name="days"/> after the one <paramref name="now"/> is in.</summary>
    private static DateTimeOffset DayStart(DateTimeOffset now, int days)
    {
        var local = now.ToLocalTime();
        var today = local.Hour < DayStartsAt ? local.Date.AddDays(-1) : local.Date;
        var day = today.AddDays(days).AddHours(DayStartsAt);
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }
}
