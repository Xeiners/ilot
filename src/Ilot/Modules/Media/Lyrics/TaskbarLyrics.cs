using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Ilot.Interop;
using Ilot.Modules.Search;
using Ilot.Settings;
using Ilot.Windowing;
using Windows.Graphics;

namespace Ilot.Modules.Media.Lyrics;

/// <summary>
/// The sung line on the Windows taskbar rather than on the closed notch (setting): against the tray's icons, at the right
/// end of the last empty stretch before them, flush right, the artwork beside it (the playing app's icon on its corner).
/// How wide the lines may go is the user's (<see cref="AppSettings.LyricsTaskbarWidth"/>: its left edge pulled, or the
/// settings), within the room there is. The line being sung brightens as it is sung (karaoke), the next one under it;
/// light on a dark taskbar, dark on a light one. There while a track with lyrics plays (a pause of a moment does not take
/// it away); gone when an app is full screen, when the taskbar hides itself, or when it has no room. Placed again whenever
/// the taskbar may have changed (another app in front, and every few seconds while it shows), and kept above it. A click
/// opens the notch on the player.
/// </summary>
internal sealed class TaskbarLyrics : IDisposable
{
    private const double Room = 4; // Left free between the artwork and the tray's icons.
    private static readonly TimeSpan PausedFor = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan LookAgain = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Settled = TimeSpan.FromMilliseconds(600);

    private readonly LyricsViewModel _lyrics;
    private readonly MediaViewModel _player;
    private readonly AppSettings _settings;
    private readonly TaskbarLyricsWindow _window;
    private readonly FullscreenWatcher _fullscreen;
    private readonly DispatcherQueueTimer _paused;
    private readonly DispatcherQueueTimer _again;
    private readonly DispatcherQueueTimer _settled;
    private ShellIcons? _icons; // The playing app's icon, drawn by the shell (made the first time one is needed).
    private string? _badgeApp;
    private bool _playing;
    private bool _shown;
    private bool _looking;
    private RectInt32? _placed;
    private double _scale = 1; // The taskbar's screen's scale, as last read.
    private bool _disposed;

    public TaskbarLyrics(LyricsViewModel lyrics, MediaViewModel player, AppSettings settings, DispatcherQueue dispatcher)
    {
        _lyrics = lyrics;
        _player = player;
        _settings = settings;
        _playing = player.IsPlaying;
        _window = new TaskbarLyricsWindow();
        _window.Clicked += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);
        _window.Resized += OnResized;
        _window.Panel.SetLyrics(lyrics.Content);
        _window.SetCover(player.Artwork);
        ShowBadge();
        ApplySize();

        _paused = Timer(dispatcher, PausedFor, () =>
        {
            _playing = false;
            Update();
        });
        _again = Timer(dispatcher, LookAgain, Look);
        _again.IsRepeating = true;
        _settled = Timer(dispatcher, Settled, Look);

