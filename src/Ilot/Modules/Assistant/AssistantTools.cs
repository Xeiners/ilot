using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Ilot.Localization;
using Ilot.Modules.Clipboard;
using Ilot.Modules.Code;
using Ilot.Modules.Search;
using Ilot.Modules.Search.Everything;
using Ilot.Settings;

namespace Ilot.Modules.Assistant;

/// <summary>Where an action of the assistant is at, as the conversation shows it.</summary>
public enum ActionState
{
    /// <summary>Waiting for the user to allow it, or not.</summary>
    Asking,
    Running,
    Done,

    /// <summary>The user said no.</summary>
    Refused,
    Failed,

    /// <summary>Done, then taken back by the user (« Annuler », Ctrl + Z): see FileJournal.</summary>
    Undone,
}

/// <summary>
/// Something the assistant does on the PC (a tool Gemini called), as a line of the conversation: what it is, where it is
/// at, how it went; and, for what must be allowed first, the user's answer (<see cref="Decide"/>).
/// </summary>
public sealed class AssistantAction(string label, string glyph, string? detail, string? remember = null)
{
    private TaskCompletionSource<(bool Allowed, bool Always)>? _decision;

    public string Label { get; } = label;

    /// <summary>A Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; } = glyph;

    /// <summary>Exactly what will be done (a command line, an address), shown while it waits to be allowed.</summary>
    public string? Detail { get; } = detail;

    /// <summary>What « Toujours autoriser » would allow for good, in words (« youtube.com »); <c>null</c>: it cannot be.</summary>
    public string? Remembers { get; } = remember is null ? null : AssistantTools.Scope(remember);

    public ActionState State { get; internal set; } = ActionState.Running;

    /// <summary>How it went, in a few words (« 12 fichiers », why it failed…).</summary>
    public string Outcome { get; internal set; } = "";

    /// <summary>What it showed Gemini, if it is a picture (the screen).</summary>
    public ChatImage? Image { get; internal set; }

    /// <summary>What it gave, to read under it (a command's output, as a terminal shows it); <c>null</c>: nothing to read.</summary>
    public string? Output { get; internal set; }

    /// <summary>What it made (a folder, a file), opened with a click on « Ouvrir » beside it; <c>null</c>: nothing.</summary>
    public string? Opens { get; internal set; }

    /// <summary>What it did to the user's files, in the journal: « Annuler » beside it takes it back (see FileJournal).</summary>
    public FileEntry? Entry { get; internal set; }

    /// <summary>Done, in a few words for the closed notch (« Dossier copié », « Fichier supprimé »); <c>null</c>: nothing to say there.</summary>
    public string? Said { get; internal set; }

    /// <summary>Said on the closed notch already (once is enough).</summary>
    internal bool Announced { get; set; }

    /// <summary>
    /// The user's answer: allowed, or not (the buttons under the action); <paramref name="always"/>: and from now on,
    /// without asking (« Toujours autoriser »). Nothing if it was not asked.
    /// </summary>
    public void Decide(bool allowed, bool always = false) => _decision?.TrySetResult((allowed, allowed && always));

    /// <summary>Waits for <see cref="Decide"/> (no answer if the conversation is started afresh meanwhile: cancelled).</summary>
    internal async Task<(bool Allowed, bool Always)> AskAsync(CancellationToken cancellation)
    {
        _decision = new TaskCompletionSource<(bool Allowed, bool Always)>(TaskCreationOptions.RunContinuationsAsynchronously);
        State = ActionState.Asking;
        using (cancellation.Register(() => _decision.TrySetCanceled(cancellation)))
        {
            return await _decision.Task;
        }
    }
}

/// <summary>
/// What a tool call will do, before it is done: the line shown for it, whether the user must allow it first (and
/// exactly what they allow), how to do it; or why it will not be done at all (<see cref="Refusal"/>, told to Gemini).
/// </summary>
public sealed record ToolPlan(string Label, string Glyph, Func<CancellationToken, Task<ToolOutcome>> Run)
{
    /// <summary>The user allows it first (see <see cref="AssistantAction.Decide"/>).</summary>
    public bool Confirm { get; init; }

    /// <summary>Exactly what is allowed (a command line, an address).</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// What « Toujours autoriser » keeps, so that it is not asked again (see <see cref="AppSettings.AssistantAllowed"/>):
    /// a site, a file, a program. <c>null</c>: asked each time.
    /// </summary>
    public string? Remember { get; init; }

    /// <summary>Not done at all (an unknown tool, a command not on the list…): why.</summary>
    public string? Refusal { get; init; }

    public static ToolPlan Refused(string label, string glyph, string why) =>
        new(label, glyph, _ => Task.FromResult(new ToolOutcome([], why, Failed: true))) { Refusal = why };
}

/// <summary>
/// What a tool gave: for Gemini (<paramref name="Response"/>, a JSON object), for the user (<paramref name="Note"/>), a
/// picture if it took one, a text to read if it gave one (<paramref name="Shown"/>: a command's output), and what it
/// made, to open with a click (<paramref name="Opens"/>: a folder or a file created).
/// </summary>
public sealed record ToolOutcome(JsonObject Response, string Note, bool Failed = false, ChatImage? Image = null, string? Shown = null, string? Opens = null, FileEntry? Entry = null, string? Said = null);

/// <summary>The assistant's tools: what Gemini is told it may call, and what each call does.</summary>
public interface IAssistantTools
{
    /// <summary>The function declarations sent with each question (Gemini's « tools »).</summary>
    JsonArray Declarations { get; }

    /// <summary>What calling <paramref name="name"/> with <paramref name="args"/> will do (nothing is done yet).</summary>
    ToolPlan Plan(string name, JsonObject args);
}

