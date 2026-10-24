using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Ilot.Interop;

namespace Ilot.Modules.Search.Everything;

public enum EverythingStatus
{
    /// <summary>No Everything to ask (the copy shipped with the notch is missing, or would not start).</summary>
    Unavailable,

    /// <summary>Everything is starting, or building its index: it answers with what it has so far.</summary>
    Starting,

    Ready,
}

/// <summary>
/// The notch's file search engine: Everything (voidtools), run out of sight. The notch ships a copy of Everything
/// (portable: nothing installed) and starts it in the background, with its own settings and index under
/// <c>%LOCALAPPDATA%\Ilot\Everything</c>: no window, no tray icon. An Everything the user already runs is used as is.
/// </summary>
/// <remarks>
/// <para>
/// Full speed (the whole disks, read from their file tables, kept up to date as files change) needs the Everything
/// service, which does what needs administrator rights. When it is there (installed with Everything, or once from the
/// settings: <see cref="EnableFullSpeedAsync"/>), the copy uses it. Without it, the copy indexes the user's folder, and
/// keeps it up to date too.
/// </para>
/// <para>
/// It runs as Everything's default instance: the service only serves that one (a named instance would look for a
/// service of its own name).
/// </para>
/// </remarks>
public sealed class EverythingEngine : IDisposable
{
    private const string ServiceName = "Everything";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(2);

    private static readonly string DataFolder = Path.Combine(
        AppData.Folder, "Everything");

    private readonly EverythingClient _client = new();
    private readonly DispatcherQueueTimer _watch;
    private Process? _own;
    private long _watchingSince;
    private bool _checking;
    private EverythingStatus _status = EverythingStatus.Unavailable;

    public EverythingEngine(DispatcherQueue dispatcher)
    {
        // Only while Everything starts: once its index is ready, nothing runs any more.
        _watch = dispatcher.CreateTimer();
        _watch.Interval = CheckEvery;
        _watch.Tick += (_, _) => Check();
    }

