using System.ComponentModel;
using System.Diagnostics;
using MyNotch.Interop;
using MyNotch.Modules.Clipboard;
using MyNotch.Modules.Code;
using MyNotch.Modules.Search.Everything;
using MyNotch.Settings;
using MyNotch.Localization;

namespace MyNotch.Modules.Search;

/// <summary>How a result is opened.</summary>
public enum SearchOpening
{
    /// <summary>Enter, or a click.</summary>
    Open,

    /// <summary>Ctrl + Enter: shown in its folder.</summary>
    Reveal,

    /// <summary>Shift + Enter: run as administrator.</summary>
    Elevated,

    /// <summary>Ctrl + C: its path (or a calculation's result) on the clipboard.</summary>
    CopyPath,

    // From a result's menu (a right click), through the Explorer's own commands (see ShellMenu).

    /// <summary>The Windows « Open with » dialog (a file).</summary>
    OpenWith,

    /// <summary>The file or folder itself on the clipboard, to paste in the Explorer.</summary>
    CopyItem,

    /// <summary>To the Recycle Bin.</summary>
    Delete,

    /// <summary>Its Windows properties.</summary>
    Properties,

    /// <summary>The Explorer's whole menu (Send to, Share, the apps' own entries…).</summary>
    MoreOptions,
}

/// <summary>What opening a result did.</summary>
public enum SearchOutcome
{
    /// <summary>Something opened (an app, a file, a page…): it takes the screen and the keyboard.</summary>
    Launched,

    /// <summary>Something went to the clipboard.</summary>
    Copied,

    /// <summary>It asks first: the same Enter again confirms.</summary>
    Confirm,

    /// <summary>Nothing happened (the user said no to the administrator prompt…).</summary>
    Nothing,
}

/// <summary>
/// The search: what is typed, looked up everywhere at once (apps, VS Code projects, Windows' settings, commands,
/// calculations, and files with Everything), then ranked in one list: how well each answers, what kind it is, how often
/// and lately it was opened, and for files, where they are and how fresh. The local sources answer at once; Everything
/// within a few milliseconds, waited for a moment so the list does not jump.
/// </summary>
public sealed class SearchViewModel
{
    /// <summary>Files asked of Everything (most recent first), ranked here.</summary>
    private const int FilesAsked = 150;
    private static readonly TimeSpan WaitForFiles = TimeSpan.FromMilliseconds(90);

    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string[] PlacesOfNote = ["Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music", "OneDrive", "dev", "source"];

    private readonly AppSettings _settings;
    private readonly AppCatalog _apps;
    private readonly CodeService _code;
    private readonly SearchHistory _history;
    private IReadOnlyList<RecentProject> _projects = [];
    private int _version;
    private string? _confirming;

    public SearchViewModel(AppSettings settings, EverythingEngine everything, AppCatalog apps, CodeService code, SearchHistory history, ShellIcons icons, CodeViewModel projects)
    {
        Projects = projects;
        _settings = settings;
        Everything = everything;
        _apps = apps;
        _code = code;
        _history = history;
        Icons = icons;
        apps.Changed += (_, _) => Search(Query);
        settings.PropertyChanged += OnSettingsChanged;

        // Ready for the first Ctrl + Q (read in the background).
        if (settings.SearchApps)
        {
            apps.Refresh();
        }
    }

    public EverythingEngine Everything { get; }

    public ShellIcons Icons { get; }

    /// <summary>VS Code's recent projects, as tiles (« code » typed: see SearchView, CodeShelf).</summary>
    public CodeViewModel Projects { get; }

    public string Query { get; private set; } = "";

    /// <summary>The results for <see cref="Query"/>, best first; none while it is empty.</summary>
    public IReadOnlyList<SearchResult> Results { get; private set; } = [];

    /// <summary>The result waiting for a second Enter (restart, shut down…); <c>null</c> if none.</summary>
    public string? Confirming => _confirming;

    public int MaxResults => (int)_settings.SearchMaxResults;

    /// <summary>The search opens with the last one typed (see the settings).</summary>
    public bool KeepsQuery => _settings.SearchKeepQuery;

    /// <summary>Raised on the UI thread when <see cref="Results"/> change.</summary>
    public event EventHandler? ResultsChanged;

