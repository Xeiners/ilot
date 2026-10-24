using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using MyNotch.Interop;

using MyNotch.Localization;

namespace MyNotch.Modules.Code;

/// <summary>
/// VS Code's recent projects, read from its own state (<c>%APPDATA%\Code\User\globalStorage\state.vscdb</c>, a
/// SQLite database, key <c>history.recentlyOpenedPathsList</c>), and the way to open them.
/// </summary>
/// <remarks>
/// Read on demand only (when the tab is shown), off the UI thread. VS Code may be writing to its database: the read
/// waits a moment, then falls back to a copy of the file. When its list is empty (cleared), the projects it keeps
/// state for (<c>workspaceStorage</c>) stand in.
/// </remarks>
public sealed class CodeService
{
    private const string HistoryKey = "history.recentlyOpenedPathsList";
    private const int MaxProjects = 30;
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromMilliseconds(400);

    // Stable first, Insiders if that is the only one around.
    private static readonly (string Folder, string Executable)[] Editions =
    [
        ("Code", "Code.exe"),
        ("Code - Insiders", "Code - Insiders.exe"),
    ];

    /// <summary>VS Code's state database, if VS Code has ever run here.</summary>
    public static string? DatabasePath => Editions
        .Select(edition => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), edition.Folder, "User", "globalStorage", "state.vscdb"))
        .FirstOrDefault(File.Exists);

    /// <summary>VS Code itself: launched directly (no console window flashing, as with <c>code.cmd</c>).</summary>
    public static string? Executable { get; } = FindExecutable();

    public static bool IsInstalled => Executable is not null || DatabasePath is not null;

    /// <summary>The recent projects, most recent first; those gone from the disk are left out.</summary>
    /// <exception cref="IOException">The database cannot be read.</exception>
    public Task<IReadOnlyList<RecentProject>> GetRecentAsync() => Task.Run(Read);

    public static void Open(RecentProject project)
    {
        var start = Executable is { } executable
            ? new ProcessStartInfo(executable) { UseShellExecute = false }
            : new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/c", "code" } };
        switch (project.Kind)
        {
            case ProjectKind.Remote when project.Location.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase):
                start.ArgumentList.Add("--file-uri");
                break;

            case ProjectKind.Remote:
                start.ArgumentList.Add("--folder-uri");
                break;
        }

        start.ArgumentList.Add(project.Location);
        Process.Start(start)?.Dispose();
    }

    /// <summary>Shows the project in the Explorer: the folder itself, or the workspace file selected in its folder.</summary>
    public static void Reveal(RecentProject project)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        if (project.Kind == ProjectKind.Workspace)
        {
            start.Arguments = $"/select,\"{project.Location}\"";
        }
        else
        {
            start.ArgumentList.Add(project.Location);
        }

        Process.Start(start)?.Dispose();
    }

    /// <summary>
    /// VS Code's own list ("File › Open Recent") first, in its order; then every project VS Code keeps state for
    /// (one folder each in <c>workspaceStorage</c>), most recently used first. The second fills in when the list was
    /// cleared, or is short.
    /// </summary>
    private static IReadOnlyList<RecentProject> Read()
    {
        var projects = new List<RecentProject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IOException? failure = null;
        try
        {
            if (DatabasePath is { } path && ReadHistory(path) is { } history)
            {
                Add(ParseHistory(history));
            }
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[Code] {ex.Message}: {ex.InnerException?.Message}");
            failure = ex;
        }

        Add(ReadWorkspaceStorage());
        if (projects.Count == 0 && failure is not null)
        {
            throw failure;
        }

        return projects;

        void Add(IEnumerable<RecentProject> found)
        {
            foreach (var project in found)
            {
                if (projects.Count < MaxProjects && seen.Add(project.Location))
                {
                    projects.Add(project);
                }
            }
        }
    }

    private static byte[]? ReadHistory(string path)
    {
        try
        {
            return WinSqlite.ReadValue(path, "ItemTable", "key", "value", HistoryKey, BusyTimeout);
        }
        catch (SqliteException ex) when (ex.IsLocked)
        {
            Debug.WriteLine($"[Code] Database busy, reading a copy: {ex.Message}");
            return ReadCopy(path);
        }
        catch (Exception ex) when (ex is SqliteException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new IOException("Historique de VS Code illisible", ex);
        }
    }

    /// <summary>
    /// <c>workspaceStorage\&lt;id&gt;\workspace.json</c>: <c>{"folder": "file:///…"}</c> or <c>{"workspace": "…"}</c>.
    /// When a project was last used is when VS Code last saved its state there.
    /// </summary>
    private static IEnumerable<RecentProject> ReadWorkspaceStorage()
    {
        var found = new List<(DateTime Used, RecentProject Project)>();
        foreach (string folder in Editions.Select(edition => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), edition.Folder, "User", "workspaceStorage")))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (string directory in Directory.EnumerateDirectories(folder))
            {
                try
                {
                    string file = Path.Combine(directory, "workspace.json");
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    using var json = JsonDocument.Parse(File.ReadAllBytes(file));
                    var root = json.RootElement;
                    var project = root.TryGetProperty("folder", out var uri) ? ToProject(uri.GetString() ?? "", workspace: false)
                        : root.TryGetProperty("workspace", out uri) ? ToProject(uri.GetString() ?? "", workspace: true)
                        : null;
                    if (project is not null)
                    {
                        string state = Path.Combine(directory, "state.vscdb");
                        found.Add((File.Exists(state) ? File.GetLastWriteTimeUtc(state) : Directory.GetLastWriteTimeUtc(directory), project));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    Debug.WriteLine($"[Code] Skipped {directory}: {ex.Message}");
                }
            }
        }

        return found.OrderByDescending(entry => entry.Used).Select(entry => entry.Project);
    }

    /// <summary>VS Code holds the database: read a copy of it (with its write-ahead log, so nothing recent is missed).</summary>
    private static byte[]? ReadCopy(string path)
    {
        string folder = Path.Combine(Path.GetTempPath(), "MyNotch", $"vscode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string copy = Path.Combine(folder, "state.vscdb");
            File.Copy(path, copy);
            if (File.Exists(path + "-wal"))
            {
                File.Copy(path + "-wal", copy + "-wal");
            }

            return WinSqlite.ReadValue(copy, "ItemTable", "key", "value", HistoryKey, BusyTimeout);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw new IOException("Historique de VS Code illisible", ex);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A temporary copy: Windows cleans the temp folder anyway.
            }
        }
    }

    /// <summary>
    /// <c>{"entries":[{"folderUri":"file:///c%3A/dev/app"}, {"workspace":{"configPath":"file:///…/x.code-workspace"}},
    /// {"folderUri":"vscode-remote://wsl%2Bubuntu/home/me/app","remoteAuthority":"wsl+ubuntu"}, {"fileUri":…}]}</c>.
    /// Single files are left out: this is about projects.
    /// </summary>
    private static List<RecentProject> ParseHistory(byte[] json)
    {
        var projects = new List<RecentProject>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return projects;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                string? uri = null;
                bool workspace = false;
                if (entry.TryGetProperty("folderUri", out var folder))
                {
                    uri = folder.GetString();
                }
                else if (entry.TryGetProperty("workspace", out var space) && space.TryGetProperty("configPath", out var config))
                {
                    uri = config.GetString();
                    workspace = true;
                }

                if (uri is not null && ToProject(uri, workspace) is { } project)
                {
                    projects.Add(project);
                }
            }
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[Code] History not understood: {ex.Message}");
        }

        return projects;
    }

    private static RecentProject? ToProject(string text, bool workspace)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.IsFile)
        {
            // Still on the disk?
            string path = LocalPath(uri);
            if (workspace ? !File.Exists(path) : !Directory.Exists(path))
            {
                return null;
            }

            string name = workspace ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path) is { Length: > 0 } leaf ? leaf : path;
            return new RecentProject(
                name,
                path,
                workspace ? ProjectKind.Workspace : ProjectKind.Folder,
                Abbreviate(Path.GetDirectoryName(path) ?? path),
                workspace ? ProjectStack.ForWorkspace : ProjectStack.Detect(path));
        }

        // vscode-remote://wsl%2Bubuntu/home/me/app: a folder on another machine.
        string remotePath = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        string remoteName = remotePath[(remotePath.LastIndexOf('/') + 1)..];
        string parent = remotePath.LastIndexOf('/') is > 0 and var slash ? remotePath[..slash] : "/";
        return new RecentProject(
            workspace ? Path.GetFileNameWithoutExtension(remoteName) : remoteName is { Length: > 0 } ? remoteName : "/",
            uri.OriginalString,
            ProjectKind.Remote,
            $"{DescribeRemote(Uri.UnescapeDataString(uri.Authority))} · {parent}");
    }

    /// <summary>
    /// file:///c%3A/dev/app → C:\dev\app; file://server/share/app → \\server\share\app. VS Code encodes the drive's
    /// colon, which <see cref="Uri.LocalPath"/> does not recognise as a drive.
    /// </summary>
    private static string LocalPath(Uri uri)
    {
        string path = Uri.UnescapeDataString(uri.AbsolutePath).Replace('/', '\\');
        if (!string.IsNullOrEmpty(uri.Host))
        {
            return Path.TrimEndingDirectorySeparator($@"\\{uri.Host}{path}");
        }

        if (path.Length >= 3 && path[0] == '\\' && path[2] == ':')
        {
            path = char.ToUpperInvariant(path[1]) + path[2..];
        }

        return path.Length > 3 ? Path.TrimEndingDirectorySeparator(path) : path;
    }

    /// <summary>"wsl+Ubuntu" → "WSL : Ubuntu", "ssh-remote+server" → "SSH : server"…</summary>
    private static string DescribeRemote(string authority)
    {
        string[] parts = authority.Split('+', 2);
        string host = parts.Length > 1 ? parts[1] : "";
        string kind = parts[0] switch
        {
            "wsl" => "WSL",
            "ssh-remote" => "SSH",
            "dev-container" or "attached-container" => Loc.T("Conteneur"),
            "codespaces" => "Codespaces",
            "tunnel" => "Tunnel",
            var other => other,
        };

        // Containers and codespaces name themselves with long encoded ids: the kind says enough.
        return host.Length is > 0 and <= 24 ? $"{kind} : {host}" : kind;
    }

    /// <summary>Paths in the user's folder start with "~"; long ones keep their beginning and end.</summary>
    private static string Abbreviate(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
        {
            path = "~" + path[home.Length..];
        }

        const int Longest = 46;
        return path.Length <= Longest ? path : path[..18] + "…" + path[^(Longest - 19)..];
    }

    private static string? FindExecutable()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var (folder, executable) in Editions)
        {
            string product = folder == "Code" ? "Microsoft VS Code" : "Microsoft VS Code Insiders";
            foreach (string root in new[] { Path.Combine(local, "Programs"), programs })
            {
                string candidate = Path.Combine(root, product, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // Installed elsewhere: its "bin" folder (with code.cmd) is on the PATH, next to the program.
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, "code.cmd"))
                    && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)) is { } install
                    && File.Exists(Path.Combine(install, "Code.exe")))
                {
                    return Path.Combine(install, "Code.exe");
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        return null;
    }
}
