namespace MyNotch.Modules.Media;

/// <summary>The system-wide "now playing" session and its transport controls.</summary>
public interface IMediaSessionService
{
    MediaSnapshot Current { get; }

    /// <summary>Raised on the UI thread whenever the session, its track or its playback state changes.</summary>
    event EventHandler<MediaSnapshot>? SnapshotChanged;

    Task StartAsync();

    /// <returns><c>true</c> if the player accepted the command.</returns>
    Task<bool> TogglePlayPauseAsync();

    Task<bool> SkipNextAsync();

    Task<bool> SkipPreviousAsync();

    /// <summary>Moves playback to <paramref name="position"/> (from the start of the track).</summary>
    Task<bool> SeekAsync(TimeSpan position);
}
