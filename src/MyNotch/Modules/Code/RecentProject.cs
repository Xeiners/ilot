using MyNotch.Localization;

namespace MyNotch.Modules.Code;

public enum ProjectKind
{
    Folder,

    /// <summary>A <c>.code-workspace</c> file: several folders opened together.</summary>
    Workspace,

    /// <summary>A folder on another machine (WSL, SSH, container…), opened through VS Code's remote extensions.</summary>
    Remote,
}

/// <summary>A project VS Code opened recently.</summary>
/// <param name="Name">Folder or workspace name.</param>
/// <param name="Location">Where it is: a local path, or for a remote one the URI VS Code knows it by.</param>
/// <param name="Place">Where it is, as shown: "~\dev", "WSL : Ubuntu · /home/me"…</param>
/// <param name="Stack">What it is made with, when recognised (see <see cref="ProjectStack.Detect"/>).</param>
public sealed record RecentProject(string Name, string Location, ProjectKind Kind, string Place, ProjectStack? Stack = null)
{
    /// <summary>The project's colour: its stack's, or one of its own, always the same.</summary>
    public Windows.UI.Color Color => Stack?.Color ?? ProjectStack.ColorFor(Name);

    /// <summary>What it is made with, or else what it is: "React Native", "Dossier", "Distant"…</summary>
    public string Label => Stack?.Label ?? Loc.T(Kind == ProjectKind.Remote ? "Distant" : "Dossier");

    /// <summary>Segoe Fluent Icons glyph of the kind.</summary>
    public string Glyph => Kind switch
    {
        ProjectKind.Workspace => "",
        ProjectKind.Remote => "",
        _ => "",
    };

    public bool IsLocal => Kind != ProjectKind.Remote;
}
