using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ilot.Modules.Media.Lyrics;

/// <summary>A track the lyrics database knows: what it says of it, and its synced lyrics if it has some.</summary>
public sealed record LyricsCandidate(long Id, string Title, string Artist, string Album, double? Duration, string? SyncedLyrics);

/// <summary>The candidate chosen, its score out of 100, and how the track was read to choose it.</summary>
public sealed record LyricsMatch(LyricsCandidate Candidate, double Score, LyricsMatcher.Reading Reading);

/// <summary>
/// Which lyrics belong to the track playing, from what the player tells Windows (title, artist, album, duration). No
/// first-come choice: the track is read every way it can be (a song « Title » by « Artist », or a video « Artist - Title »,
/// « Title | Artist », « Artist「Title」 »), searched for several ways, and every synced candidate is scored out of 100:
/// <list type="bullet">
/// <item>the title (45): the same words, or nearly (accents, case, punctuation, typing apart), once both are rid of what
/// players add (« (Official Video) », « - Remastered 2011 », « (feat. X) »);</item>
/// <item>the artist (35): the best match between the artists on either side (« Drake, Future », « AC/DC » and « ACDC »);</item>
/// <item>the duration (15): the closer the better, and never more than 8 s apart (another version would drift out of
/// sync);</item>
/// <item>the album: a bonus when it matches;</item>
/// <item>the version: live, remix, acoustic… on one side only costs 30 points (a remix is not the original: refused).</item>
/// </list>
/// The best one is taken if it is good enough (the title and the artist close, 72 at least; 85 while the duration is not
/// known yet, as the player may not have told it): no lyrics rather than wrong ones.
/// </summary>
public static partial class LyricsMatcher
{
    /// <summary>Good enough to stop searching.</summary>
    public const double Sure = 90;

    private const double Accept = 72;
    private const double AcceptBlind = 85; // Without a duration to tell versions apart.
    private const double MostGap = 8;
    private const double MostGapAlone = 3; // Without the artist to confirm it.
    private const double VersionPenalty = 30;

    /// <summary>One way of reading the track: its title, and its artists (the main one first; none when unknown).</summary>
    public sealed record Reading(string Title, IReadOnlyList<string> Artists, IReadOnlySet<string> Versions)
    {
        public string? Artist => Artists.Count > 0 ? Artists[0] : null;
    }

    /// <summary>
    /// The ways <paramref name="query"/> can be read: as given (title cleaned, its « feat. » among the artists), and, when
    /// its title holds an artist too (a video's), « Artist - Title », « Title | Artist », « Artist「Title」 », « Artist
    /// "Title" »; and last, for a video (its title says so, or a browser plays it), the title alone, whoever sings it (its
    /// « artist » may be only its channel).
    /// </summary>
    public static IReadOnlyList<Reading> Readings(LyricsQuery query)
    {
        var title = Clean(query.Title);
        var artists = Artists(query.Artist);
        var versions = new HashSet<string>(title.Versions);
        versions.UnionWith(AlbumVersions(query.Album));
        var readings = new List<Reading>();
        var titles = new List<string> { title.Core }; // What may be the song's title, for the title alone.
        Add(title.Core, [.. artists, .. title.Featured]);

        // A video's title: « Artist - Title », or the other way round (the scoring tells which).
        if (Separator().Match(title.Core) is { Success: true } separator)
        {
            string left = title.Core[..separator.Index].Trim();
            string right = title.Core[(separator.Index + separator.Length)..].Trim();
            var rightTitle = Clean(right);
            versions.UnionWith(rightTitle.Versions);
            Add(rightTitle.Core, [.. Artists(left), .. rightTitle.Featured, .. artists]);
            Add(Clean(left).Core, [.. Artists(right), .. artists]);
            titles.Add(Clean(left).Core); // « Song - Anime » as often as « Artist - Song ».
            titles.Add(rightTitle.Core);
        }

        if (Quoted().Match(title.Core) is { Success: true } quoted)
        {
            Add(Clean(quoted.Groups["title"].Value).Core, [.. Artists(quoted.Groups["artist"].Value), .. artists]);
            titles.Add(Clean(quoted.Groups["title"].Value).Core);
        }

        // Last, for a video, the title alone: its « artist » is often only the channel that posted it (an AMV, a lyrics
        // video), and its title does not say who sings. Scored strictly (see Score). Never for a music app, whose artist is
        // the singer: another song of the same name and length would do otherwise.
        if (artists.Count == 0 || title.Video || FromBrowser(query.Source))
        {
            foreach (string alone in titles)
            {
                Add(alone, []);
            }
        }

        return readings;

        void Add(string core, List<string> who)
        {
            var distinct = who.Where(name => Key(name).Length > 0).DistinctBy(Key).ToList();
            if (Key(core).Length > 0 && !readings.Any(reading => Key(reading.Title) == Key(core) && reading.Artist == distinct.FirstOrDefault()))
            {
                readings.Add(new Reading(core, distinct, versions));
            }
        }
    }

