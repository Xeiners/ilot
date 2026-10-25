using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Ilot.Localization;
using Ilot.Settings;

namespace Ilot.Updates;

/// <summary>Where an update stands.</summary>
public enum UpdateState
{
    /// <summary>Not looked for yet.</summary>
    Idle,

    Checking,

    /// <summary>The latest release is this version (or none is out yet).</summary>
    UpToDate,

    /// <summary>A newer version is out, but this copy (run from the sources) does not install it itself.</summary>
    Available,

    Downloading,

    /// <summary>A newer version is downloaded and checked: ready to install.</summary>
    Ready,

    /// <summary>GitHub could not be reached, or the download was not right; the next check tries again.</summary>
    Failed,
}

/// <summary>
/// Ilot keeps itself up to date from its releases on GitHub (Xeiners/ilot, public): half a minute after it starts, then
/// every six hours, one request to GitHub's API (no key); a newer version's installer is downloaded in the background
/// (to <c>%LOCALAPPDATA%\Ilot\Updates</c>), its size and SHA-256 checked, and the notch offers it (see UpdateModule).
/// Installing runs it silently: Ilot quits, the installer replaces it, and starts it again (installer\Ilot.iss). Put off
/// (« Plus tard »), it goes in the next time Ilot starts. Only a copy installed by the installer updates itself, never
/// one run from the sources. Nothing runs between two checks.
/// </summary>
public sealed partial class AppUpdater : ObservableObject, IDisposable
{
    private const string LatestRelease = "https://api.github.com/repos/Xeiners/ilot/releases/latest";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan DownloadFor = TimeSpan.FromMinutes(15);
    private static readonly HttpClient Http = CreateClient();

    private readonly AppSettings _settings;
    private readonly DispatcherQueueTimer _timer;
    private UpdateState _state;
    private string _statusText = "";
    private Version? _available; // The newer version found.
    private string? _installer; // Its installer, downloaded and checked.
    private bool _busy;

    public AppUpdater(AppSettings settings, DispatcherQueue dispatcher)
    {
        _settings = settings;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += async (_, _) =>
        {
            await CheckAsync();
            Schedule(CheckEvery);
        };
        settings.PropertyChanged += OnSettingsChanged;
        ShowStatus();
    }

    /// <summary>This version (Directory.Build.props), on three parts.</summary>
    public static Version Current => UpdateRelease.Normalize(typeof(AppUpdater).Assembly.GetName().Version ?? new Version(1, 0, 0));

    /// <summary>This copy was installed (its uninstaller next to it): only such a copy updates itself.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public UpdateState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    /// <summary>Where it stands, in a few words (the settings show it).</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>The newer version found, if any.</summary>
    public Version? Available => _available;

    /// <summary>Raised when a newer version is downloaded and ready: the notch offers it.</summary>
    public event EventHandler? Ready;

    /// <summary>Raised just before the installer starts and Ilot quits: the single instance lets go, for the installer.</summary>
    public event EventHandler? Installing;

    private static string Updates => Path.Combine(AppData.Folder, "Updates");

    /// <summary>
    /// At startup: a version downloaded before and put off goes in now, automatically, once (should that installation
    /// fail, it is offered rather than tried at every start); installers already used are cleared; the checks start.
    /// </summary>
    public void Start()
    {
        if (IsInstalled && FindDownloaded() is { } pending)
        {
            _installer = pending.Path;
            _available = pending.Version;
            string tried = pending.Path + ".tried";
            if (_settings.AutoUpdate && !File.Exists(tried))
            {
                File.WriteAllText(tried, "");
                Install();
                return;
            }

            SetReady();
        }

        ClearOld();
        if (IsInstalled && _settings.AutoUpdate)
        {
            Schedule(FirstCheck);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _settings.PropertyChanged -= OnSettingsChanged;
    }

    /// <summary>
    /// Asks GitHub for the latest release; a newer one is downloaded (an installed copy) and offered. Asked from the
    /// settings (« Rechercher ») as well as on schedule.
    /// </summary>
    public async Task CheckAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            State = UpdateState.Checking;
            StatusText = Loc.T("Recherche d'une mise à jour…");
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                SetUpToDate(); // No release out yet.
                return;
            }

            response.EnsureSuccessStatusCode();
            var release = UpdateRelease.Parse(await response.Content.ReadAsStringAsync());
            if (release is null || release.Version <= Current)
            {
                SetUpToDate();
                return;
            }

            _available = release.Version;
            string version = release.Version.ToString(3);
            if (!IsInstalled)
            {
                State = UpdateState.Available;
                StatusText = Loc.F("Ilot {0} est sortie (cette copie est la {1}, lancée depuis les sources : elle ne se met pas à jour toute seule).",
                    version, Current.ToString(3));
                return;
            }