    /// <summary>The search shows: the lists it keeps are read again if they are old, and Everything made sure of.</summary>
    public async void Opened()
    {
        if (_settings.SearchFiles)
        {
            Everything.Start();
        }

        if (_settings.SearchApps)
        {
            _apps.Refresh();
        }

        if (_settings.SearchProjects && CodeService.IsInstalled)
        {
            try
            {
                _projects = await _code.GetRecentAsync();
                if (Query.Length > 0)
                {
                    Search(Query);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Debug.WriteLine($"[Search] VS Code projects not read: {ex.Message}");
            }
        }
    }

    /// <summary>Looks <paramref name="text"/> up everywhere; the results come with <see cref="ResultsChanged"/>.</summary>
    public async void Search(string text)
    {
        int version = ++_version;
        Query = text;
        _confirming = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            Publish([]);
            return;
        }

        string[] tokens = FuzzyMatch.Tokens(text);
        var local = Local(text, tokens);
        var calculation = local.Any(result => result.Kind == SearchKind.Calculation);
        Task<IReadOnlyList<EverythingItem>?>? files = _settings.SearchFiles && !calculation && text.Trim().Length >= 2
            ? Everything.SearchAsync(EverythingQuery(text), FilesAsked)
            : null;

        if (files is null)
        {
            Publish(Merge(local, []));
            return;
        }

        // A moment for Everything (usually enough): one list, rather than one that jumps when the files come.
        if (await Task.WhenAny(files, Task.Delay(WaitForFiles)) != files)
        {
            if (version != _version)
            {
                return;
            }

            Publish(Merge(local, []));
        }

        var found = await files;
        if (version != _version)
        {
            return;
        }

        Publish(Merge(local, found is null ? [] : Files(found, tokens, local)));
    }

    /// <summary>Opens <paramref name="result"/> as asked; it is counted for next time when it opens.</summary>
    public async Task<SearchOutcome> OpenAsync(SearchResult result, SearchOpening opening)
    {
        if (opening == SearchOpening.CopyPath || result.Kind == SearchKind.Calculation)
        {
            string text = result.Kind == SearchKind.Calculation ? result.Target : result.Path ?? result.Target;
            return await SystemClipboard.WriteTextAsync(text) ? SearchOutcome.Copied : SearchOutcome.Nothing;
        }

        if (result.NeedsConfirmation && _confirming != result.Key)
        {
            _confirming = result.Key;
            return SearchOutcome.Confirm;
        }

        _confirming = null;
        bool done = opening == SearchOpening.Reveal && result.Path is { } path ? Reveal(path) : Launch(result, opening == SearchOpening.Elevated);
        if (!done)
        {
            return SearchOutcome.Nothing;
        }

        _history.Record(result.Key);
        if (result.Kind is SearchKind.File or SearchKind.Folder && result.Path is { } file)
        {
            Everything.CountRun(file);
        }

        return SearchOutcome.Launched;
    }