        _fullscreen = new FullscreenWatcher(dispatcher);
        _fullscreen.Changed += (_, _) => Update();
        _fullscreen.ForegroundChanged += OnForegroundChanged;
        lyrics.PropertyChanged += OnLyricsChanged;
        lyrics.Synced += OnSynced;
        player.PropertyChanged += OnPlayerChanged;
        settings.PropertyChanged += OnSettingsChanged;
        Update();
    }

    /// <summary>The line clicked: the notch opens on the player.</summary>
    public event EventHandler? Clicked;

    /// <summary>Raised when it comes onto the taskbar or leaves it (see <see cref="IsShown"/>), or finds it has no room there.</summary>
    public event EventHandler? ShownChanged;

    /// <summary>On the taskbar now (the track has lyrics, it plays, there is room…).</summary>
    public bool IsShown => _shown;

    /// <summary>The taskbar had room for it, last it looked (or it has not looked yet).</summary>
    public bool HasRoom { get; private set; } = true;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lyrics.PropertyChanged -= OnLyricsChanged;
        _lyrics.Synced -= OnSynced;
        _player.PropertyChanged -= OnPlayerChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
        _paused.Stop();
        _again.Stop();
        _settled.Stop();
        _fullscreen.Dispose();
        _icons?.Dispose();
        _window.Close();
    }

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.Content))
        {
            _window.Panel.SetLyrics(_lyrics.Content);
        }

        if (e.PropertyName is nameof(LyricsViewModel.Content) or nameof(LyricsViewModel.IsShown))
        {
            Update();
        }
    }

    private void OnSynced(object? sender, LyricsPosition position) => _window.Panel.Sync(position);

    /// <summary>Playing: at once; paused: after a moment (between two tracks, it stays). Its artwork and app follow the track.</summary>
    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MediaViewModel.Artwork):
                _window.SetCover(_player.Artwork);
                return;

            case nameof(MediaViewModel.SourceAppId):
                ShowBadge();
                return;

            case nameof(MediaViewModel.IsPlaying) when _player.IsPlaying:
                _paused.Stop();
                _playing = true;
                Update();
                return;

            case nameof(MediaViewModel.IsPlaying) when !_paused.IsRunning:
                _paused.Start();
                return;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.CompactLyricsFontSize))
        {
            ApplySize();
        }
        else if (e.PropertyName == nameof(AppSettings.LyricsTaskbarWidth) && !_window.IsResizing)
        {
            Look(); // Set in the settings: at its new width.
        }
    }

    /// <summary>Its left edge pulled and let go: how wide its lines may go now, kept.</summary>
    private void OnResized(object? sender, int width) =>
        _settings.LyricsTaskbarWidth = width / _scale - TaskbarLyricsWindow.Chrome;

    /// <summary>Another app in front: the taskbar came over the line if it was clicked; and it may have changed (an app opened…).</summary>
    private void OnForegroundChanged(object? sender, EventArgs e)
    {
        if (_shown)
        {
            _window.KeepAbove();
            _settled.Stop();
            _settled.Start();
        }
    }

    /// <summary>Whether the line is wanted on the taskbar now: then where it goes is looked for; else it goes.</summary>
    private void Update()
    {
        if (_disposed)
        {
            return;
        }

        if (Wanted)
        {
            Look();
        }
        else
        {
            Withdraw();
        }
    }

    private bool Wanted => _lyrics.IsShown && _playing && !_fullscreen.IsFullscreen;

    /// <summary>Where the taskbar has room, read off the UI thread; the line goes there (or away, without room).</summary>
    private async void Look()
    {
        if (_looking || _disposed || !Wanted || _window.IsResizing)
        {
            return;
        }

        _looking = true;
        TaskbarSpots.Taskbar? taskbar;
        try
        {
            taskbar = await TaskbarSpots.FindAsync();
        }
        finally
        {
            _looking = false;
        }

        if (_disposed || !Wanted || _window.IsResizing)
        {
            return;
        }

        if (Spot(taskbar, _settings.LyricsTaskbarWidth) is not { } spot)
        {
            Withdraw();
            SetRoom(false);
            return;
        }

        SetRoom(true);

        _scale = spot.Scale;
        _window.WidthRange = (spot.Narrowest, spot.Widest);
        if (spot.Bounds != _placed)
        {
            _placed = spot.Bounds;
            _window.Place(spot.Bounds);
        }

        Tint();
        if (!_shown)
        {
            _shown = true;
            _window.Appear();
            _again.Start();
            ShownChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _window.KeepAbove();
        }
    }

    /// <summary>
    /// Against the tray's icons: at the right end of the last empty stretch with room for it (the one before the icons), a
    /// little room left; its lines <paramref name="lines"/> logical pixels wide at most (the artwork beside them), less if
    /// the stretch is narrower; none when it has not even the narrowest. How narrow and how wide its left edge may make it.
    /// </summary>
    internal static (RectInt32 Bounds, int Narrowest, int Widest, double Scale)? Spot(TaskbarSpots.Taskbar? taskbar, double lines)
    {
        if (taskbar is not { Bounds: { Width: > 0, Height: > 0 } bar })
        {
            return null;
        }

        var middle = new NativeMethods.Point { X = bar.X + bar.Width / 2, Y = bar.Y + bar.Height / 2 };
        double scale = NativeMethods.GetScaleForMonitor(NativeMethods.MonitorFromPoint(middle, NativeMethods.MONITOR_DEFAULTTONEAREST));
        int room = (int)Math.Round(Room * scale);
        int narrowest = (int)Math.Round((AppSettings.MinLyricsTaskbarWidth + TaskbarLyricsWindow.Chrome) * scale);
        int widest = (int)Math.Round((AppSettings.MaxLyricsTaskbarWidth + TaskbarLyricsWindow.Chrome) * scale);
        int wanted = (int)Math.Round((lines + TaskbarLyricsWindow.Chrome) * scale);
        if (taskbar.Gaps.LastOrDefault(gap => gap.Right - gap.Left - room >= narrowest) is not { Right: > 0 } free)
        {
            return null;
        }

        int space = free.Right - free.Left - room;
        int width = Math.Clamp(wanted, narrowest, Math.Min(space, widest));
        return (new RectInt32(free.Right - room - width, bar.Y, width, bar.Height), narrowest, Math.Min(space, widest), scale);
    }

    private void SetRoom(bool room)
    {
        if (room != HasRoom)
        {
            HasRoom = room;
            ShownChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Withdraw()
    {
        _again.Stop();
        _settled.Stop();
        if (_shown)
        {
            _shown = false;
            _window.Disappear();
            ShownChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Light on a dark taskbar, dark on a light one (Windows' own mode, the taskbar's).</summary>
    private void Tint() => _window.Tint(
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1);

    /// <summary>The playing app's icon on the artwork's corner, as the shell draws it; none when it has none.</summary>
    private async void ShowBadge()
    {
        string app = _player.SourceAppId;
        if (app == _badgeApp)
        {
            return;
        }

        _badgeApp = app;
        ImageSource? icon = null;
        if (app.Length > 0 && ShellNameOf(app) is { } source)
        {
            _icons ??= new ShellIcons();
            icon = await _icons.GetAsync(source, app);
        }

        if (!_disposed && _badgeApp == app)
        {
            _window.SetBadge(icon);
        }
    }

    /// <summary>
    /// What the shell knows the playing app by: a program ("Spotify.exe"), its file, as running (read with the least
    /// access there is); anything else, its entry in the Start menu's apps (a packaged app, "…!App"; "Chrome"…).
    /// </summary>
    private static string? ShellNameOf(string app)
    {
        if (!app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return @"shell:AppsFolder\" + app;
        }

        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app));
        try
        {
            return running.Select(process => NativeMethods.ProcessPath(process.Id)).FirstOrDefault(path => path is not null);
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// The sung line at the closed notch's size (setting), the next one under it; both centred in the taskbar's height
    /// (the next one is smaller: the pair is lifted a little).
    /// </summary>
    private void ApplySize()
    {
        double size = _settings.CompactLyricsFontSize;
        double line = Math.Ceiling(size * 1.45);
        double spacing = Math.Round(size * 1.25);
        var panel = _window.Panel;
        panel.LineFontSize = size;
        panel.LineSpacing = spacing;
        panel.Height = line;
        panel.Margin = new Thickness(4, 0, 0, Math.Max(0, spacing - 0.1 * line));
    }

    private static DispatcherQueueTimer Timer(DispatcherQueue dispatcher, TimeSpan interval, Action tick)
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }
}
