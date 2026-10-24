using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using MyNotch.Settings;

namespace MyNotch.Modules.Media.Lyrics;

/// <summary>Where playback stands in the lyrics: the line being sung, how far into it, and how long it lasts.</summary>
public readonly record struct LyricsPosition(int Index, TimeSpan IntoLine, TimeSpan LineLength, bool IsPlaying);

/// <summary>
/// Lyrics of the current track and the line being sung.
/// </summary>
/// <remarks>
/// No polling: a one-shot timer is armed for the exact moment the next line starts, and re-armed whenever the
/// player reports a new position, pauses or seeks. It only runs while the lyrics can be seen.
/// </remarks>
public sealed class LyricsViewModel : ObservableObject
{
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(15);

    private readonly ILyricsService _service;
    private readonly MediaViewModel _player;
    private readonly AppSettings _settings;
    private readonly DispatcherQueueTimer _nextLine;
    private CancellationTokenSource? _fetch;
    private string _trackKey = "";
    private SyncedLyrics? _content;
    private bool _isTracking;

    public LyricsViewModel(
        IMediaSessionService media,
        MediaViewModel player,
        ILyricsService service,
        AppSettings settings,
        DispatcherQueue dispatcher)
    {
        _service = service;
        _player = player;
        _settings = settings;
        _nextLine = dispatcher.CreateTimer();
        _nextLine.IsRepeating = false;
        _nextLine.Tick += (_, _) => Sync();
        ToggleCommand = new RelayCommand(() => settings.ShowLyrics = !settings.ShowLyrics);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ShowLyrics))
            {
                OnPropertyChanged(nameof(IsEnabled));
                OnPropertyChanged(nameof(IsShown));
                Sync();
            }
        };

        media.SnapshotChanged += (_, snapshot) => OnSnapshot(snapshot);
        player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaViewModel.Timeline) or nameof(MediaViewModel.IsPlaying))
            {
                Sync();
            }
        };
        OnSnapshot(media.Current);
    }

    /// <summary>Turns lyrics on or off (the setting, saved).</summary>
    public IRelayCommand ToggleCommand { get; }

    /// <summary>Lyrics of the current track, or <c>null</c> (none found, or still loading).</summary>
    public SyncedLyrics? Content
    {
        get => _content;
        private set
        {
            if (SetProperty(ref _content, value))
            {
                OnPropertyChanged(nameof(IsAvailable));
                OnPropertyChanged(nameof(IsShown));
                Sync();
            }
        }
    }

    private bool _isLooking;

    /// <summary>The current track's lyrics are being looked for (a new track): they may come in a moment.</summary>
    public bool IsLooking
    {
        get => _isLooking;
        private set => SetProperty(ref _isLooking, value);
    }

    /// <summary>The track has synced lyrics (whether or not they are shown).</summary>
    public bool IsAvailable => _content is not null;

    /// <summary>The user wants lyrics (setting).</summary>
    public bool IsEnabled => _settings.ShowLyrics;

    /// <summary>Lyrics are wanted and exist; otherwise the view shows something else.</summary>
    public bool IsShown => IsEnabled && _content is not null;

    /// <summary>Set by the view while the lyrics can be seen: only then does the line timer run.</summary>
    public bool IsTracking
    {
        get => _isTracking;
        set
        {
            if (SetProperty(ref _isTracking, value))
            {
                Sync();
            }
        }
    }

    /// <summary>
    /// Raised whenever the position in the lyrics is (re)computed: a new line starts, or the player reports a new
    /// position, pauses, resumes or seeks. Views re-sync their line and its scrolling from it.
    /// </summary>
    public event EventHandler<LyricsPosition>? Synced;

    private async void OnSnapshot(MediaSnapshot snapshot)
    {
        string key = snapshot.HasSession && snapshot.Title.Length > 0 ? $"{snapshot.Artist}\u001F{snapshot.Title}" : "";
        if (key == _trackKey)
        {
            return;
        }

        _trackKey = key;
        _fetch?.Cancel();
        Content = null;
        if (key.Length == 0)
        {
            IsLooking = false;
            return;
        }

        var fetch = _fetch = new CancellationTokenSource();
        IsLooking = true;
        var query = new LyricsQuery(snapshot.Title, snapshot.Artist, snapshot.AlbumTitle, snapshot.Timeline.Duration);
        try
        {
            var lyrics = await _service.GetAsync(query, fetch.Token);
            if (!fetch.IsCancellationRequested)
            {
                Content = lyrics;
            }
        }
        catch (OperationCanceledException)
        {
            // Another track started meanwhile.
        }
        finally
        {
            if (_fetch == fetch)
            {
                IsLooking = false; // Found or not: this track's lookup is over.
            }
        }
    }

    private void Sync()
    {
        _nextLine.Stop();
        var lyrics = _content;
        if (lyrics is null || !IsShown)
        {
            return;
        }

        var timeline = _player.Timeline;
        bool playing = _player.IsPlaying;
        var position = timeline.PositionAt(DateTimeOffset.Now, playing);
        int index = lyrics.IndexAt(position);
        var lineStart = index < 0 ? TimeSpan.Zero : lyrics.Lines[index].Time;
        var lineEnd = index + 1 < lyrics.Lines.Count ? lyrics.Lines[index + 1].Time : timeline.Duration;
        var length = lineEnd > lineStart ? lineEnd - lineStart : TimeSpan.Zero;
        Synced?.Invoke(this, new LyricsPosition(index, position - lineStart, length, playing && timeline.IsKnown));

        if (!_isTracking || !playing || !timeline.IsKnown || index + 1 >= lyrics.Lines.Count)
        {
            return;
        }

        var wait = (lyrics.Lines[index + 1].Time - position) / Math.Max(timeline.PlaybackRate, 0.01);
        _nextLine.Interval = wait < MinimumWait ? MinimumWait : wait;
        _nextLine.Start();
    }
}