    /// <summary>
    /// The best candidate for <paramref name="query"/> if one is good enough; <c>null</c> if none is. A match on the title
    /// alone (no artist to confirm it) only counts when that title is a single artist's among the candidates: twenty
    /// songs are called « Someone Like You », and the one playing may not even be in the database.
    /// </summary>
    public static LyricsMatch? Best(LyricsQuery query, IReadOnlyList<Reading> readings, IEnumerable<LyricsCandidate> candidates)
    {
        var all = candidates.ToList();
        LyricsMatch? best = null;
        foreach (var candidate in all)
        {
            if (string.IsNullOrWhiteSpace(candidate.SyncedLyrics))
            {
                continue;
            }

            foreach (var reading in readings)
            {
                if (Score(query, reading, candidate) is { } score && (best is null || score > best.Score)
                    && (reading.Artists.Count > 0 || SingleArtist(reading.Title, all)))
                {
                    best = new LyricsMatch(candidate, score, reading);
                }
            }
        }

        return best;
    }

    /// <summary>Whether every candidate titled <paramref name="title"/> (lyrics synced or not) is by the same artist.</summary>
    private static bool SingleArtist(string title, IReadOnlyList<LyricsCandidate> candidates)
    {
        string key = Key(title);
        return candidates
            .Where(candidate => Similarity(key, Key(Clean(candidate.Title).Core)) >= 0.93)
            .Select(candidate => Key(Artists(candidate.Artist) is { Count: > 1 } names ? names[1] : candidate.Artist))
            .Distinct()
            .Count() <= 1;
    }

    /// <summary>
    /// The score of <paramref name="candidate"/> for <paramref name="reading"/>, out of 100; <c>null</c> when it cannot be
    /// the track (title or artist too far, duration too far apart, below the bar).
    /// </summary>
    public static double? Score(LyricsQuery query, Reading reading, LyricsCandidate candidate)
    {
        var title = Clean(candidate.Title);
        double titleScore = Similarity(Key(reading.Title), Key(title.Core));
        if (titleScore < 0.75)
        {
            return null;
        }

        double weights = 45;
        double total = 45 * titleScore;

        double? artistScore = reading.Artists.Count == 0 ? null : ArtistSimilarity(reading.Artists, [.. Artists(candidate.Artist), .. title.Featured], candidate.Artist);
        if (artistScore is { } artist)
        {
            if (artist < 0.6)
            {
                return null;
            }

            weights += 35;
            total += 35 * artist;
        }

        bool timed = query.Duration > TimeSpan.Zero && candidate.Duration is > 0;
        if (timed)
        {
            double gap = Math.Abs(candidate.Duration!.Value - query.Duration.TotalSeconds);
            if (gap > MostGap)
            {
                return null;
            }

            weights += 15;
            total += 15 * (gap <= 1.5 ? 1 : 1 - (gap - 1.5) / (MostGap - 1.5) * 0.7);
        }

        double score = total / weights * 100;

        // The album: a bonus when it matches (a single, a compilation are the same song all the same).
        if (query.Album.Length > 0 && candidate.Album.Length > 0)
        {
            double album = Similarity(Key(Clean(query.Album).Core), Key(Clean(candidate.Album).Core));
            score += album >= 0.8 ? 5 : 0;
        }

        // Another version (live, remix…): another song to sing along.
        var versions = new HashSet<string>(title.Versions);
        versions.UnionWith(AlbumVersions(candidate.Album));
        versions.SymmetricExceptWith(reading.Versions);
        score -= Math.Min(2, versions.Count) * VersionPenalty;

        // Without the artist (none told, or only a channel's name): a near-identical title, its duration within 3 s, and
        // a little under any candidate its artist confirms.
        if (artistScore is null)
        {
            if (titleScore < 0.93 || !timed || Math.Abs(candidate.Duration!.Value - query.Duration.TotalSeconds) > MostGapAlone)
            {
                return null;
            }

            score -= 5;
        }

        return score >= (timed ? Accept : AcceptBlind) ? Math.Min(score, 100) : null;
    }