    private void Publish(IReadOnlyList<SearchResult> results)
    {
        Results = results;
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName?.StartsWith("Search", StringComparison.Ordinal) == true && Query.Length > 0)
        {
            Search(Query);
        }
    }

    // ── Sources ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the notch knows itself: a calculation, apps, projects, settings, commands.</summary>
    private List<SearchResult> Local(string text, string[] tokens)
    {
        var results = new List<SearchResult>();
        if (_settings.SearchCalculator && Calculator.Evaluate(text) is { } value)
        {
            string shown = Calculator.Format(value);
            results.Add(new SearchResult
            {
                Key = "calc",
                Title = $"= {shown}",
                Subtitle = Loc.F("{0}  ·  Entrée pour copier", text.Trim().TrimStart('=').Trim()),
                Kind = SearchKind.Calculation,
                Target = shown,
                Glyph = "\uE8EF",
                Score = 10,
            });
        }

        if (_settings.SearchApps)
        {
            foreach (var app in _apps.Apps)
            {
                Add(results, tokens, new SearchResult
                {
                    Key = $"app:{app.Id}",
                    Title = app.Name,
                    Subtitle = Loc.T("Application"),
                    Kind = SearchKind.Application,
                    Target = app.ShellPath,
                    Glyph = "\uE71D",
                    IconSource = app.ShellPath,
                    Path = System.IO.Path.IsPathFullyQualified(app.Id) ? app.Id : null,
                }, bonus: 0.22);
            }
        }

        if (_settings.SearchProjects)
        {
            foreach (var project in _projects)
            {
                Add(results, tokens, new SearchResult
                {
                    Key = $"project:{project.Location}",
                    Title = project.Name,
                    Subtitle = $"{project.Label}  ·  {project.Place}",
                    Kind = SearchKind.Project,
                    Target = project.Location,
                    Glyph = project.Glyph,
                    Tint = project.Color,
                    Path = project.IsLocal ? project.Location : null,
                    Item = project,
                }, bonus: 0.12);
            }
        }

        if (_settings.SearchSettings)
        {
            foreach (var page in SearchCatalog.Settings)
            {
                Add(results, tokens, new SearchResult
                {
                    Key = $"setting:{page.Uri}",
                    Title = Loc.T(page.Title),
                    Subtitle = Loc.T("Paramètres Windows"),
                    Kind = SearchKind.Setting,
                    Target = page.Uri,
                    Glyph = page.Glyph,
                }, bonus: 0.06, keywords: Loc.IsEnglish ? $"{page.Keywords} {page.Title}" : page.Keywords);
            }
        }

        if (_settings.SearchCommands)
        {
            foreach (var command in SearchCatalog.Commands)
            {
                Add(results, tokens, new SearchResult
                {
                    Key = $"command:{command.Id}",
                    Title = Loc.T(command.Title),
                    Subtitle = Loc.T(command.NeedsConfirmation ? "Commande  ·  Entrée deux fois pour confirmer" : "Commande"),
                    Kind = SearchKind.Command,
                    Target = command.Id,
                    Glyph = command.Glyph,
                    NeedsConfirmation = command.NeedsConfirmation,
                }, bonus: 0.04, keywords: Loc.IsEnglish ? $"{command.Keywords} {command.Title}" : command.Keywords);
            }
        }

        if (_settings.SearchWeb && text.Trim().Length >= 2)
        {
            string query = text.Trim();
            results.Add(new SearchResult
            {
                Key = "web",
                Title = Loc.F("Rechercher « {0} » sur le web", query),
                Subtitle = "Google",
                Kind = SearchKind.Web,
                Target = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}",
                Glyph = "\uE774",
                Score = -10,
            });
        }

        return results;
    }

    /// <summary>Adds <paramref name="result"/> if its title (or else its keywords) answers the search, scored.</summary>
    private void Add(List<SearchResult> results, string[] tokens, SearchResult result, double bonus, string? keywords = null)
    {
        double? score = FuzzyMatch.Score(result.Title, tokens, out var matches);
        if (score is null && keywords is not null && FuzzyMatch.Score(keywords, tokens, out _) is { } byKeyword)
        {
            score = byKeyword * 0.8;
            matches = [];
        }

        if (score is not { } found)
        {
            return;
        }

        result.Matches = matches;
        result.Score = found + bonus + _history.Boost(result.Key);
        results.Add(result);
    }

    /// <summary>Everything's answer, ranked: its name against the search, then where it is, how fresh, how used.</summary>
    private List<SearchResult> Files(IReadOnlyList<EverythingItem> items, string[] tokens, List<SearchResult> local)
    {
        var apps = local.Where(result => result.Kind == SearchKind.Application).Select(result => result.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<SearchResult>(items.Count);
        foreach (var item in items)
        {
            // A Start menu shortcut is already there, as its app.
            if (item.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && apps.Contains(System.IO.Path.GetFileNameWithoutExtension(item.Name)))
            {
                continue;
            }

            double score = FuzzyMatch.Score(item.Name, tokens, out var matches) ?? 0.3; // Matched on its path.
            string path = item.FullPath;
            score += Placement(item.Folder);
            score += item.IsFolder ? 0.05 : 0;
            score += Math.Min(0.24, Math.Log2(1 + item.RunCount) * 0.08);
            if (item.Modified is { } modified)
            {
                double days = (DateTimeOffset.Now - modified).TotalDays;
                score += days < 1 ? 0.07 : days < 7 ? 0.04 : days < 30 ? 0.015 : 0;
            }

            string key = $"file:{path}";
            score += _history.Boost(key);
            string extension = System.IO.Path.GetExtension(item.Name).ToLowerInvariant();
            results.Add(new SearchResult
            {
                Key = key,
                Title = item.Name,
                Subtitle = ShortFolder(item.Folder),
                Kind = item.IsFolder ? SearchKind.Folder : SearchKind.File,
                Target = path,
                Path = path,
                Glyph = item.IsFolder ? "\uE8B7" : "\uE8A5",
                IconSource = path,
                // Every PDF looks alike; programs, shortcuts and icons have their own picture.
                IconKey = item.IsFolder ? "folder" : extension is ".exe" or ".lnk" or ".ico" or ".url" or ".appref-ms" or "" ? path : extension,
                Matches = matches.Count > 0 ? matches : item.Matches,
                Score = score,
            });
        }

        return results;
    }

    /// <summary>Where a file is says much: the user's places first, deep or hidden folders last.</summary>
    private static double Placement(string folder)
    {
        double score = 0;
        int depth = folder.Count(c => c == '\\');
        score -= Math.Min(0.12, Math.Max(0, depth - 5) * 0.015);
        if (folder.Contains("\\AppData\\", StringComparison.OrdinalIgnoreCase))
        {
            score -= 0.12;
        }

        if (folder.Contains("\\.", StringComparison.Ordinal))
        {
            score -= 0.08;
        }

        if (folder.StartsWith(Home, StringComparison.OrdinalIgnoreCase))
        {
            string rest = folder[Home.Length..].TrimStart('\\');
            if (PlacesOfNote.Any(place => rest.StartsWith(place, StringComparison.OrdinalIgnoreCase)))
            {
                score += 0.05;
            }
        }

        return score;
    }

    /// <summary>"~\Documents\Factures" rather than "C:\Users\me\Documents\Factures".</summary>
    private static string ShortFolder(string folder) =>
        folder.StartsWith(Home, StringComparison.OrdinalIgnoreCase) ? "~" + folder[Home.Length..] : folder;

    /// <summary>What was typed, as Everything reads it, minus the folders left out (see the settings).</summary>
    private string EverythingQuery(string text)
    {
        var query = new System.Text.StringBuilder(text.Trim());
        // One per line: "\n" as saved, "\r" as the settings' text field writes them.
        foreach (string line in _settings.SearchExclusions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            query.Append(" !\"").Append(line.Replace("\"", "", StringComparison.Ordinal)).Append('"');
        }

        return query.ToString();
    }

    /// <summary>Best first, as many as the settings say; a calculation on top, the web search last.</summary>
    private IReadOnlyList<SearchResult> Merge(List<SearchResult> local, List<SearchResult> files)
    {
        var web = local.FirstOrDefault(result => result.Kind == SearchKind.Web);
        var ranked = local.Where(result => result.Kind != SearchKind.Web)
            .Concat(files)
            .OrderByDescending(result => result.Score)
            .Take(web is null ? MaxResults : MaxResults - 1)
            .ToList();
        if (web is not null)
        {
            ranked.Add(web);
        }

        return ranked;
    }

    // ── Opening ─────────────────────────────────────────────────────────────────────────────────────────────

    private static bool Launch(SearchResult result, bool elevated)
    {
        try
        {
            switch (result.Kind)
            {
                case SearchKind.Command:
                    return Run(result.Target);

                case SearchKind.Project when result.Item is RecentProject project:
                    CodeService.Open(project);
                    return true;

                default:
                    var start = new ProcessStartInfo(result.Target) { UseShellExecute = true };
                    if (elevated && result.Kind is SearchKind.Application or SearchKind.File)
                    {
                        start.Verb = "runas";
                    }

                    using (Process.Start(start))
                    {
                    }

                    return true;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Debug.WriteLine($"[Search] Not opened: {result.Target}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The file or folder, selected in its folder in the Explorer.</summary>
    private static bool Reveal(string path)
    {
        try
        {
            using (Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false }))
            {
            }

            return true;
        }
        catch (Win32Exception ex)
        {
            Debug.WriteLine($"[Search] Not revealed: {path}: {ex.Message}");
            return false;
        }
    }

    private static bool Run(string command)
    {
        switch (command)
        {
            case "lock":
                return NativeMethods.LockWorkStation();
            case "sleep":
                return NativeMethods.SetSuspendState(hibernate: false, force: false, disableWakeEvent: false);
            case "recycle":
                return NativeMethods.SHEmptyRecycleBin(0, null, NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI) >= 0;
            case "restart" or "shutdown" or "logoff":
                string arguments = command switch
                {
                    "restart" => "/r /t 0",
                    "shutdown" => "/s /t 0",
                    _ => "/l",
                };
                using (Process.Start(new ProcessStartInfo("shutdown.exe", arguments) { UseShellExecute = false, CreateNoWindow = true }))
                {
                }

                return true;
            default:
                return false;
        }
    }
}
