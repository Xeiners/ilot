using Windows.UI;

namespace MyNotch.Modules.Media;

/// <summary>Immutable picture of the current media session.</summary>
public sealed record MediaSnapshot(
    string? SourceAppId,
    string Title,
    string Artist,
    string AlbumTitle,
    MediaArtwork? Artwork,
    bool IsPlaying,
    bool CanPlayPause,
    bool CanSkipNext,
    bool CanSkipPrevious,
    bool CanSeek,
    MediaTimeline Timeline)
{
    public static MediaSnapshot Empty { get; } =
        new(null, "", "", "", null, false, false, false, false, false, MediaTimeline.None);

    public bool HasSession => SourceAppId is not null;
}

/// <summary>Cover art (encoded bytes) and the accent colour extracted from it.</summary>
public sealed record MediaArtwork(byte[] Bytes, string Hash, Color Accent);

/// <summary>Playback position as last reported by the player.</summary>
public readonly record struct MediaTimeline(TimeSpan Position, TimeSpan Duration, DateTimeOffset LastUpdated, double PlaybackRate)
{
    public static MediaTimeline None => default;

    public bool IsKnown => Duration > TimeSpan.Zero;

    /// <summary>Playback position at <paramref name="now"/>, within [0, <see cref="Duration"/>].</summary>
    public TimeSpan PositionAt(DateTimeOffset now, bool isPlaying)
    {
        if (!IsKnown)
        {
            return TimeSpan.Zero;
        }

        // Players only report the position when something happens (seek, play, pause): extrapolate in between.
        var position = Position;
        if (isPlaying && LastUpdated.Year > 2000 && now > LastUpdated)
        {
            position += (now - LastUpdated) * Math.Max(PlaybackRate, 0);
        }

        return position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position;
    }

    /// <summary>Progress in [0, 1] at <paramref name="now"/>.</summary>
    public double ProgressAt(DateTimeOffset now, bool isPlaying) => IsKnown ? PositionAt(now, isPlaying) / Duration : 0;
}
