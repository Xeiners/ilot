using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ilot.Modules.Media.Lyrics;

/// <summary>
/// Synced lyrics from LRCLIB (https://lrclib.net), a free and open lyrics database.
/// </summary>
/// <remarks>
/// Asked several ways at once: the exact track (title, artist, album, duration), and a search for each way the track can
/// be read (<see cref="LyricsMatcher.Readings"/>); should nothing score high enough, a wider search on the words of the
/// title and the artist. Every candidate is scored (<see cref="LyricsMatcher"/>) and the best one kept if it is good
/// enough: no lyrics rather than wrong ones. Results are cached in memory per track as asked (its duration included, so a
/// track asked again once its duration is known is looked for again), "no lyrics" included; concurrent requests for the
/// same track share one download. When LRCLIB cannot be reached at all, nothing is cached: the next play tries again.
/// Everything runs off the UI thread.
/// </remarks>
public sealed class LrcLibLyricsService : ILyricsService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<SyncedLyrics?>> _cache = new();

    public LrcLibLyricsService()
    {
        _http = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Ilot/1.0");
    }

    public Task<SyncedLyrics?> GetAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        string seconds = Math.Round(query.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        string key = string.Join('\u001F', Normalize(query.Artist), Normalize(query.Title), Normalize(query.Album), seconds);
        return _cache.GetOrAdd(key, _ => FetchAsync(query, key)).WaitAsync(cancellationToken);
    }

    public void Dispose() => _http.Dispose();

    private async Task<SyncedLyrics?> FetchAsync(LyricsQuery query, string key)
    {
        try
        {
            var readings = LyricsMatcher.Readings(query);
            if (readings.Count == 0)
            {
                return null;
            }

            var (first, wider) = LyricsMatcher.Searches(readings);
            var found = new Dictionary<long, LyricsCandidate>();
            int answered = 0;

            await Gather([GetExactAsync(query), .. first.Select(search => SearchAsync(search.Title, search.Artist))]).ConfigureAwait(false);
            var best = LyricsMatcher.Best(query, readings, found.Values);
            if (best is not { Score: >= LyricsMatcher.Sure })
            {
                await Gather(wider.Select(SearchWordsAsync)).ConfigureAwait(false);
                best = LyricsMatcher.Best(query, readings, found.Values);
            }

            if (answered == 0)
            {
                throw new HttpRequestException("LRCLIB did not answer."); // Not cached: tried again next time.
            }

            Debug.WriteLine(best is null
                ? $"[Lyrics] None good enough for \"{query.Title}\" among {found.Count} candidates."
                : $"[Lyrics] \"{query.Title}\" → {best.Candidate.Artist} - {best.Candidate.Title} ({best.Score:0}/100).");
            return LrcParser.Parse(best?.Candidate.SyncedLyrics);

            async Task Gather(IEnumerable<Task<LyricsCandidate[]?>> requests)
            {
                foreach (var candidates in await Task.WhenAll(requests).ConfigureAwait(false))
                {
                    if (candidates is null)
                    {
                        continue;
                    }

                    answered++;
                    foreach (var candidate in candidates)
                    {
                        found.TryAdd(candidate.Id, candidate);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Debug.WriteLine($"[Lyrics] Unavailable for \"{query.Title}\": {ex.Message}");
            _cache.TryRemove(key, out _);
            return null;
        }
    }

    /// <summary>The track exactly as the player names it (LRCLIB allows a couple of seconds); none if it lacks album or duration.</summary>
    private Task<LyricsCandidate[]?> GetExactAsync(LyricsQuery query)
    {
        if (query.Album.Length == 0 || query.Duration <= TimeSpan.Zero || query.Artist.Length == 0)
        {
            return Task.FromResult<LyricsCandidate[]?>([]);
        }

        string url = "api/get"
            + $"?track_name={Escape(query.Title)}&artist_name={Escape(query.Artist)}&album_name={Escape(query.Album)}"
            + $"&duration={Math.Round(query.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture)}";
        return AskAsync(url, single: true);
    }

    private Task<LyricsCandidate[]?> SearchAsync(string title, string? artist)
    {
        string url = $"api/search?track_name={Escape(title)}";
        if (!string.IsNullOrEmpty(artist))
        {
            url += $"&artist_name={Escape(artist)}";
        }

        return AskAsync(url, single: false);
    }

    /// <summary>A search on words, wherever they are (title, artist, album).</summary>
    private Task<LyricsCandidate[]?> SearchWordsAsync(string words) => AskAsync($"api/search?q={Escape(words)}", single: false);

    /// <summary>The candidates of one request; <c>null</c> when it could not be answered (none found is an empty list).</summary>
    private async Task<LyricsCandidate[]?> AskAsync(string url, bool single)
    {
        try
        {
            using var response = await _http.GetAsync(url).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            response.EnsureSuccessStatusCode();
            var tracks = single
                ? [await response.Content.ReadFromJsonAsync<LrcLibTrack>(Json).ConfigureAwait(false)]
                : await response.Content.ReadFromJsonAsync<LrcLibTrack?[]>(Json).ConfigureAwait(false) ?? [];
            return tracks.OfType<LrcLibTrack>().Select(track => track.ToCandidate()).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Debug.WriteLine($"[Lyrics] {url}: {ex.Message}");
            return null;
        }
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private sealed record LrcLibTrack(long Id, string? TrackName, string? ArtistName, string? AlbumName, double? Duration, string? SyncedLyrics)
    {
        public LyricsCandidate ToCandidate() => new(Id, TrackName ?? "", ArtistName ?? "", AlbumName ?? "", Duration, SyncedLyrics);
    }
}
