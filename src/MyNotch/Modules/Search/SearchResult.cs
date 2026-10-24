using MyNotch.Localization;

namespace MyNotch.Modules.Search;

public enum SearchKind
{
    Calculation,
    Application,
    Project,
    Setting,
    Command,
    Folder,
    File,
    Web,
}

/// <summary>One line of the search's results: what it is, how it shows, and what opening it does.</summary>
public sealed class SearchResult
{
    /// <summary>What it is, the same from one search to the next ("app:…", "file:C:\…"): the history counts on it.</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    /// <summary>Where it is, or what it does ("C:\Users\…", "Paramètres Windows"…).</summary>
    public string Subtitle { get; init; } = "";

    public required SearchKind Kind { get; init; }

    /// <summary>What opening it means: an app's id, a path, a "ms-settings:" page, a command, the result of a calculation…</summary>
    public required string Target { get; init; }

    /// <summary>A Segoe Fluent Icons glyph, shown until (or instead of) the item's own icon.</summary>
    public string Glyph { get; init; } = "";

    /// <summary>What the shell knows the item by, for its own icon (a path, "shell:AppsFolder\…"); <c>null</c>: the glyph.</summary>
    public string? IconSource { get; init; }

    /// <summary>Icons alike share one (".pdf" for every PDF…); defaults to <see cref="IconSource"/>.</summary>
    public string? IconKey { get; init; }

    /// <summary>A colour of its own for the glyph's tile (a project's…); <c>null</c>: the kind's.</summary>
    public Windows.UI.Color? Tint { get; init; }

    /// <summary>Parts of <see cref="Title"/> that matched the search (start, length), shown brighter.</summary>
    public IReadOnlyList<(int Start, int Length)> Matches { get; set; } = [];

    /// <summary>How well it answers the search, history and kind included: the results are in this order.</summary>
    public double Score { get; set; }

    /// <summary>A file or folder (or app, project) on the disk: it can be shown in its folder, and its path copied.</summary>
    public string? Path { get; init; }

    /// <summary>What it stands for, when opening needs more than <see cref="Target"/> (a VS Code project…).</summary>
    public object? Item { get; init; }

    /// <summary>Opening asks first (restart, shut down…): a second Enter confirms.</summary>
    public bool NeedsConfirmation { get; init; }

    /// <summary>What the kind is called, at the right of the line.</summary>
    public string KindLabel => Kind switch
    {
        SearchKind.Calculation => Loc.T("Calcul"),
        SearchKind.Application => Loc.T("Application"),
        SearchKind.Project => Loc.T("Projet"),
        SearchKind.Setting => Loc.T("Paramètres"),
        SearchKind.Command => Loc.T("Commande"),
        SearchKind.Folder => Loc.T("Dossier"),
        SearchKind.File => Loc.T("Fichier"),
        _ => "Web",
    };
}