    /// <summary>
    /// What to search the database for, best first: each reading's title with its main artist (the first two readings),
    /// then, should they find nothing good enough, the words of title and artist together (each once), and the titles
    /// alone (a video's possible titles, else the second reading's): three at most.
    /// </summary>
    public static (IReadOnlyList<(string Title, string? Artist)> First, IReadOnlyList<string> Wider) Searches(IReadOnlyList<Reading> readings)
    {
        var named = readings.Where(reading => reading.Artist is not null).ToList();
        var first = (named.Count > 0 ? named : readings).Take(2).Select(reading => (reading.Title, reading.Artist)).Distinct().ToList();
        var wider = new List<string>();
        if (readings.Count > 0)
        {
            var main = readings[0];
            wider.Add(string.Join(' ', Key($"{main.Artist} {main.Title}").Split(' ').Distinct()));
            var alone = readings.Where(reading => reading.Artist is null).Select(reading => Key(reading.Title)).ToList();
            wider.AddRange(alone.Count > 0 ? alone : [Key(readings.Count > 1 ? readings[1].Title : main.Title)]);
        }

        return (first, wider.Where(words => words.Length > 0).Distinct().Take(3).ToList());
    }

    // ── Titles ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A title rid of what players add, the versions it names (live, remix…), and the artists it features.</summary>
    public sealed record CleanTitle(string Core, IReadOnlySet<string> Versions, IReadOnlyList<string> Featured, bool Video = false);

