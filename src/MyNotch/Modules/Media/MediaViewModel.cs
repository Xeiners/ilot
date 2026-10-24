using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.UI;
using MyNotch.Localization;

namespace MyNotch.Modules.Media;

/// <summary>Presentation state of the media module.</summary>
public sealed class MediaViewModel : ObservableObject
{
    private static string IdleTitle => Loc.T("Aucune lecture en cours");
    private static string IdleSubtitle => Loc.T("Lancez Spotify, YouTube, VLC…");

    // Right after a seek, players often report their old position once or twice: those reports are ignored
    // for a moment so the bar does not bounce back.
    private static readonly TimeSpan SeekSettleTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SeekTolerance = TimeSpan.FromSeconds(1.5);

    private readonly IMediaSessionService _media;
    private string _title = IdleTitle;
    private string _subtitle = IdleSubtitle;
    private string _trackId = "";
    private ImageSource? _artwork;
    private string? _artworkHash;
    private Color _accent = ArtworkPalette.Neutral;
    private bool _hasMedia;
    private bool _isPlaying;
    private bool _canPlayPause;
    private bool _canSkipNext;
    private bool _canSkipPrevious;
    private bool _canSeek;
    private MediaTimeline _timeline;
    private DateTimeOffset _seekIssuedAt;
    private TimeSpan _seekTarget;

    public MediaViewModel(IMediaSessionService media)
    {
        _media = media;
        TogglePlayPauseCommand = new AsyncRelayCommand(TogglePlayPauseAsync, () => CanPlayPause, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        NextCommand = new AsyncRelayCommand(media.SkipNextAsync, () => CanSkipNext, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        PreviousCommand = new AsyncRelayCommand(media.SkipPreviousAsync, () => CanSkipPrevious, AsyncRelayCommandOptions.AllowConcurrentExecutions);

        media.SnapshotChanged += (_, snapshot) => Apply(snapshot);
        Apply(media.Current);
    }

    public IAsyncRelayCommand TogglePlayPauseCommand { get; }

    public IAsyncRelayCommand NextCommand { get; }

    public IAsyncRelayCommand PreviousCommand { get; }

    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }

    /// <summary>Changes whenever another track starts; drives the text transition.</summary>
    public string TrackId { get => _trackId; private set => SetProperty(ref _trackId, value); }

    public ImageSource? Artwork { get => _artwork; private set => SetProperty(ref _artwork, value); }

    private string _sourceAppId = "";

    /// <summary>The app playing, as Windows knows it ("Spotify.exe", "…!App" for a packaged one); empty without media.</summary>
    public string SourceAppId { get => _sourceAppId; private set => SetProperty(ref _sourceAppId, value); }

    /// <summary>Dominant colour of the cover, used for the equalizer and the ambient glow.</summary>
    public Color Accent { get => _accent; private set => SetProperty(ref _accent, value); }

    public bool HasMedia { get => _hasMedia; private set => SetProperty(ref _hasMedia, value); }

    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }

    public MediaTimeline Timeline { get => _timeline; private set => SetProperty(ref _timeline, value); }

    /// <summary>Whether the player accepts a new playback position (the scrubber is interactive).</summary>
    public bool CanSeek { get => _canSeek; private set => SetProperty(ref _canSeek, value); }