            await DownloadAsync(release);
            SetReady();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException
            or UnauthorizedAccessException or InvalidDataException)
        {
            ErrorLog.Write($"[Update] {ex.Message}");
            State = UpdateState.Failed;
            StatusText = Loc.T("Impossible de vérifier les mises à jour pour l'instant : nouvel essai plus tard.");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Runs the installer, silently (it closes nothing but Ilot, keeps the choices made at installation, and starts Ilot
    /// again once done), then quits. Installed for all users, Windows asks for the administrator's rights.
    /// </summary>
    public void Install()
    {
        if (_installer is not { } path || !File.Exists(path) || !IsInstalled)
        {
            return;
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        bool everyone = !AppContext.BaseDirectory.StartsWith(local, StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS " + (everyone ? "/ALLUSERS" : "/CURRENTUSER"),
        };
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Win32Exception ex)
        {
            // Refused (the administrator's rights, asked for an installation for all users): it stays as it is.
            ErrorLog.Write($"[Update] Not started: {ex.Message}");
            StatusText = Loc.T("La mise à jour n'a pas pu démarrer.");
            return;
        }

        Installing?.Invoke(this, EventArgs.Empty);
        Application.Current.Exit();
    }

    /// <summary>The installer, in the updates' folder, then checked (size, SHA-256) before it is kept under its name.</summary>
    private async Task DownloadAsync(UpdateRelease release)
    {
        Directory.CreateDirectory(Updates);
        string version = release.Version.ToString(3);
        string path = Path.Combine(Updates, $"Ilot-Setup-{version}.exe");
        if (File.Exists(path))
        {
            _installer = path; // Downloaded and checked before.
            return;
        }

        State = UpdateState.Downloading;
        StatusText = Loc.F("Téléchargement d'Ilot {0}…", version);
        string part = path + ".part";
        using (var cancel = new CancellationTokenSource(DownloadFor))
        {
            using var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancel.Token);
            await using var target = File.Create(part);
            byte[] buffer = new byte[81920];
            long done = 0;
            int shown = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel.Token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel.Token);
                done += read;
                int percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent != shown)
                {
                    shown = percent;
                    StatusText = Loc.F("Téléchargement d'Ilot {0}… {1} %", version, percent);
                }
            }
        }

        if (!Matches(part, release))
        {
            File.Delete(part);
            throw new InvalidDataException("The installer downloaded is not the one GitHub describes.");
        }

        File.Move(part, path, overwrite: true);
        _installer = path;
    }

    /// <summary>The file is the release's installer: its size, and its SHA-256 when GitHub gives it.</summary>
    private static bool Matches(string path, UpdateRelease release)
    {
        if (release.Size > 0 && new FileInfo(path).Length != release.Size)
        {
            return false;
        }

        if (release.Sha256 is not { } expected)
        {
            return true;
        }

        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file)) == expected;
    }

    /// <summary>A newer version's installer, downloaded before and not installed yet (put off): the newest.</summary>
    private static (string Path, Version Version)? FindDownloaded()
    {
        if (!Directory.Exists(Updates))
        {
            return null;
        }

        (string Path, Version Version)? newest = null;
        foreach (string file in Directory.EnumerateFiles(Updates, "Ilot-Setup-*.exe"))
        {
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && VersionOf(file) is { } version && version > Current
                && (newest is null || version > newest.Value.Version))
            {
                newest = (file, version);
            }
        }

        return newest;
    }

    /// <summary>The installers of this version or older (and their marks), and unfinished downloads: done with.</summary>
    private static void ClearOld()
    {
        if (!Directory.Exists(Updates))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(Updates))
        {
            if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || VersionOf(file) is not { } version || version <= Current)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"[Update] {file} kept: {ex.Message}");
                }
            }
        }
    }

    private static Version? VersionOf(string file) =>
        InstallerName().Match(Path.GetFileName(file)) is { Success: true } match && Version.TryParse(match.Groups[1].Value, out var version)
            ? UpdateRelease.Normalize(version)
            : null;

    // An installer, or the mark of its automatic installation tried at startup.
    [GeneratedRegex(@"^Ilot-Setup-(\d+\.\d+\.\d+)\.exe(\.tried)?$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerName();

    private void SetReady()
    {
        State = UpdateState.Ready;
        StatusText = Loc.F("Ilot {0} est prête (vous avez la {1}) : elle s'installe en quelques secondes.",
            _available?.ToString(3) ?? "", Current.ToString(3));
        Ready?.Invoke(this, EventArgs.Empty);
    }

    private void SetUpToDate()
    {
        _available = null;
        State = UpdateState.UpToDate; // An offer made earlier no longer stands: the notch takes it back (UpdateModule).
        StatusText = Loc.F("Ilot {0} est à jour.", Current.ToString(3));
    }

    private void ShowStatus() => StatusText = IsInstalled
        ? Loc.F("Ilot {0}.", Current.ToString(3))
        : Loc.F("Ilot {0}, lancée depuis les sources : seule la version installée se met à jour toute seule.", Current.ToString(3));

    private void Schedule(TimeSpan after)
    {
        _timer.Stop();
        if (_settings.AutoUpdate && IsInstalled)
        {
            _timer.Interval = after;
            _timer.Start();
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.AutoUpdate))
        {
            Schedule(FirstCheck); // On: soon; off: no more (Schedule stops it).
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Ilot/{Current.ToString(3)}"); // GitHub's API wants one.
        return client;
    }
}