    /// <summary>
    /// « Get Lucky (feat. Pharrell Williams) - Radio Edit » → « Get Lucky », featuring Pharrell Williams. Everything in
    /// brackets goes (on both sides, so « (I Can't Get No) Satisfaction » still matches itself), and an ending after « - »
    /// that says a version, a remaster or « From "…" »; the versions they name are kept apart.
    /// </summary>
    public static CleanTitle Clean(string title)
    {
        var versions = new HashSet<string>();
        var featured = new List<string>();
        bool video = false;
        string core = title.Replace('\u00A0', ' ').Trim().Trim('"', '\'', '“', '”', '«', '»').Trim();

        core = Brackets().Replace(core, match =>
        {
            string inside = match.Groups["inside"].Value.Trim();
            if (FeaturingStart().Match(inside) is { Success: true } feat)
            {
                featured.AddRange(Artists(inside[feat.Length..]));
            }
            else
            {
                versions.UnionWith(Versions(inside));
                video |= VideoWords().IsMatch(inside);
            }

            return " ";
        });

        // 「AMV」, 『MV』: what the video is, not a title in quotes (« Artist「Title」 » is, see Readings).
        core = Decorated().Replace(core, match =>
        {
            versions.UnionWith(Versions(match.Groups["inside"].Value));
            video = true;
            return " ";
        });

        // « - Anime MV », « | Lyrics », « AMV - »: what the video is, at either end; the dashes left around it, one.
        core = Dashes().Replace(core, " - ");
        while (true)
        {
            var decoration = DecorationEnd().Match(core) is { Success: true } end ? end : DecorationStart().Match(core);
            if (!decoration.Success)
            {
                break;
            }

            versions.UnionWith(Versions(decoration.Value));
            core = core.Remove(decoration.Index, decoration.Length);
            video = true;
        }

        // « - Remastered 2011 », « - Live at Wembley », « - From "Frozen" », « - feat. X »: an ending, not a title.
        while (DashEnding().Match(core) is { Success: true } ending)
        {
            string tail = ending.Groups["tail"].Value;
            if (FeaturingStart().Match(tail) is { Success: true } feat)
            {
                featured.AddRange(Artists(tail[feat.Length..]));
            }

            versions.UnionWith(Versions(tail));
            core = core[..ending.Index];
        }

        if (InlineFeaturing().Match(core) is { Success: true } inline)
        {
            featured.AddRange(Artists(inline.Groups["who"].Value));
            core = core[..inline.Index];
        }

        core = Spaces().Replace(core, " ").Trim().Trim('-', '–', '—', '|', ':', ' ');
        if (core.Length == 0)
        {
            core = Spaces().Replace(title.Replace('(', ' ').Replace(')', ' ').Replace('[', ' ').Replace(']', ' '), " ").Trim();
        }

        return new CleanTitle(core, versions, featured, video);
    }

    /// <summary>The versions a bracket or an ending names: live, remix, acoustic…</summary>
    private static IEnumerable<string> Versions(string text)
    {
        string key = " " + Key(text) + " ";
        foreach (var (name, words) in VersionWords)
        {
            if (words.Any(word => key.Contains($" {word} ", StringComparison.Ordinal)))
            {
                yield return name;
            }
        }
    }

    /// <summary>The versions an album says by its name (« Live at Wembley », « MTV Unplugged »).</summary>
    private static IEnumerable<string> AlbumVersions(string album)
    {
        if (album.Length == 0)
        {
            return [];
        }

        var found = new HashSet<string>(Clean(album).Versions);
        string key = Key(album);
        if (LiveAlbum().IsMatch(key))
        {
            found.Add("live");
        }

        if (key.Contains("unplugged", StringComparison.Ordinal))
        {
            found.Add("acoustic");
        }

        return found;
    }

    private static readonly (string Name, string[] Words)[] VersionWords =
    [
        ("live", ["live", "en direct", "en vivo", "ao vivo", "concert"]),
        ("remix", ["remix", "rmx", "rework", "bootleg", "vip mix", "club mix", "dub mix"]),
        ("acoustic", ["acoustic", "acoustique", "acustico", "unplugged", "stripped"]),
        ("instrumental", ["instrumental", "karaoke", "backing track"]),
        ("demo", ["demo"]),
        ("cover", ["cover", "reprise"]),
        ("speed", ["sped up", "speed up", "spedup", "nightcore", "slowed", "reverb", "8d"]),
        ("orchestral", ["orchestral", "symphonic", "piano version"]),
        ("acapella", ["acapella", "a cappella", "a capella"]),
    ];

    /// <summary>A browser plays it (YouTube and the like): its « artist » is a channel's name, perhaps not the singer.</summary>
    public static bool FromBrowser(string source) => Browser().IsMatch(source);

    // ── Artists ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The artists of a credit: « Drake, Future », « Simon & Garfunkel », « A feat. B », « A x B », « A / B »; a channel's
    /// name rid of « - Topic » and « VEVO ». The whole credit too, first (« Tyler, The Creator » is one artist).
    /// </summary>
    public static List<string> Artists(string credit)
    {
        string cleaned = Channel().Replace(credit.Trim(), "").Trim();
        if (cleaned.Length == 0)
        {
            return [];
        }

        var names = new List<string> { cleaned };
        names.AddRange(Credit().Split(cleaned).Select(name => name.Trim()).Where(name => name.Length > 0));
        return names.DistinctBy(Key).ToList();
    }

