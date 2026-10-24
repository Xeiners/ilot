using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyNotch.Modules.Media.Lyrics;

/// <summary>
/// Synced lyrics from LRCLIB (https://lrclib.net), a free and open lyrics database.
/// </summary>
/// <remarks>
/// Exact match first (<c>/api/get</c>: title, artist, album, duration), then a search as a fallback. Results are
/// cached in memory per artist and title, "no lyrics" included, and concurrent requests for the same track share
/// one download. Network failures are not cached: the next play of the track tries again. Everything runs off
/// the UI thread.
/// </remarks>
public sealed partial class LrcLibLyricsService : ILyricsService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<SyncedLyrics?>> _cache = new();

    public LrcLibLyricsService()
    {
        _http = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MyNotch/1.0");
    }

    public Task<SyncedLyrics?> GetAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        string key = Normalize(query.Artist) + '\u001F' + Normalize(query.Title);
        return _cache.GetOrAdd(key, _ => FetchAsync(query, key)).WaitAsync(cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private async Task<SyncedLyrics?> FetchAsync(LyricsQuery query, string key)
    {
        try
        {
            var track = await GetExactAsync(query).ConfigureAwait(false) ?? await SearchAsync(query).ConfigureAwait(false);
            return LrcParser.Parse(track?.SyncedLyrics);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Debug.WriteLine($"[Lyrics] Unavailable for \"{query.Title}\": {ex.Message}");
            _cache.TryRemove(key, out _);
            return null;
        }
    }

    private async Task<LrcLibTrack?> GetExactAsync(LyricsQuery query)
    {
        if (query.Album.Length == 0 || query.Duration <= TimeSpan.Zero)
        {
            return null;
        }

        string url = "api/get"
            + $"?track_name={Escape(query.Title)}&artist_name={Escape(query.Artist)}&album_name={Escape(query.Album)}"
            + $"&duration={Math.Round(query.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture)}";
        using var response = await _http.GetAsync(url).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var track = await response.Content.ReadFromJsonAsync<LrcLibTrack>(Json).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(track?.SyncedLyrics) ? null : track;
    }

    /// <summary>
    /// Searches by title and artist, then by title alone keeping only the same artist ("DemiLovatoVEVO" is
    /// "Demi Lovato"), then for video titles shaped "Artist - Title".
    /// </summary>
    private async Task<LrcLibTrack?> SearchAsync(LyricsQuery query)
    {
        string title = CleanTitle(query.Title);
        string artist = CleanArtist(query.Artist);
        if (title.Length == 0)
        {
            return null;
        }

        if (artist.Length > 0)
        {
            var match = Pick(await SearchAsync(title, artist).ConfigureAwait(false), query.Duration, artist: null);
            if (match is not null)
            {
                return match;
            }
        }

        var byTitle = Pick(await SearchAsync(title, artist: null).ConfigureAwait(false), query.Duration, artist);
        if (byTitle is not null || artist.Length == 0)
        {
            return byTitle;
        }

        int dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash <= 0)
        {
            return null;
        }

        string videoArtist = title[..dash].Trim();
        string videoTitle = title[(dash + 3)..].Trim();
        return Pick(await SearchAsync(videoTitle, videoArtist).ConfigureAwait(false), query.Duration, artist: null);
    }

    private async Task<LrcLibTrack[]> SearchAsync(string title, string? artist)
    {
        string url = $"api/search?track_name={Escape(title)}";
        if (!string.IsNullOrEmpty(artist))
        {
            url += $"&artist_name={Escape(artist)}";
        }

        return await _http.GetFromJsonAsync<LrcLibTrack[]>(url, Json).ConfigureAwait(false) ?? [];
    }

    /// <summary>Best synced candidate, optionally by the same artist (compared letters only).</summary>
    private static LrcLibTrack? Pick(LrcLibTrack[] results, TimeSpan duration, string? artist)
    {
        var synced = results.Where(track => !string.IsNullOrWhiteSpace(track.SyncedLyrics));
        if (artist is not null)
        {
            string wanted = Letters(artist);
            synced = synced.Where(track => Letters(track.ArtistName).Contains(wanted) || wanted.Contains(Letters(track.ArtistName)));
        }

        if (duration <= TimeSpan.Zero)
        {
            return synced.FirstOrDefault();
        }

        // Same song, other version (live, remix…) would drift out of sync: insist on a close duration.
        return synced
            .Select(track => (track, gap: Math.Abs((track.Duration ?? 0) - duration.TotalSeconds)))
            .Where(candidate => candidate.gap <= DurationTolerance.TotalSeconds)
            .OrderBy(candidate => candidate.gap)
            .Select(candidate => candidate.track)
            .FirstOrDefault();
    }

    private static string Letters(string? value) =>
        string.Concat((value ?? "").Where(char.IsLetterOrDigit)).ToLowerInvariant() is { Length: > 0 } letters ? letters : "\u0000";

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    /// <summary>"Song (Official Video)" → "Song": video platforms decorate titles that the database does not.</summary>
    private static string CleanTitle(string title) => Decoration().Replace(title, "").Trim();

    /// <summary>"Artist - Topic" / "ArtistVEVO" → "Artist" (YouTube channel names).</summary>
    private static string CleanArtist(string artist) => ChannelSuffix().Replace(artist, "").Trim();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(official|video|audio|lyrics?|clip|visuali[sz]er|remaster(ed)?|hd|4k)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Decoration();

    [GeneratedRegex(@"(\s*-\s*Topic|VEVO)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelSuffix();

    private sealed record LrcLibTrack(string? SyncedLyrics, string? ArtistName, double? Duration);
}
