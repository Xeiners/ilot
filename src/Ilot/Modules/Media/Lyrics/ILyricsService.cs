namespace Ilot.Modules.Media.Lyrics;

/// <summary>What is known about the track whose lyrics are wanted (<paramref name="Source"/>: the app playing it).</summary>
public sealed record LyricsQuery(string Title, string Artist, string Album, TimeSpan Duration, string Source = "");

/// <summary>Finds synced lyrics for a track.</summary>
public interface ILyricsService
{
    /// <returns>The lyrics, or <c>null</c> when none exist or they cannot be fetched (never throws but on cancellation).</returns>
    Task<SyncedLyrics?> GetAsync(LyricsQuery query, CancellationToken cancellationToken);
}