/// <summary>
/// What the assistant may do on the PC, when the user asks it to: see the screen, find files (Everything, as the search
/// does), read or write the clipboard, open a file, a folder, an app, a web page or a project in VS Code, and run a few
/// diagnostic commands that only read (see <see cref="ShellCommands"/>). Each action shows in the conversation; what could
/// do harm waits for the user's yes: a web page (what is in its address leaves the PC), a program or a script opened,
/// and the commands unless the settings say to run them directly. Nothing goes anywhere but to Gemini, as the rest of
/// the conversation does. The same tools serve the written conversation and the spoken one (see Voice/AssistantVoice).
/// </summary>
public sealed class AssistantTools(ScreenCaptureService screen, EverythingEngine everything, AppCatalog apps, AppSettings settings, FileJournal? journal = null) : IAssistantTools
{
    /// <summary>The clipboard's text given to Gemini is cut there.</summary>
    private const int MostClipboard = 8000;

    private const int MostFiles = 20;

    /// <summary>Opened without asking: folders' contents that only show (documents, pictures, sound, video, archives).</summary>
    private static readonly HashSet<string> Harmless = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".csv",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".heic", ".tif", ".tiff",
        ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".aac", ".mp4", ".mkv", ".avi", ".mov", ".webm", ".zip", ".7z", ".rar", ".epub",
    };

    public JsonArray Declarations { get; } =
    [
        Function("capture_screen",
            "Takes a screenshot of the user's screen (the one Ilot is on) and attaches it, so you can see what they see. Use it when they ask you to look at their screen, at what they are doing, or about something visible on it."),
        Function("search_files",
            "Finds files and folders on the user's PC by name (Everything's syntax works: ext:pdf, *.docx, words of the folder), most recently changed first.",
            ("query", Text("Words of the name to find."), true),
            ("max_results", new JsonObject { ["type"] = "INTEGER", ["description"] = "How many, 1 to 20 (10 by default)." }, false)),
        Function("manage_clipboard",
            "Reads the text on the user's clipboard, or copies a text to it (for them to paste).",
            ("action", new JsonObject { ["type"] = "STRING", ["enum"] = new JsonArray("read", "write"), ["description"] = "read, or write." }, true),
            ("text", Text("The text to copy (write only)."), false)),
        Function("open_file_or_app",
            "Opens a file or a folder (full path), a web address (https://…), a Windows settings page (ms-settings:…), or an installed app by its name; or a folder or a file in Visual Studio Code. Opening a web page, a program or a script waits for the user's approval.",
            ("target", Text("A full path, an address, the name of an installed app (« Spotify », « Calculatrice »), or one of the user's folders by name, "
                + "possibly followed by the rest: Downloads, Documents, Desktop, Pictures, Music, Videos, Home (« Downloads », « Documents\\Factures »). "
                + "Never guess a C:\\Users\\… path: you do not know the user's name (C:\\Users\\Public is not theirs)."), true),
            ("in_vscode", new JsonObject { ["type"] = "BOOLEAN", ["description"] = "Open the folder or file in Visual Studio Code." }, false)),
        Function("execute_shell_command",
            "Runs one read-only diagnostic command on the PC (the user may have to approve it first). No shell: no pipes, redirections or chained commands. Only these programs: "
            + "ipconfig (/all), ping (-n up to 10), nslookup, hostname, whoami, systeminfo, tasklist, getmac, driverquery, netstat, where, "
            + "git (status, log, diff, show, branch, remote, tag, rev-parse, describe, shortlog, ls-files, blame), "
            + "dotnet (--info, --version, --list-sdks, --list-runtimes), node/python/py/java (--version), winget (list, search, show).",
            ("command", Text("The command line, e.g. « ipconfig /all » or « git log -5 --oneline »."), true),
            ("working_directory", Text("The folder to run it in (a repository, for git)."), false)),
        Function("create_folder",
            "Creates a new folder on the user's PC (the user approves it first). Only creates: nothing is ever deleted, moved or replaced; "
            + "an existing folder is left as it is. Only in the user's own folders or on another drive, never in Windows' or programs' folders.",
            ("path", Text(PathHelp + " E.g. « Documents\\Factures 2026 » or « D:\\Projets\\Site »."), true)),
        Function("create_text_file",
            "Creates a new plain text file (.txt, .md or .csv) with the given content (the user approves it first, seeing its beginning). "
            + "Never replaces an existing file. Only in the user's own folders or on another drive.",
            ("path", Text(PathHelp + " The file's name ends with .txt, .md or .csv (.txt if none). E.g. « Bureau\\Courses.txt »."), true),
            ("content", Text("The text to write in it."), true)),
        Function("copy_item",
            "Copies a file or a folder (with all it holds) into another folder (the user approves it first). Never replaces anything: "
            + "if the destination already holds that name, give a new_name. The copy goes only where create_folder could create.",
            ("source", Text("The file or folder to copy. " + PathHelp), true),
            ("destination_folder", Text("The folder to copy it into (made if missing). " + PathHelp), true),
            ("new_name", Text("Another name for the copy (optional)."), false)),
        Function("move_item",
            "Moves a file or a folder into another folder, or renames it (same folder, new_name), after the user approves. "
            + "Never replaces anything. Only from and to the user's own folders or another drive; never the user's folders themselves "
            + "(Desktop, Documents…), only what they hold.",
            ("source", Text("The file or folder to move. " + PathHelp), true),
            ("destination_folder", Text("The folder to move it into (made if missing); its own folder to rename it. " + PathHelp), true),
            ("new_name", Text("Its new name (optional; to rename)."), false)),
        Function("delete_item",
            "Deletes a file or a folder (with all it holds) by putting it in the Recycle Bin, where the user can restore it. The user ALWAYS approves each deletion first. "
            + "Only in the user's own folders or another drive; never the user's folders themselves (Desktop, Documents…), only what they hold. Only delete when the user clearly asks.",
            ("path", Text("The file or folder to delete. " + PathHelp), true)),
        Function("list_windows",
            "Lists the apps' windows open on the user's screen (front first): each one's id, title, program (« explorer » for a File Explorer folder window, "
            + "« Code » for VS Code…) and app name. Use it to find which windows to close, e.g. the VS Code window of a project (its folder's name is in the title)."),
        Function("focus_window",
            "Brings a window already open to the front (restored if minimized), as a click on it would: when the user wants to see or use an app or a folder already open. "
            + "Give a window_id from list_windows, or an app and/or words of the title (the front-most match is taken).",
            ("window_id", Text("An id from list_windows."), false),
            ("app", Text("The app's program or name (« Code », « explorer », « Spotify »)."), false),
            ("title_contains", Text("Words the window's title holds."), false)),
        Function("close_windows",
            "Closes windows as their own cross would (the user approves it first, unless allowed for good): their app may ask to save, nothing is forced. "
            + "Give window_ids from list_windows, or an app (« explorer » for all File Explorer folder windows, « Code », « chrome »…), and/or words of the title.",
            ("window_ids", new JsonObject { ["type"] = "ARRAY", ["items"] = new JsonObject { ["type"] = "STRING" }, ["description"] = "Ids from list_windows." }, false),
            ("app", Text("The app's program or name: all its windows (with title_contains: only those)."), false),
            ("title_contains", Text("Words the window's title holds (« mynotch »)."), false)),
    ];

    /// <summary>How a place to create in is said (see <see cref="CreationPlaces.Resolve"/>).</summary>
    private const string PathHelp = "A full path (C:\\…, D:\\…), or one of the user's folders by name followed by the rest: Desktop, Documents, Downloads, Pictures, Music, Videos, Home "
        + "(you do not know their full paths: use these names). Given only a file's name (« plateforme streaming.txt »), find where it is with search_files first, "
        + "then use the full path it gives.";

    public ToolPlan Plan(string name, JsonObject args) => name switch
    {
        "capture_screen" => CaptureScreen(),
        "search_files" => SearchFiles(args),
        "manage_clipboard" => Clipboard(args),
        "open_file_or_app" => Open(args),
        "execute_shell_command" => Command(args),
        "create_folder" => CreateFolder(args),
        "create_text_file" => CreateTextFile(args),
        "copy_item" => Transfer(args, move: false),
        "move_item" => Transfer(args, move: true),
        "delete_item" => Delete(args),
        "list_windows" => ListWindows(),
        "focus_window" => FocusWindow(args),
        "close_windows" => CloseWindows(args),
        _ => ToolPlan.Refused(Loc.F("Outil inconnu : {0}", name), "\uE946", Loc.T("Cet outil n'existe pas.")),
    };

    // ── The screen ──────────────────────────────────────────────────────────────────────────────────────────

    private ToolPlan CaptureScreen() => new(Loc.T("Capture de l'écran"), "\uE722", async _ =>
    {
        if (await screen.CaptureAsync() is not { } image)
        {
            return new ToolOutcome(Error(Loc.T("L'écran n'a pas pu être capturé.")), Loc.T("Échec"), Failed: true);
        }

        return new ToolOutcome(new JsonObject { ["result"] = "The screenshot is attached." }, Loc.F("{0} × {1}", image.Width, image.Height), Image: image);
    });

    // ── Files ───────────────────────────────────────────────────────────────────────────────────────────────

    private ToolPlan SearchFiles(JsonObject args)
    {
        string query = String(args, "query");
        if (query.Length == 0)
        {
            return ToolPlan.Refused(Loc.T("Recherche de fichiers"), "\uE721", Loc.T("Rien à chercher."));
        }

        int count = Math.Clamp(Integer(args, "max_results") ?? 10, 1, MostFiles);
        return new ToolPlan(Loc.F("Recherche de fichiers : {0}", query), "\uE721", async _ =>
        {
            if (everything.Status != EverythingStatus.Ready || await everything.SearchAsync(EverythingEngine.WithExclusions(query, settings.SearchExclusions), count) is not { } items)
            {
                everything.Start();
                return new ToolOutcome(Error(Loc.T("La recherche de fichiers n'est pas prête : réessayez dans un moment.")), Loc.T("Pas prête"), Failed: true);
            }

            var files = new JsonArray();
            foreach (var item in items)
            {
                files.Add(new JsonObject
                {
                    ["name"] = item.Name,
                    ["path"] = item.FullPath,
                    ["is_folder"] = item.IsFolder,
                    ["modified"] = item.Modified?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                });
            }

            return new ToolOutcome(new JsonObject { ["count"] = items.Count, ["files"] = files }, Loc.Count(items.Count, "résultat", "résultats"));
        });
    }

    // ── The clipboard ───────────────────────────────────────────────────────────────────────────────────────

    private static ToolPlan Clipboard(JsonObject args)
    {
        if (String(args, "action").Equals("write", StringComparison.OrdinalIgnoreCase))
        {
            string text = String(args, "text", trim: false);
            return new ToolPlan(Loc.T("Copie dans le presse-papier"), "\uE8C8", async _ =>
                await SystemClipboard.WriteTextAsync(text)
                    ? new ToolOutcome(new JsonObject { ["result"] = "Copied." }, Loc.Count(text.Length, "caractère", "caractères"), Said: Loc.T("Copié dans le presse-papier"))
                    : new ToolOutcome(Error(Loc.T("Le presse-papier est occupé : réessayez.")), Loc.T("Échec"), Failed: true));
        }

        return new ToolPlan(Loc.T("Lecture du presse-papier"), "\uE77F", async _ =>
        {
            // Private copies (password managers) are never read: as the clipboard's history does.
            if (await SystemClipboard.ReadAsync() is not CapturedText { Text: { Length: > 0 } text })
            {
                return new ToolOutcome(Error(Loc.T("Pas de texte dans le presse-papier (vide, une image, ou privé).")), Loc.T("Pas de texte"));
            }

            bool cut = text.Length > MostClipboard;
            return new ToolOutcome(
                new JsonObject { ["text"] = cut ? text[..MostClipboard] : text, ["truncated"] = cut },
                Loc.Count(text.Length, "caractère", "caractères"));
        });
    }

    // ── Opening ─────────────────────────────────────────────────────────────────────────────────────────────

    private ToolPlan Open(JsonObject args)
    {
        string target = String(args, "target").Trim('"');
        bool code = Boolean(args, "in_vscode");
        if (target.Length == 0)
        {
            return ToolPlan.Refused(Loc.T("Ouverture"), "\uE8E5", Loc.T("Rien à ouvrir."));
        }

        // A web page: allowed first (what its address holds leaves the PC), or for good, its whole site.
        if (Uri.TryCreate(target, UriKind.Absolute, out var address) && address.Scheme is "http" or "https")
        {
            return new ToolPlan(Loc.F("Ouvrir {0}", address.Host), "\uE774", cancellation => Launch(address.AbsoluteUri, address.Host, cancellation, program: DefaultBrowser()))
            {
                Confirm = true,
                Detail = address.AbsoluteUri,
                Remember = "web:" + Site(address.Host),
            };
        }

        // A page of Windows' settings: it only shows.
        if (target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) && !target.Contains(' ', StringComparison.Ordinal))
        {
            return new ToolPlan(Loc.T("Ouvrir les paramètres Windows"), "\uE713", cancellation => Launch(target, target, cancellation, hint: Loc.T("Paramètres")));
        }

        string path = Expand(target);

        // One of the user's folders by name (« Downloads », « Téléchargements\Factures »): theirs, where Windows keeps it
        // (Gemini does not know their user name: a C:\Users\… it would guess may be another's, or Public's).
        if (!IsPath(path) && CreationPlaces.Locate(target) is { Path: { } located })
        {
            path = located;
        }
        bool folder = IsPath(path) && Directory.Exists(path);
        bool file = IsPath(path) && File.Exists(path);
        string name = System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : path;
        if (code)
        {
            if (!folder && !file)
            {
                return ToolPlan.Refused(Loc.T("Ouvrir dans VS Code"), "\uE943", Loc.F("Introuvable : {0}", target));
            }

            return CodeService.Executable is null
                ? ToolPlan.Refused(Loc.T("Ouvrir dans VS Code"), "\uE943", Loc.T("VS Code n'est pas installé."))
                : new ToolPlan(Loc.F("Ouvrir dans VS Code : {0}", name), "\uE943", async cancellation =>
                {
                    var before = OpenWindows.Snapshot();
                    Interop.NativeMethods.AllowSetForegroundWindow(Interop.NativeMethods.ASFW_ANY);
                    if (!CodeService.OpenPath(path))
                    {
                        return new ToolOutcome(Error(Loc.T("VS Code n'est pas installé.")), Loc.T("Échec"), Failed: true);
                    }

                    // Its window, new or already open on that folder (its name in the title), to the front.
                    return Opened(name, await OpenWindows.FrontAfterLaunchAsync(before, null, name, cancellation));
                });
        }

        if (folder)
        {
            return new ToolPlan(Loc.F("Ouvrir le dossier {0}", name), "\uE838", cancellation => Launch(path, name, cancellation));
        }

        if (file)
        {
            // A document opens as it would by a double click; a program, a script, anything else is allowed first.
            bool harmless = Harmless.Contains(System.IO.Path.GetExtension(path));
            return new ToolPlan(Loc.F("Ouvrir {0}", name), "\uE8A5", cancellation => Launch(path, name, cancellation))
            {
                Confirm = !harmless,
                Detail = harmless ? null : path,
                Remember = harmless ? null : "open:" + System.IO.Path.GetFullPath(path).ToLowerInvariant(),
            };
        }

        if (IsPath(path))
        {
            return ToolPlan.Refused(Loc.F("Ouvrir {0}", name), "\uE8A5", Loc.F("Introuvable : {0}", target));
        }

        // An installed app, by its name (as the Start menu lists it).
        if (FindApp(target) is { } app)
        {
            return new ToolPlan(Loc.F("Lancer {0}", app.Name), "\uE7AC", cancellation => Launch(app.ShellPath, app.Name, cancellation));
        }

        return ToolPlan.Refused(Loc.F("Ouvrir {0}", target), "\uE8E5",
            Loc.T("Rien à ce nom : donnez un chemin complet, une adresse web ou le nom d'une appli installée."));
    }

    /// <summary>The installed app <paramref name="name"/> means: the same name, else the best match (as the search ranks them).</summary>
    private InstalledApp? FindApp(string name)
    {
        if (apps.Apps.Count == 0)
        {
            apps.Refresh(); // Read for next time.
        }

        string folded = FuzzyMatch.Fold(name);
        if (apps.Apps.FirstOrDefault(app => FuzzyMatch.Fold(app.Name) == folded) is { } same)
        {
            return same;
        }

        string[] tokens = FuzzyMatch.Tokens(name);
        return apps.Apps
            .Select(app => (app, score: FuzzyMatch.Score(app.Name, tokens, out _)))
            .Where(each => each.score is >= 0.5)
            .OrderByDescending(each => each.score)
            .Select(each => each.app)
            .FirstOrDefault();
    }

    /// <summary>
    /// <paramref name="target"/> opened as a double click would, then its window brought to the front (see
    /// <see cref="OpenWindows.FrontAfterLaunchAsync"/>): what it opens never stays behind, even with the notch closed
    /// (Windows would leave it there: Ilot is not the app in use). An app already open, woken, comes to the front too
    /// (found by <paramref name="hint"/>, or its <paramref name="program"/>: the browser, for a page).
    /// </summary>
    private static async Task<ToolOutcome> Launch(string target, string name, CancellationToken cancellation, string? hint = null, string? program = null)
    {
        var before = OpenWindows.Snapshot();
        int? id = null;
        try
        {
            Interop.NativeMethods.AllowSetForegroundWindow(Interop.NativeMethods.ASFW_ANY); // Enough when Ilot is the app in use.
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            id = process is { HasExited: false } ? process.Id : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new ToolOutcome(Error(ex.Message), Loc.T("Échec"), Failed: true);
        }

        var front = await OpenWindows.FrontAfterLaunchAsync(before, id, hint ?? name, cancellation, program);
        return Opened(name, front);
    }

    /// <summary>Opened (and its window in front, if it showed in time).</summary>
    private static ToolOutcome Opened(string name, OpenWindow? front = null) => new(
        new JsonObject { ["result"] = $"Opened {name}.", ["in_front"] = front is not null },
        Loc.T("Ouvert"),
        Said: Loc.F("Ouvert : {0}", name));

    /// <summary>The browser that opens web pages (its program: « chrome », « msedge »), from the user's choice; <c>null</c> if unknown.</summary>
    private static string? DefaultBrowser()
    {
        try
        {
            using var choice = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (choice?.GetValue("ProgId") is not string id)
            {
                return null;
            }

            using var command = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{id}\shell\open\command");
            if (command?.GetValue(null) is not string line)
            {
                return null;
            }

            // « "C:\…\chrome.exe" --single-argument %1 »: the program's name.
            string path = line.StartsWith('"') ? line[1..line.IndexOf('"', 1)] : line.Split(' ')[0];
            return System.IO.Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>« ~\Documents », « %USERPROFILE%\Documents »: the full path.</summary>
    private static string Expand(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        if (path == "~" || path.StartsWith("~\\", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal))
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        }

        return path.Replace('/', '\\');
    }

    /// <summary>A full path (« C:\… », « \\server\… »), not a name.</summary>
    private static bool IsPath(string path) => System.IO.Path.IsPathFullyQualified(path);

    // ── Commands ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A command of the list: allowed by the user first, unless the settings say to run them directly.</summary>
    private ToolPlan Command(JsonObject args)
    {
        string line = String(args, "command");
        string folder = Expand(String(args, "working_directory"));
        var (command, refusal) = ShellCommands.Parse(line);
        if (command is null)
        {
            return ToolPlan.Refused(Loc.F("Commande : {0}", line), "\uE756", refusal ?? Loc.T("Commande refusée."));
        }

        if (folder.Length > 0 && !(IsPath(folder) && Directory.Exists(folder)))
        {
            return ToolPlan.Refused(Loc.F("Commande : {0}", command.Line), "\uE756", Loc.F("Dossier introuvable : {0}", folder));
        }

        string detail = folder.Length > 0 ? $"{command.Line}\n{folder}" : command.Line;
        return new ToolPlan(Loc.F("Commande : {0}", command.Line), "\uE756", async cancellation =>
        {
            var result = await ShellCommands.RunAsync(command, folder, cancellation);
            var response = new JsonObject { ["exit_code"] = result.ExitCode, ["output"] = result.Output };
            if (result.Whole.Length > 0)
            {
                // Shown whole under the command: Gemini says what matters in it, rather than reading it all out (aloud, a list of ports…).
                response["shown_to_user"] = "The user sees this whole output under the command, in the conversation: do not repeat it, tell only what matters in it.";
            }
            if (result.Truncated)
            {
                response["truncated"] = true;
            }

            if (result.TimedOut)
            {
                response["timed_out"] = true;
            }

            string note = result.TimedOut ? Loc.T("Arrêtée : trop longue") : Loc.F("Code de sortie {0}", result.ExitCode);
            return new ToolOutcome(response, note, Failed: result.TimedOut, Shown: result.Whole.Length > 0 ? result.Whole : null, Said: result.TimedOut ? null : Loc.T("Commande terminée"));
        })
        { Confirm = !settings.AssistantRunCommandsDirectly, Detail = detail, Remember = "command:" + command.Program };
    }

    // ── Creating (a folder, a text file) ────────────────────────────────────────────────────────────────────

    /// <summary>The text written in a file at most (a note, a list: not a book).</summary>
    private const int MostFileText = 100_000;

    /// <summary>The files it may create: plain text only.</summary>
    private static readonly string[] TextFiles = [".txt", ".md", ".csv"];

    /// <summary>
    /// A new folder (and those above it, if missing), where it may be (see <see cref="CreationPlaces"/>): always allowed
    /// by the user first, its full path shown; never « Toujours autoriser ». One already there is left as it is.
    /// </summary>
    private ToolPlan CreateFolder(JsonObject args)
    {
        string asked = String(args, "path");
        var (path, refusal) = CreationPlaces.Resolve(asked);
        if (path is null)
        {
            return ToolPlan.Refused(Loc.F("Créer le dossier {0}", asked), "", refusal!);
        }

        string name = System.IO.Path.GetFileName(path);
        return new ToolPlan(Loc.F("Créer le dossier {0}", name), "", _ =>
        {
            try
            {
                if (CreationPlaces.Check(path) is { } why)
                {
                    return Task.FromResult(new ToolOutcome(Error(why), Loc.T("Refusé"), Failed: true));
                }

                if (Directory.Exists(path))
                {
                    return Task.FromResult(new ToolOutcome(new JsonObject { ["result"] = "The folder already exists: left as it is.", ["path"] = path },
                        Loc.T("Existe déjà"), Opens: path));
                }

                if (File.Exists(path))
                {
                    return Task.FromResult(new ToolOutcome(Error(Loc.T("Un fichier porte déjà ce nom.")), Loc.T("Échec"), Failed: true));
                }

                Directory.CreateDirectory(path);
                return Task.FromResult(new ToolOutcome(new JsonObject { ["result"] = "Created.", ["path"] = path }, Loc.T("Créé"), Opens: path, Entry: journal?.Add(FileChange.Folder, null, path), Said: Loc.T("Dossier créé")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Task.FromResult(new ToolOutcome(Error(ex.Message), Loc.T("Échec"), Failed: true));
            }
        })
        { Confirm = true, Detail = path, Remember = CreateKey };
    }

    /// <summary>
    /// A new text file (.txt, .md, .csv; .txt if none), its folders made if missing: allowed by the user first (its path
    /// and the beginning of what it will hold shown); never one already there replaced. UTF-8 (with its mark for .csv,
    /// for Excel to read the accents).
    /// </summary>
    private ToolPlan CreateTextFile(JsonObject args)
    {
        string asked = String(args, "path");
        string content = String(args, "content", trim: false);
        if (System.IO.Path.GetExtension(asked.Trim().Trim('"')).Length == 0)
        {
            asked = asked.Trim().Trim('"') + ".txt";
        }

        var (path, refusal) = CreationPlaces.Resolve(asked);
        string extension = path is null ? "" : System.IO.Path.GetExtension(path).ToLowerInvariant();
        refusal ??= !TextFiles.Contains(extension) ? Loc.T("Seulement un fichier texte : .txt, .md ou .csv.")
            : content.Length > MostFileText ? Loc.T("Texte trop long pour un fichier créé ainsi.")
            : null;
        if (path is null || refusal is not null)
        {
            return ToolPlan.Refused(Loc.F("Créer le fichier {0}", System.IO.Path.GetFileName(asked)), "", refusal!);
        }

        string name = System.IO.Path.GetFileName(path);
        string detail = content.Length > 0 ? $"{path}\n\n{CreationPlaces.Preview(content)}" : path;
        return new ToolPlan(Loc.F("Créer le fichier {0}", name), "", async cancellation =>
        {
            try
            {
                if (CreationPlaces.Check(path) is { } why)
                {
                    return new ToolOutcome(Error(why), Loc.T("Refusé"), Failed: true);
                }

                if (File.Exists(path) || Directory.Exists(path))
                {
                    return new ToolOutcome(Error(Loc.T("Ce fichier existe déjà : il n'est jamais remplacé. Choisissez un autre nom.")), Loc.T("Existe déjà"), Failed: true);
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: extension == ".csv");
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) // Never over another.
                await using (var writer = new StreamWriter(file, encoding))
                {
                    await writer.WriteAsync(content.AsMemory(), cancellation);
                }

                return new ToolOutcome(new JsonObject { ["result"] = "Created.", ["path"] = path }, Loc.Count(content.Length, "caractère", "caractères"), Opens: path, Entry: journal?.Add(FileChange.File, null, path), Said: Loc.T("Fichier créé"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new ToolOutcome(Error(ex.Message), Loc.T("Échec"), Failed: true);
            }
        })
        { Confirm = true, Detail = detail, Remember = CreateKey };
    }

    // ── Copying, moving ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A file or a folder copied, or moved (renamed, in its own folder), see <see cref="FileTransfer"/>: always allowed by
    /// the user first (where from, where to), never « Toujours autoriser ». Copied from wherever it is on this PC, to
    /// where the assistant may create (see <see cref="CreationPlaces"/>); moved only from there too, never the user's
    /// folders themselves. Nothing is ever replaced.
    /// </summary>
    private ToolPlan Transfer(JsonObject args, bool move)
    {
        string asked = String(args, "source");
        string glyph = move ? "" : "";
        string Label(string name) => move ? Loc.F("Déplacer {0}", name) : Loc.F("Copier {0}", name);

        var (source, refusal) = CreationPlaces.Locate(asked);
        if (source is null)
        {
            return ToolPlan.Refused(Label(asked), glyph, refusal!);
        }

        string name = System.IO.Path.GetFileName(source);
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            return ToolPlan.Refused(Label(name), glyph, Loc.F("Introuvable : {0}", source));
        }

        if (System.IO.Path.GetPathRoot(source) is { } root && source.Length <= root.Length)
        {
            return ToolPlan.Refused(Label(source), glyph, Loc.T("Pas un disque entier."));
        }

        if (move && CreationPlaces.Movable(source) is { } kept)
        {
            return ToolPlan.Refused(Label(name), glyph, kept);
        }

        // Where to: the folder (where the assistant may create), and the name there (its own, or the one given).
        string renamed = String(args, "new_name");
        if (renamed.Length > 0 && (renamed.IndexOfAny(['\\', '/']) >= 0 || renamed.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0))
        {
            return ToolPlan.Refused(Label(name), glyph, Loc.F("Nom impossible sous Windows : « {0} ».", renamed));
        }

        var (folder, where) = CreationPlaces.Locate(String(args, "destination_folder"));
        string? destination = folder is null ? null : System.IO.Path.Combine(folder, renamed.Length > 0 ? renamed : name);
        where ??= destination is null ? null : CreationPlaces.Check(destination);
        if (destination is null || where is not null)
        {
            return ToolPlan.Refused(Label(name), glyph, where ?? Loc.T("Ce chemin n'est pas valable."));
        }

        if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) || CreationPlaces.Inside(destination, source))
        {
            return ToolPlan.Refused(Label(name), glyph, Loc.T("Pas dans lui-même."));
        }

        string into = System.IO.Path.GetFileName(folder) is { Length: > 0 } leaf ? leaf : folder!;
        bool renaming = move && string.Equals(System.IO.Path.GetDirectoryName(source), folder, StringComparison.OrdinalIgnoreCase);
        string label = renaming
            ? Loc.F("Renommer {0} en {1}", name, System.IO.Path.GetFileName(destination))
            : move ? Loc.F("Déplacer {0} vers {1}", name, into) : Loc.F("Copier {0} vers {1}", name, into);
        bool isFolder = Directory.Exists(source);
        string said = renaming ? Loc.T("Renommé")
            : move ? (isFolder ? Loc.T("Dossier déplacé") : Loc.T("Fichier déplacé"))
            : isFolder ? Loc.T("Dossier copié") : Loc.T("Fichier copié");
        return new ToolPlan(label, glyph, async cancellation =>
        {
            bool made = false, done = false;
            try
            {
                // Judged again as it is done (the user may have taken a while to allow it).
                if ((CreationPlaces.Check(destination) ?? (move ? CreationPlaces.Movable(source) : null)) is { } why)
                {
                    return new ToolOutcome(Error(why), Loc.T("Refusé"), Failed: true);
                }

                made = !Directory.Exists(folder);
                Directory.CreateDirectory(folder!);
                var result = move
                    ? await FileTransfer.MoveAsync(source, destination, cancellation)
                    : await FileTransfer.CopyAsync(source, destination, cancellation);
                done = true;
                string note = Loc.Count(result.Files, "fichier", "fichiers") + " · " + FileTransfer.Size(result.Bytes);
                return new ToolOutcome(new JsonObject
                {
                    ["result"] = move ? "Moved." : "Copied.",
                    ["path"] = destination,
                    ["files"] = result.Files,
                    ["bytes"] = result.Bytes,
                }, note, Opens: destination, Entry: journal?.Add(move ? FileChange.Move : FileChange.Copy, source, destination), Said: said);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new ToolOutcome(Error(ex.Message), Loc.T("Échec"), Failed: true);
            }
            finally
            {
                // Failed or stopped: the folder made for it goes too, if it is still empty (nothing else is touched).
                if (made && !done && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder!).Any())
                {
                    try { Directory.Delete(folder!); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        })
        { Confirm = true, Detail = $"{source}\n→ {destination}", Remember = move ? MoveKey : CopyKey };
    }

    // ── Deleting ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A file or a folder deleted: into the Recycle Bin (recoverable from there), from where it could be moved (see
    /// <see cref="CreationPlaces.Movable"/>), on a drive that has a Recycle Bin. ALWAYS allowed by the user first, each
    /// time: never « Toujours autoriser » (no <see cref="ToolPlan.Remember"/>), whatever the settings. Noted in the journal.
    /// </summary>
    private ToolPlan Delete(JsonObject args)
    {
        string asked = String(args, "path");
        var (path, refusal) = CreationPlaces.Locate(asked);
        string name = path is null ? asked : System.IO.Path.GetFileName(path);
        bool isFolder = path is not null && Directory.Exists(path);
        refusal ??= !isFolder && !File.Exists(path) ? Loc.F("Introuvable : {0}", path)
            : CreationPlaces.Movable(path!) is { } kept ? kept
            : !HasRecycleBin(path!) ? Loc.T("Pas de Corbeille sur ce disque : Notch ne supprime que ce qui peut aller à la Corbeille.")
            : null;
        if (refusal is not null)
        {
            return ToolPlan.Refused(Loc.F("Supprimer {0}", name), "", refusal);
        }

        string detail = $"{path}\n\n" + (isFolder
            ? Loc.T("Le dossier et tout ce qu'il contient iront à la Corbeille (récupérables de là).")
            : Loc.T("Le fichier ira à la Corbeille (récupérable de là)."));
        return new ToolPlan(Loc.F("Supprimer {0}", name), "", async _ =>
        {
            // Judged again as it is done (the user may have taken a while to allow it).
            if (CreationPlaces.Movable(path!) is { } why)
            {
                return new ToolOutcome(Error(why), Loc.T("Refusé"), Failed: true);
            }

            var (failed, entry) = journal is not null
                ? await journal.DeleteAsync(path!)
                : (await Task.Run(() => Interop.NativeMethods.Recycle(path!)), null);
            if (failed is not null)
            {
                return new ToolOutcome(Error(failed), Loc.T("Pas supprimé"), Failed: true);
            }

            return new ToolOutcome(new JsonObject { ["result"] = "Moved to the Recycle Bin (the user can restore it from there).", ["path"] = path },
                Loc.T("À la Corbeille"), Entry: entry, Said: isFolder ? Loc.T("Dossier supprimé") : Loc.T("Fichier supprimé"));
        })
        { Confirm = true, Detail = detail }; // Never Remember: a deletion is asked for each time.
    }

    /// <summary>The drive of <paramref name="path"/> has a Recycle Bin (a fixed one: a USB key or a network drive has none).</summary>
    private static bool HasRecycleBin(string path)
    {
        try
        {
            return System.IO.Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ── Windows ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The windows told to Gemini at most (the front ones).</summary>
    private const int MostWindows = 60;

    /// <summary>The windows to close shown one by one while asking, at most (then how many more).</summary>
    private const int ShownWindows = 12;

    /// <summary>The apps' windows open (see <see cref="OpenWindows"/>): only read, so not asked.</summary>
    private static ToolPlan ListWindows() => new(Loc.T("Fenêtres ouvertes"), "", _ =>
    {
        var windows = OpenWindows.List();
        var list = new JsonArray();
        foreach (var window in windows.Take(MostWindows))
        {
            list.Add(new JsonObject { ["id"] = window.Id, ["title"] = window.Title, ["program"] = window.Program, ["app"] = window.App });
        }

        return Task.FromResult(new ToolOutcome(new JsonObject { ["count"] = windows.Count, ["windows"] = list }, Loc.Count(windows.Count, "fenêtre", "fenêtres")));
    });

    /// <summary>
    /// A window already open brought to the front (see <see cref="OpenWindows.BringToFront"/>): the front-most of those
    /// it names. Nothing is changed but which window is in front: not asked.
    /// </summary>
    private static ToolPlan FocusWindow(JsonObject args)
    {
        string id = String(args, "window_id"), app = String(args, "app"), title = String(args, "title_contains");
        if (id.Length == 0 && app.Length == 0 && title.Length == 0)
        {
            return ToolPlan.Refused(Loc.T("Mettre au premier plan"), "", Loc.T("Dites laquelle : une appli, des mots du titre, ou son id (list_windows)."));
        }

        var found = OpenWindows.Match(OpenWindows.List(), id.Length > 0 ? [id] : [], app, title).FirstOrDefault();
        if (found is null)
        {
            return ToolPlan.Refused(Loc.T("Mettre au premier plan"), "", Loc.T("Aucune fenêtre ouverte ne correspond."));
        }

        string shown = found.Title.Length > 70 ? found.Title[..69] + "…" : found.Title;
        return new ToolPlan(Loc.F("Au premier plan : {0}", shown), "", _ =>
        {
            bool front = OpenWindows.BringToFront(found.Handle);
            return Task.FromResult(front
                ? new ToolOutcome(new JsonObject { ["result"] = "In front.", ["title"] = found.Title }, Loc.T("Devant"), Said: Loc.F("Au premier plan : {0}", found.App))
                : new ToolOutcome(Error(Loc.T("Windows ne l'a pas laissée passer devant (une appli lancée en administrateur ?).")), Loc.T("Échec"), Failed: true));
        });
    }

    /// <summary>
    /// Windows closed as their cross would (see <see cref="OpenWindows.CloseAsync"/>): those it names (ids, an app,
    /// words of the title), shown before (allowed for good, it is not asked again); those still open after say so.
    /// </summary>
    private static ToolPlan CloseWindows(JsonObject args)
    {
        var ids = args["window_ids"] is JsonArray given
            ? given.OfType<JsonValue>().Select(id => id.ToString().Trim('"')).Where(id => id.Length > 0).ToHashSet()
            : [];
        string app = String(args, "app"), title = String(args, "title_contains");
        if (ids.Count == 0 && app.Length == 0 && title.Length == 0)
        {
            return ToolPlan.Refused(Loc.T("Fermer des fenêtres"), "", Loc.T("Dites lesquelles : une appli, des mots du titre, ou leurs ids (list_windows)."));
        }

        var chosen = OpenWindows.Match(OpenWindows.List(), ids, app, title);
        if (chosen.Count == 0)
        {
            return ToolPlan.Refused(Loc.T("Fermer des fenêtres"), "", Loc.T("Aucune fenêtre ouverte ne correspond."));
        }

        string label = chosen.Count == 1 ? Loc.F("Fermer {0}", Shorten(chosen[0].Title)) : Loc.F("Fermer {0} fenêtres", chosen.Count);
        var lines = chosen.Take(ShownWindows).Select(window => $"• {Shorten(window.Title)} — {window.App}").ToList();
        if (chosen.Count > ShownWindows)
        {
            lines.Add(Loc.F("… et {0} autres", chosen.Count - ShownWindows));
        }

        return new ToolPlan(label, "", async cancellation =>
        {
            var left = await OpenWindows.CloseAsync(chosen, cancellation);
            int closed = chosen.Count - left.Count;
            var response = new JsonObject { ["closed"] = closed };
            if (left.Count > 0)
            {
                response["still_open"] = new JsonArray([.. left.Select(window => (JsonNode)window.Title)]);
                response["note"] = "Those still open are asking the user something (save?), or refused: tell the user, do not force them.";
            }

            // Still open: shown by name under the action (their app may be asking to save).
            string note = left.Count == 0 ? Loc.Count(closed, "fermée", "fermées")
                : Loc.Count(closed, "fermée", "fermées") + " · " + Loc.Count(left.Count, "encore ouverte", "encore ouvertes");
            string? shown = left.Count == 0 ? null
                : Loc.T("Encore ouvertes (l'appli demande peut-être d'enregistrer) :") + "\n" + string.Join('\n', left.Select(window => $"• {Shorten(window.Title)} — {window.App}"));
            return new ToolOutcome(response, note, Shown: shown, Said: closed == 0 ? null : closed == 1 ? Loc.T("Fenêtre fermée") : Loc.F("{0} fenêtres fermées", closed));
        })
        { Confirm = true, Detail = string.Join('\n', lines), Remember = WindowsKey };

        static string Shorten(string text) => text.Length > 70 ? text[..69] + "…" : text;
    }

    // ── What is allowed for good ────────────────────────────────────────────────────────────────────────────

    // What the user may allow for good about their files, each kind as a whole (« Toujours autoriser »): every one done
    // so is in the journal (see FileJournal), to take back. Nothing is ever deleted by the assistant.
    private const string CreateKey = "files:create";
    private const string CopyKey = "files:copy";
    private const string MoveKey = "files:move";

    /// <summary>Closing windows as their cross would (their apps still ask to save): allowed for good as a whole.</summary>
    private const string WindowsKey = "windows:close";

    /// <summary>A site as it is allowed: its host, without « www. » (www.youtube.com and youtube.com alike).</summary>
    private static string Site(string host) =>
        (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host).ToLowerInvariant();

    /// <summary>What a key of <see cref="AppSettings.AssistantAllowed"/> allows, in words: « youtube.com », a file's path, « ipconfig ».</summary>
    public static string Scope(string key) => key switch
    {
        CreateKey => Loc.T("les créations de dossiers et de fichiers texte"),
        CopyKey => Loc.T("les copies de fichiers et de dossiers"),
        MoveKey => Loc.T("les déplacements et renommages de fichiers et de dossiers"),
        WindowsKey => Loc.T("la fermeture de fenêtres"),
        _ => key.IndexOf(':') is int colon and > 0 ? key[(colon + 1)..] : key,
    };

    /// <summary>A key of <see cref="AppSettings.AssistantAllowed"/> as the settings list it: what kind, and what.</summary>
    public static string Describe(string key) => key.Split(':', 2) switch
    {
        ["web", var site] => Loc.F("Les pages de {0}", site),
        ["open", var path] => Loc.F("Ouvrir {0}", path),
        ["command", var program] => Loc.F("La commande {0}", program),
        ["files", "create"] => Loc.T("Créer des dossiers et des fichiers texte"),
        ["files", "copy"] => Loc.T("Copier des fichiers et des dossiers"),
        ["files", "move"] => Loc.T("Déplacer et renommer des fichiers et des dossiers"),
        ["windows", "close"] => Loc.T("Fermer des fenêtres"),
        _ => key,
    };

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    private static JsonObject Error(string message) => new() { ["error"] = message };

    private static string String(JsonObject args, string name, bool trim = true) =>
        args[name] is JsonValue value && value.TryGetValue(out string? text) ? (trim ? text.Trim() : text) : "";

    private static int? Integer(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number) ? (int)Math.Clamp(Math.Round(number), int.MinValue, int.MaxValue) : null;

    private static bool Boolean(JsonObject args, string name) =>
        args[name] is JsonValue value && value.TryGetValue(out bool flag) && flag;

    private static JsonObject Text(string description) => new() { ["type"] = "STRING", ["description"] = description };

    /// <summary>A function declaration; no parameters at all when it takes none (Google refuses an empty object).</summary>
    private static JsonObject Function(string name, string description, params (string Name, JsonObject Schema, bool Required)[] parameters)
    {
        var function = new JsonObject { ["name"] = name, ["description"] = description };
        if (parameters.Length > 0)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var (parameter, schema, needed) in parameters)
            {
                properties[parameter] = schema;
                if (needed)
                {
                    required.Add(parameter);
                }
            }

            function["parameters"] = new JsonObject { ["type"] = "OBJECT", ["properties"] = properties, ["required"] = required };
        }

        return function;
    }
}