    public bool CanPlayPause
    {
        get => _canPlayPause;
        private set
        {
            if (SetProperty(ref _canPlayPause, value))
            {
                TogglePlayPauseCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanSkipNext
    {
        get => _canSkipNext;
        private set
        {
            if (SetProperty(ref _canSkipNext, value))
            {
                NextCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanSkipPrevious
    {
        get => _canSkipPrevious;
        private set
        {
            if (SetProperty(ref _canSkipPrevious, value))
            {
                PreviousCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void Apply(MediaSnapshot snapshot)
    {
        bool hasMedia = snapshot.HasSession && (snapshot.Title.Length > 0 || snapshot.Artist.Length > 0);

        string trackId = hasMedia ? $"{snapshot.SourceAppId}\u001F{snapshot.Title}\u001F{snapshot.Artist}" : "";
        if (trackId != TrackId)
        {
            _seekIssuedAt = default;
        }

        Title = hasMedia ? FirstNonEmpty(snapshot.Title, Loc.T("Lecture en cours")) : IdleTitle;
        Subtitle = hasMedia ? FirstNonEmpty(snapshot.Artist, snapshot.AlbumTitle, AppName(snapshot.SourceAppId)) : IdleSubtitle;
        TrackId = trackId;
        SourceAppId = hasMedia ? snapshot.SourceAppId ?? "" : "";
        HasMedia = hasMedia;
        IsPlaying = hasMedia && snapshot.IsPlaying;
        CanPlayPause = hasMedia && snapshot.CanPlayPause;
        CanSkipNext = hasMedia && snapshot.CanSkipNext;
        CanSkipPrevious = hasMedia && snapshot.CanSkipPrevious;
        CanSeek = hasMedia && snapshot.CanSeek;
        var timeline = hasMedia ? snapshot.Timeline : MediaTimeline.None;
        if (!IsStaleAfterSeek(timeline, snapshot.IsPlaying))
        {
            Timeline = timeline;
        }

        Accent = hasMedia ? snapshot.Artwork?.Accent ?? ArtworkPalette.Neutral : ArtworkPalette.Neutral;

        _ = LoadArtworkAsync(hasMedia ? snapshot.Artwork : null);
    }

    /// <summary>Jumps to <paramref name="fraction"/> of the track.</summary>
    public async Task SeekAsync(double fraction)
    {
        var timeline = Timeline;
        if (!CanSeek || !timeline.IsKnown)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        _seekTarget = timeline.Duration * Math.Clamp(fraction, 0, 1);
        _seekIssuedAt = now;

        // Optimistic: the bar and the times move at once, the player confirms with its next update.
        Timeline = timeline with { Position = _seekTarget, LastUpdated = now };
        if (!await _media.SeekAsync(_seekTarget))
        {
            _seekIssuedAt = default;
            Apply(_media.Current);
        }
    }

    private bool IsStaleAfterSeek(MediaTimeline reported, bool isPlaying)
    {
        var now = DateTimeOffset.Now;
        if (!reported.IsKnown || now - _seekIssuedAt > SeekSettleTime)
        {
            return false;
        }

        var expected = _seekTarget + (isPlaying ? now - _seekIssuedAt : TimeSpan.Zero);
        return (reported.PositionAt(now, isPlaying) - expected).Duration() > SeekTolerance;
    }

    private async Task LoadArtworkAsync(MediaArtwork? artwork)
    {
        if (artwork?.Hash == _artworkHash)
        {
            return;
        }

        _artworkHash = artwork?.Hash;
        if (artwork is null)
        {
            Artwork = null;
            return;
        }

        BitmapImage? image = new();
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(artwork.Bytes.AsBuffer());
            stream.Seek(0);
            await image.SetSourceAsync(stream);
        }
        catch (Exception)
        {
            image = null;
        }

        // A newer cover may have arrived while this one was decoding.
        if (_artworkHash == artwork.Hash)
        {
            Artwork = image;
        }
    }

    private async Task TogglePlayPauseAsync()
    {
        // Optimistic: the glyph morphs instantly, the session confirms (or corrects) right after.
        IsPlaying = !IsPlaying;
        if (!await _media.TogglePlayPauseAsync())
        {
            IsPlaying = !IsPlaying;
        }
    }

    /// <summary>"Spotify.exe" → "Spotify", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → "ZuneMusic".</summary>
    private static string AppName(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return "";
        }

        string name = appUserModelId.Split('!')[0].Split('_')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        name = name[(name.LastIndexOf('.') + 1)..];
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "";
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
