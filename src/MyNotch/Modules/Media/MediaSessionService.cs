using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MyNotch.Modules.Media;

/// <summary>
/// Follows the system media session (System Media Transport Controls), whichever app owns it: Spotify,
/// a browser tab (YouTube…), VLC, Media Player… and publishes it as <see cref="MediaSnapshot"/>s.
/// </summary>
/// <remarks>
/// SMTC events arrive on worker threads and in bursts: a track change typically fires several of them, and the
/// cover often lands after the title. Everything is marshalled to the UI thread and coalesced into one refresh.
/// </remarks>
public sealed class MediaSessionService : IMediaSessionService, IDisposable
{
    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(60);
    private const ulong MaxArtworkBytes = 8 * 1024 * 1024;

    private readonly DispatcherQueue _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private CancellationTokenSource? _pendingRefresh;
    private MediaArtwork? _artwork;

    public MediaSessionService(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public MediaSnapshot Current { get; private set; } = MediaSnapshot.Empty;

    public event EventHandler<MediaSnapshot>? SnapshotChanged;

    public async Task StartAsync()
    {
        if (_manager is not null)
        {
            return;
        }

        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Media] SMTC unavailable: {ex.Message}");
            return;
        }

        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _manager.SessionsChanged += OnSessionsChanged;
        SelectSession();
    }

    public Task<bool> TogglePlayPauseAsync() => SendAsync(session => session.TryTogglePlayPauseAsync());

    public Task<bool> SkipNextAsync() => SendAsync(session => session.TrySkipNextAsync());

    public Task<bool> SkipPreviousAsync() => SendAsync(session => session.TrySkipPreviousAsync());

    public Task<bool> SeekAsync(TimeSpan position) => SendAsync(session =>
        session.TryChangePlaybackPositionAsync((session.GetTimelineProperties().StartTime + position).Ticks));

    public void Dispose()
    {
        _pendingRefresh?.Cancel();
        Attach(null);
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager = null;
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        _dispatcher.TryEnqueue(SelectSession);

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) =>
        _dispatcher.TryEnqueue(SelectSession);

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _dispatcher.TryEnqueue(ScheduleRefresh);

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        _dispatcher.TryEnqueue(ScheduleRefresh);

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        _dispatcher.TryEnqueue(ScheduleRefresh);

    /// <summary>
    /// Follows the session Windows considers current, unless it is paused while another one is playing
    /// (Spotify paused, then a video started in the browser): the one making sound wins.
    /// </summary>
    private void SelectSession()
    {
        if (_manager is null)
        {
            return;
        }

        GlobalSystemMediaTransportControlsSession? session = null;
        try
        {
            session = _manager.GetCurrentSession();
            if (session is null || !IsPlaying(session))
            {
                var sessions = _manager.GetSessions();
                session = sessions.FirstOrDefault(IsPlaying) ?? session ?? sessions.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Media] Session lookup failed: {ex.Message}");
        }

        Attach(session);
        ScheduleRefresh();
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, _session))
        {
            return;
        }

        if (_session is { } previous)
        {
            try
            {
                previous.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                previous.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                previous.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }
            catch (Exception ex)
            {
                // The player may already be gone.
                Debug.WriteLine($"[Media] Detach failed: {ex.Message}");
            }
        }

        _session = session;
        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
    }

    private void ScheduleRefresh()
    {
        _pendingRefresh?.Cancel();
        _pendingRefresh = new CancellationTokenSource();
        _ = RefreshAsync(_pendingRefresh.Token);
    }

    private async Task RefreshAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(CoalesceDelay, cancellation);

            var session = _session;
            var snapshot = session is null ? MediaSnapshot.Empty : await ReadAsync(session);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(session, _session))
            {
                return;
            }

            Current = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Media] Refresh failed: {ex.Message}");
        }
    }

    private async Task<MediaSnapshot> ReadAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var properties = await session.TryGetMediaPropertiesAsync();
        var playback = session.GetPlaybackInfo();
        var controls = playback?.Controls;
        var artwork = await ReadArtworkAsync(properties?.Thumbnail);

        return new MediaSnapshot(
            SourceAppId: session.SourceAppUserModelId,
            Title: properties?.Title?.Trim() ?? "",
            Artist: properties?.Artist?.Trim() ?? "",
            AlbumTitle: properties?.AlbumTitle?.Trim() ?? "",
            Artwork: artwork,
            IsPlaying: playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            CanPlayPause: controls is not null && (controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled),
            CanSkipNext: controls?.IsNextEnabled == true,
            CanSkipPrevious: controls?.IsPreviousEnabled == true,
            CanSeek: controls?.IsPlaybackPositionEnabled == true,
            Timeline: ReadTimeline(session.GetTimelineProperties(), playback));
    }

    private async Task<MediaArtwork?> ReadArtworkAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        try
        {
            byte[] bytes;
            using (var stream = await thumbnail.OpenReadAsync())
            {
                if (stream.Size == 0 || stream.Size > MaxArtworkBytes)
                {
                    return null;
                }

                var buffer = await stream.ReadAsync(new Windows.Storage.Streams.Buffer((uint)stream.Size), (uint)stream.Size, InputStreamOptions.None);
                bytes = buffer.ToArray();
            }

            // Players re-send the cover with every update: only decode what actually changed.
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (_artwork?.Hash == hash)
            {
                return _artwork;
            }

            var accent = await ArtworkPalette.ExtractAccentAsync(bytes);
            return _artwork = new MediaArtwork(bytes, hash, accent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Media] Artwork unavailable: {ex.Message}");
            return null;
        }
    }

    private static MediaTimeline ReadTimeline(
        GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline,
        GlobalSystemMediaTransportControlsSessionPlaybackInfo? playback)
    {
        if (timeline is null)
        {
            return MediaTimeline.None;
        }

        var duration = timeline.EndTime - timeline.StartTime;
        return duration <= TimeSpan.Zero
            ? MediaTimeline.None
            : new MediaTimeline(timeline.Position - timeline.StartTime, duration, timeline.LastUpdatedTime, playback?.PlaybackRate ?? 1.0);
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> SendAsync(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> command)
    {
        if (_session is not { } session)
        {
            return false;
        }

        try
        {
            return await command(session);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Media] Command failed: {ex.Message}");
            return false;
        }
    }
}