    /// <summary>
    /// How close two credits are: the best pair of artists, a little less when it is only a guest; the whole credits, as
    /// written or letters only (« AC/DC », « ACDC »).
    /// </summary>
    private static double ArtistSimilarity(IReadOnlyList<string> wanted, IReadOnlyList<string> found, string foundCredit)
    {
        double best = Similarity(Key(string.Join(" ", wanted.Take(1))), Key(foundCredit));
        for (int i = 0; i < wanted.Count; i++)
        {
            foreach (string name in found)
            {
                double similarity = Similarity(Key(wanted[i]), Key(name));
                best = Math.Max(best, i <= 1 ? similarity : similarity * 0.9); // The credit, its first name: in full.
            }
        }

        return best;
    }

    // ── Comparing ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A text as compared: lower case, no accents, « & » as « and », punctuation as spaces; letters of every script kept.
    /// </summary>
    public static string Key(string text)
    {
        var key = new StringBuilder(text.Length);
        foreach (char c in text.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                key.Append(char.ToLowerInvariant(c));
            }
            else if (c is '\'' or '’' or '`' or '´')
            {
                // « don't » and « dont » alike.
            }
            else if (key.Length > 0 && key[^1] != ' ')
            {
                key.Append(' ');
            }
        }

        return key.ToString().Trim().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// How alike two keys are, 0 to 1: the same letters with or without spaces; the same words in another order or one
    /// more; the same text with a typo; one inside the other (« Song » in « Song Part II » less so).
    /// </summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (a == b)
        {
            return 1;
        }

        string compactA = a.Replace(" ", "", StringComparison.Ordinal);
        string compactB = b.Replace(" ", "", StringComparison.Ordinal);
        if (compactA == compactB)
        {
            return 0.97;
        }

        double edits = 1 - Distance(compactA, compactB) / (double)Math.Max(compactA.Length, compactB.Length);

        var wordsA = a.Split(' ').ToHashSet();
        var wordsB = b.Split(' ').ToHashSet();
        double words = 2.0 * wordsA.Intersect(wordsB).Count() / (wordsA.Count + wordsB.Count);

        string shorter = compactA.Length <= compactB.Length ? compactA : compactB;
        string longer = ReferenceEquals(shorter, compactA) ? compactB : compactA;
        double inside = shorter.Length >= 4 && longer.Contains(shorter, StringComparison.Ordinal)
            ? 0.6 + 0.3 * shorter.Length / longer.Length
            : 0;

