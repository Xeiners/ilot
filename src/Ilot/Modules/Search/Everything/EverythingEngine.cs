using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.UI.Dispatching;
using Ilot.Interop;
using Ilot.Settings;

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

    /// <summary>The ignored folders changed in the settings: the index follows once the typing is over.</summary>
    private static readonly TimeSpan ReindexAfter = TimeSpan.FromSeconds(4);

    private static readonly string DataFolder = Path.Combine(
        AppData.Folder, "Everything");

    private readonly AppSettings _settings;
    private readonly EverythingClient _client = new();
    private readonly DispatcherQueueTimer _watch;
    private readonly DispatcherQueueTimer _reindex;
    private Process? _own;
    private long _watchingSince;
    private bool _checking;
    private EverythingStatus _status = EverythingStatus.Unavailable;

    public EverythingEngine(DispatcherQueue dispatcher, AppSettings settings)
    {
        _settings = settings;

        // Only while Everything starts: once its index is ready, nothing runs any more.
        _watch = dispatcher.CreateTimer();
        _watch.Interval = CheckEvery;
        _watch.Tick += (_, _) => Check();

        // What the search ignores is not indexed either (see Excluded): changed, the copy starts again with it.
        _reindex = dispatcher.CreateTimer();
        _reindex.Interval = ReindexAfter;
        _reindex.IsRepeating = false;
        _reindex.Tick += async (_, _) =>
        {
            if (_own is not null && File.Exists(MarkerPath) && File.ReadAllText(MarkerPath) != Marker(IsFullSpeed))
            {
                await RestartOwnAsync();
            }
        };
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.SearchExclusions))
            {
                _reindex.Stop();
                _reindex.Start();
            }
        };
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
            // The notch's own copy, left running (the notch did not close properly): it is the notch's again. Started with
            // other settings (an older Ilot, other ignored folders): started again with these.
            _own ??= OwnCopy(running);
            if (_own is not null && (!File.Exists(MarkerPath) || File.ReadAllText(MarkerPath) != Marker(IsFullSpeed)))
            {
                _ = RestartOwnAsync();
                return;
            }

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
            WriteSettings(settings);
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
    /// <summary>
    /// <paramref name="search"/> as Everything reads it, minus the folders left out (<paramref name="exclusions"/>, one per
    /// line, as the settings keep them): the search bar's and the assistant's alike.
    /// </summary>
    public static string WithExclusions(string search, string exclusions)
    {
        var query = new StringBuilder(search.Trim());
        // One per line: "\n" as saved, "\r" as the settings' text field writes them.
        foreach (string line in exclusions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            query.Append(" !\"").Append(line.Replace("\"", "", StringComparison.Ordinal)).Append('"');
        }

        return query.ToString();
    }

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

        // Gone for good before its settings are written again: leaving, it writes its own (the old ones) over them.
        var leaving = StopOwn(keep: true);
        for (int i = 0; i < 20 && EverythingClient.FindEverything() != 0; i++)
        {
            await Task.Delay(150);
        }

        if (leaving is not null)
        {
            try
            {
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await leaving.WaitForExitAsync(patience.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or Win32Exception)
            {
                Debug.WriteLine($"[Everything] Still leaving: {ex.Message}");
            }
            finally
            {
                leaving.Dispose();
            }
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

    /// <summary>
    /// Closes the notch's copy (never the user's own Everything); its index is saved for next time. With
    /// <paramref name="keep"/>, its process is handed back to wait for it to be gone (the caller disposes of it).
    /// </summary>
    private Process? StopOwn(bool keep = false)
    {
        if (_own is not { HasExited: false } own)
        {
            return null;
        }

        nint everything = EverythingClient.FindEverything();
        if (everything != 0 && NativeMethods.GetWindowThreadProcessId(everything, out uint process) != 0 && process == own.Id)
        {
            EverythingClient.Exit(everything);
        }

        _own = null;
        if (keep)
        {
            return own;
        }

        own.Dispose();
        return null;
    }

    /// <summary>
    /// What the copy's settings are made of (see <see cref="WriteSettings"/>): one more, and they are written again at its
    /// next start (2: the ignored folders not indexed, the sorts the notch never asks for not kept ready).
    /// </summary>
    private const int SettingsVersion = 2;

    /// <summary>What the copy's settings were written from (see <see cref="Marker"/>).</summary>
    private static string MarkerPath => Path.Combine(DataFolder, "mode.txt");

    /// <summary>What the copy's settings are written from: the mode, their version, the folders left out.</summary>
    private string Marker(bool fullSpeed) =>
        $"{(fullSpeed ? "volumes" : "folders")};{SettingsVersion}\n{string.Join("\n", Excluded(_settings.SearchExclusions))}";

    /// <summary>
    /// The copy's settings: out of sight (no window, no tray icon, no update check), sizes and dates indexed, recent
    /// first ready to sort (the only order the notch asks for: the other sorts are not kept ready, each would cost memory
    /// for every file). With the service: every fixed disk. Without: the user's folder (no rights needed). What the search
    /// ignores (<see cref="Excluded"/>) is not indexed at all: never shown, it took most of the memory (on the PC this was
    /// measured on, AppData, node_modules and Windows made about 2.5 million of the 3.3 million entries). Written when
    /// missing, when the mode, these settings (<see cref="SettingsVersion"/>) or the ignored folders change; Everything keeps
    /// the rest of the file up to date itself, and filters an index already built at its next start.
    /// </summary>
    private void WriteSettings(string path)
    {
        bool fullSpeed = IsFullSpeed;
        string marker = Marker(fullSpeed);
        if (File.Exists(path) && File.Exists(MarkerPath) && File.ReadAllText(MarkerPath) == marker)
        {
            return;
        }

        string home = Escape(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var lines = new List<string>
        {
            "[Everything]",
            "run_in_background=1",
            "show_tray_icon=0",
            "show_in_taskbar=0",
            "check_for_updates_on_startup=0",
            "run_as_admin=0",
            "index_size=1",
            "fast_size_sort=0",
            "index_date_modified=1",
            "fast_date_modified_sort=1",
            "fast_path_sort=0",
            "index_recent_changes=0",
            "run_history_enabled=1",
            "auto_include_removable_volumes=0",
            "auto_include_removable_refs_volumes=0",
            "exclude_list_enabled=1",
            "exclude_folders=" + string.Join(",", Excluded(_settings.SearchExclusions).Select(folder => $"\"{Escape(folder)}\"")),
        };
        lines.AddRange(fullSpeed
            ? ["auto_include_fixed_volumes=1", "auto_include_fixed_refs_volumes=1"]
            : ["auto_include_fixed_volumes=0", "auto_include_fixed_refs_volumes=0", $"folders=\"{home}\"", "folder_monitor_changes=1"]);
        File.WriteAllLines(path, lines);
        File.WriteAllText(MarkerPath, marker);

        // Inside double quotes, Everything reads a backslash written twice as one.
        static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// The search's ignored folders (<see cref="AppSettings.SearchExclusions"/>, one per line, never shown in its results)
    /// as Everything's index leaves them out, so they take no memory either: « \node_modules\ » (anywhere) is any folder
    /// of that name (« node_modules »), « \ProgramData\Microsoft\ » that pair anywhere (« *\ProgramData\Microsoft »),
    /// « C:\Windows\ » that folder (« C:\Windows »), each with all under it. Anything else (part of a name, a wildcard, a
    /// whole disk, a network share) is left to the search, which reads it as before. Checked with Everything 1.4.1 on a
    /// test tree: each form leaves out what it names, and only that (« node_modules_x », another « Temp » stay).
    /// </summary>
    internal static IEnumerable<string> Excluded(string ignored)
    {
        var folders = new List<string>();
        foreach (string line in ignored.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string folder = line.Replace("\"", "", StringComparison.Ordinal);
            if (folder.IndexOfAny(['*', '?', ',']) >= 0)
            {
                continue;
            }

            if (folder.Length > 3 && char.IsAsciiLetter(folder[0]) && folder[1] == ':' && folder[2] == '\\')
            {
                folders.Add(folder.TrimEnd('\\')); // « C:\ » alone, a whole disk: not here (Length > 3).
            }
            else if (folder.Length > 2 && folder[0] == '\\' && folder[1] != '\\' && folder[^1] == '\\')
            {
                string inner = folder.Trim('\\');
                folders.Add(inner.Contains('\\') ? "*\\" + inner : inner);
            }
        }

        return folders.Where(folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