    /// <summary>The copy shipped with the notch, next to it.</summary>
    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "Everything", "Everything.exe");

    public EverythingStatus Status
    {
        get => _status;
        private set
        {
            if (value != _status)
            {
                _status = value;
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The Everything service is there: whole disks, at full speed.</summary>
    public static bool IsFullSpeed => NativeMethods.IsServiceRunning(ServiceName);

    /// <summary>The Everything running is the user's own, not the notch's copy.</summary>
    public bool IsUsersOwn => Status != EverythingStatus.Unavailable && _own is null;

    /// <summary>Raised on the UI thread when <see cref="Status"/> changes.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Makes sure an Everything runs: the user's, else the notch's copy, started out of sight.</summary>
    public void Start()
    {
        nint running = EverythingClient.FindEverything();
        if (running != 0)
        {
            // The notch's own copy, left running (the notch did not close properly): it is the notch's again.
            _own ??= OwnCopy(running);
            Watch();
            return;
        }

        if (!File.Exists(BundledPath))
        {
            Debug.WriteLine($"[Everything] Missing: {BundledPath}");
            Status = EverythingStatus.Unavailable;
            return;
        }

        try
        {
            Directory.CreateDirectory(DataFolder);
            string settings = Path.Combine(DataFolder, "Everything.ini");
            WriteSettings(settings, IsFullSpeed);
            var start = new ProcessStartInfo(BundledPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-startup", "-config", settings, "-db", Path.Combine(DataFolder, "Everything.db") },
            };
            _own = Process.Start(start);
            Status = EverythingStatus.Starting;
            Watch();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Debug.WriteLine($"[Everything] Not started: {ex.Message}");
            Status = EverythingStatus.Unavailable;
        }
    }

    /// <summary>
    /// Files and folders for <paramref name="search"/> (Everything's syntax), most recently changed first; <c>null</c>
    /// when no Everything answers (it is started again for next time), or when a newer search replaced this one.
    /// </summary>
    public async Task<IReadOnlyList<EverythingItem>?> SearchAsync(string search, int maxResults)
    {
        var items = await _client.QueryAsync(search, maxResults);
        if (items is null && EverythingClient.FindEverything() == 0)
        {
            Start();
        }

        return items;
    }

    /// <summary>
    /// Full speed, with one administrator prompt: the copy of Everything goes to Program Files (where only an
    /// administrator can change it: the service runs it with all rights) and installs the Everything service from there.
    /// The notch's copy then restarts to use it. <c>false</c> if the user said no, or it failed.
    /// </summary>
    public async Task<bool> EnableFullSpeedAsync()
    {
        if (IsFullSpeed)
        {
            return true;
        }

        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ilot", "Everything");
        string target = Path.Combine(folder, "Everything.exe");
        string script = $"/c mkdir \"{folder}\" 2>nul & copy /y \"{BundledPath}\" \"{target}\" >nul && \"{target}\" -install-service";
        try
        {
            using var install = Process.Start(new ProcessStartInfo("cmd.exe", script)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (install is not null)
            {
                await install.WaitForExitAsync();
            }
        }
        catch (Win32Exception ex)
        {
            Debug.WriteLine($"[Everything] Full speed declined: {ex.Message}");
            return false;
        }

        // The service takes a moment to come up.
        for (int i = 0; i < 20 && !IsFullSpeed; i++)
        {
            await Task.Delay(250);
        }

        if (!IsFullSpeed)
        {
            return false;
        }

        await RestartOwnAsync();
        return true;
    }

    /// <summary>Tells Everything a file was opened from the notch: it ranks it higher next time.</summary>
    public void CountRun(string path) => _client.CountRun(path);

    public void Dispose()
    {
        _watch.Stop();
        StopOwn();
        _client.Dispose();
    }

    private void Watch()
    {
        _watchingSince = Stopwatch.GetTimestamp();
        Check();
        if (Status != EverythingStatus.Ready)
        {
            _watch.Start();
        }
    }

    /// <summary>
    /// Asks Everything whether its index is ready, from another thread: loading it, Everything may take a second to
    /// answer, and the notch must not freeze meanwhile. One question at a time.
    /// </summary>
    private async void Check()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        nint everything = EverythingClient.FindEverything();
        bool loaded = everything != 0 && await Task.Run(() => EverythingClient.IsDatabaseLoaded(everything));
        _checking = false;
        if (loaded)
        {
            _watch.Stop();
            Status = EverythingStatus.Ready;
            return;
        }

        if (_own is { HasExited: true } && everything == 0)
        {
            _watch.Stop();
            _own = null;
            Status = EverythingStatus.Unavailable;
            return;
        }

        Status = EverythingStatus.Starting;
        if (Stopwatch.GetElapsedTime(_watchingSince) > GiveUpAfter)
        {
            _watch.Stop(); // Still indexing: searches get what there is, and the next one looks again.
        }
    }

    private async Task RestartOwnAsync()
    {
        if (_own is null)
        {
            return; // The user's own Everything: theirs to restart.
        }

        StopOwn();
        for (int i = 0; i < 20 && EverythingClient.FindEverything() != 0; i++)
        {
            await Task.Delay(150);
        }

        Start();
    }

    /// <summary>The process behind <paramref name="everything"/>, if it is the notch's copy; <c>null</c> for the user's own.</summary>
    private static Process? OwnCopy(nint everything)
    {
        if (NativeMethods.GetWindowThreadProcessId(everything, out uint id) == 0)
        {
            return null;
        }

        try
        {
            var process = Process.GetProcessById((int)id);
            if (string.Equals(process.MainModule?.FileName, BundledPath, StringComparison.OrdinalIgnoreCase))
            {
                return process;
            }

            process.Dispose();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Gone meanwhile, or not ours to look into (run as administrator): the user's.
        }

        return null;
    }

    /// <summary>Closes the notch's copy (never the user's own Everything); its index is saved for next time.</summary>
    private void StopOwn()
    {
        if (_own is not { HasExited: false } own)
        {
            return;
        }

        nint everything = EverythingClient.FindEverything();
        if (everything != 0 && NativeMethods.GetWindowThreadProcessId(everything, out uint process) != 0 && process == own.Id)
        {
            EverythingClient.Exit(everything);
        }

        own.Dispose();
        _own = null;
    }

    /// <summary>
    /// The copy's settings: out of sight (no window, no tray icon, no update check), sizes and dates indexed, recent
    /// first ready to sort. With the service: every fixed disk. Without: the user's folder (no rights needed). Written
    /// when missing or when the mode changes; Everything keeps the rest of the file up to date itself.
    /// </summary>
    private static void WriteSettings(string path, bool fullSpeed)
    {
        string mode = fullSpeed ? "volumes" : "folders";
        string marker = Path.Combine(DataFolder, "mode.txt");
        if (File.Exists(path) && File.Exists(marker) && File.ReadAllText(marker) == mode)
        {
            return;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace("\\", "\\\\", StringComparison.Ordinal);
        var lines = new List<string>
        {
            "[Everything]",
            "run_in_background=1",
            "show_tray_icon=0",
            "show_in_taskbar=0",
            "check_for_updates_on_startup=0",
            "run_as_admin=0",
            "index_size=1",
            "index_date_modified=1",
            "fast_date_modified_sort=1",
            "run_history_enabled=1",
            "auto_include_removable_volumes=0",
            "auto_include_removable_refs_volumes=0",
        };
        lines.AddRange(fullSpeed
            ? ["auto_include_fixed_volumes=1", "auto_include_fixed_refs_volumes=1"]
            : ["auto_include_fixed_volumes=0", "auto_include_fixed_refs_volumes=0", $"folders=\"{home}\"", "folder_monitor_changes=1"]);
        File.WriteAllLines(path, lines);
        File.WriteAllText(marker, mode);
    }
}