        return Math.Max(edits, Math.Max(words, inside));
    }

    /// <summary>Levenshtein distance (texts cut to 120 characters: a title, an artist).</summary>
    private static int Distance(string a, string b)
    {
        a = a.Length > 120 ? a[..120] : a;
        b = b.Length > 120 ? b[..120] : b;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    // ── Patterns ────────────────────────────────────────────────────────────────────────────────────────────

    // Anything in brackets: ( ), [ ], { }, 【 】, （ ）.
    [GeneratedRegex(@"[\(\[\{【（](?<inside>[^\)\]\}】）]*)[\)\]\}】）]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"^\s*(?:feat\.?|ft\.?|featuring|with|avec|prod\.?\s+by)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingStart();

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+(?<who>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InlineFeaturing();

    // An ending after a dash that says a version, as streaming services write them: « - Remastered 2011 », « - 2011
    // Remaster », « - Radio Edit », « - Acoustic Version », « - XYZ Remix », « - Live », « - Live at Wembley », « - Mono »,
    // « - From "Frozen" », « - feat. X ». Never a title that only starts like one (« Oasis - Live Forever »).
    [GeneratedRegex(@"\s+[-–—]\s+(?<tail>(?:\d{4}\s+)?(?:digital(?:ly)?\s+)?remaster(?:ed)?(?:\s+(?:\d{4}|version))*|[^-–—]*\b(?:version|edit|remix|rmx|mix)|live(?:\s+(?:at|in|from|on|au|aux|à|a|en)\b[^-–—]*)?|(?:acoustic|instrumental|demo|mono|stereo|unplugged|sped\s+up|slowed(?:\s+(?:and|\+|&)\s+reverb)?|bonus\s+track)(?:\s+version)?(?:\s+\d{4})?|from\s+[“""«'][^-–—]*|(?:feat\.?|ft\.?|featuring)\s+[^-–—]*)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DashEnding();

    // 「AMV」, 『Lyrics』, 「MV」: a video's kind in Japanese brackets (a title in them stays).
    [GeneratedRegex(@"[「『](?<inside>\s*(?:anime\s+|official\s+)?(?:amv|gmv|mv|m/v|pv|mmd|lyrics?|lyric\s+video|歌詞|nightcore|official|video|audio)\s*)[」』]", RegexOptions.IgnoreCase)]
    private static partial Regex Decorated();

    // Dashes and bars side by side once a decoration between them is gone: « Title - - Anime MV ».
    [GeneratedRegex(@"(?:\s*[-–—|~]\s*){2,}")]
    private static partial Regex Dashes();

    // What the video is, at the end after a separator (« - Anime MV », « | Official Lyric Video », « - AMV »), or one of
    // the unmistakable ones without (« Title AMV »).
    [GeneratedRegex(@"(?:\s*[-–—|~:]\s*(?:anime\s+|official\s+|animated\s+)?(?:amv|gmv|mv|m/v|pv|mmd|music\s+video|lyrics?(?:\s+video)?|lyric\s+video|video\s+clip|video|audio|visuali[sz]er|clip(?:\s+officiel)?|hd|4k|nightcore)|\s+(?:amv|gmv|pv|mmd))\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DecorationEnd();

    // The same at the start, before a separator: « AMV - Title », « Nightcore | Title ».
    [GeneratedRegex(@"^\s*(?:anime\s+)?(?:amv|gmv|mv|pv|mmd|nightcore)\s*[-–—|~:]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DecorationStart();

    // What says a title is a video's: « (Official Video) », « [Lyrics] », « (AMV) »…
    [GeneratedRegex(@"\b(?:official|video|audio|lyrics?|mv|m/v|amv|gmv|pv|clip|visuali[sz]er|hd|4k)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VideoWords();

    // The browsers, as Windows names the app playing (« Chrome », « MSEdge », Firefox's own id…).
    [GeneratedRegex(@"chrome|msedge|edge|firefox|308046B0AF4A39CB|opera|brave|vivaldi|arc\b|zen|floorp|librewolf|waterfox|thorium|yandex", RegexOptions.IgnoreCase)]
    private static partial Regex Browser();

    // Between artist and title in a video's title.
    [GeneratedRegex(@"\s+[-–—|~]\s+|\s*//\s*")]
    private static partial Regex Separator();

    // « Artist「Title」 », « Artist『Title』 », « Artist "Title" ».
    [GeneratedRegex(@"^(?<artist>[^「『""“]+?)\s*[「『""“](?<title>[^」』""”]+)[」』""”]")]
    private static partial Regex Quoted();

    // Between artists of a credit.
    [GeneratedRegex(@"\s*(?:,|;|&|\+|/|\s+(?:feat\.?|ft\.?|featuring|with|x|×|vs\.?|et)\s+)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Credit();

    // A video channel's name: « Artist - Topic », « ArtistVEVO ».
    [GeneratedRegex(@"(?:\s*-\s*Topic|VEVO)$", RegexOptions.IgnoreCase)]
    private static partial Regex Channel();

    [GeneratedRegex(@"^live\b|\blive$|\blive (?:at|in|from|on|a|au|aux|en)\b|\ben concert\b")]
    private static partial Regex LiveAlbum();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
