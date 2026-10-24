using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Ilot.Modules.Clipboard;
using Ilot.Notch;
using Ilot.Localization;

namespace Ilot.Modules.Code;

/// <summary>The recent VS Code projects, brought up to date each time they are shown (« code » in the search), and what to do with them.</summary>
public sealed class CodeViewModel(CodeService service) : ObservableObject
{
    private const string CopyGlyph = "";
    private const string ErrorGlyph = "";

    private NotchContext? _notch;
    private string? _message;
    private bool _refreshing;

    public ObservableCollection<RecentProject> Projects { get; } = [];

    /// <summary>Why there is nothing to show (not installed, no project, unreadable), or <c>null</c>.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => Message is not null;

    public void Attach(NotchContext notch) => _notch = notch;

    /// <summary>Reads the history again (off the UI thread); the list only changes if the projects did.</summary>
    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            if (!CodeService.IsInstalled)
            {
                Show([], Loc.T("VS Code n'est pas installé sur ce PC."));
                return;
            }

            var projects = await service.GetRecentAsync();
            Show(projects, projects.Count == 0 ? Loc.T("Aucun projet récent : ouvrez un dossier dans VS Code.") : null);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[Code] {ex}");
            Show([], Loc.T("L'historique de VS Code est illisible pour le moment."));
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>A project was opened (in VS Code, or its folder): what showed it (the search) is done.</summary>
    public event EventHandler? Launched;

    public void Open(RecentProject project) => Run(() => CodeService.Open(project), Loc.T("VS Code ne s'est pas lancé"));

    public void Reveal(RecentProject project) => Run(() => CodeService.Reveal(project), Loc.T("Dossier introuvable"));

    /// <summary>The path (or for a remote project, its URI) on the clipboard, and the notch says so.</summary>
    public async Task CopyPathAsync(RecentProject project)
    {
        if (await SystemClipboard.WriteTextAsync(project.Location))
        {
            _notch?.Announce(CopyGlyph, Loc.T("Chemin copié"));
        }
    }

    private void Show(IReadOnlyList<RecentProject> projects, string? message)
    {
        Message = message;
        if (!projects.SequenceEqual(Projects))
        {
            Projects.Clear();
            foreach (var project in projects)
            {
                Projects.Add(project);
            }
        }
    }

    private void Run(Action action, string failure)
    {
        try
        {
            action();
            Launched?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Debug.WriteLine($"[Code] {failure}: {ex.Message}");
            _notch?.Announce(ErrorGlyph, failure, Windows.UI.Color.FromArgb(255, 0xFF, 0x6B, 0x6B));
        }
    }
}
