using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ilot.Localization;

namespace Ilot.Modules.Notes;

/// <summary>
/// A note: a title and its formatted text, kept in the notes database. The text is RTF (what Word and the editor
/// speak: sizes, bold, lists…); its plain version goes with it, for the cards, the search and the counts.
/// </summary>
public sealed class Note : ObservableObject
{

    private string _title;
    private string _content;
    private string _plain;
    private DateTimeOffset _updatedAt;

    public Note(long id, string title, string content, string plain, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        Id = id;
        _title = title;
        _content = content;
        _plain = plain;
        CreatedAt = createdAt;
        _updatedAt = updatedAt;
    }

    public long Id { get; }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>
    /// RTF; or Markdown, for a note written before the editor became a rich one (it is converted when opened, and
    /// saved as RTF once changed).
    /// </summary>
    public string Content
    {
        get => _content;
        set => SetProperty(ref _content, value);
    }

    /// <summary>The text without its formatting, lines separated by <c>\n</c>.</summary>
    public string Plain
    {
        get => _plain;
        set
        {
            if (SetProperty(ref _plain, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsRich => Content.StartsWith(@"{\rtf", StringComparison.Ordinal);

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set => SetProperty(ref _updatedAt, value);
    }

    /// <summary>Nothing written yet: such a note is not worth keeping.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Clean(Plain));

    /// <summary>The title, or else the first line of the text: "Courses".</summary>
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title.Trim() : Line(0) ?? Loc.T("Sans titre");

    /// <summary>A glimpse of the text for its card: its first lines (after the first, when that one is the title).</summary>
    public string Preview
    {
        get
        {
            var lines = Lines().Skip(string.IsNullOrWhiteSpace(Title) ? 1 : 0).Take(3);
            string preview = string.Join(" · ", lines);
            return preview.Length > 0 ? preview : Loc.T("Note vide");
        }
    }

    /// <summary>When it was last changed, the way a person says it: "à l'instant", "il y a 5 min", "hier", "12 oct.".</summary>
    public string WhenTextAt(DateTimeOffset now)
    {
        var ago = now - UpdatedAt;
        var local = UpdatedAt.ToLocalTime();
        return ago switch
        {
            { TotalMinutes: < 1 } => Loc.T("À l'instant"),
            { TotalHours: < 1 } => Loc.F("Il y a {0} min", (int)ago.TotalMinutes),
            _ when local.Date == now.ToLocalTime().Date => local.ToString("HH:mm", Loc.Culture),
            _ when local.Date == now.ToLocalTime().Date.AddDays(-1) => Loc.T("Hier"),
            _ when local.Year == now.ToLocalTime().Year => local.ToString("d MMM", Loc.Culture),
            _ => local.ToString("d MMM yyyy", Loc.Culture),
        };
    }

    /// <summary>A plain version of Markdown, for notes written before the rich editor: its marks left out.</summary>
    public static string PlainFromMarkdown(string markdown) => string.Join('\n', markdown.Split('\n')
        .Where(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal))
        .Select(line => line.Trim().TrimStart('#', '>', '-', '*', '+', ' ')
            .Replace("[ ] ", "☐ ", StringComparison.Ordinal).Replace("[x] ", "☑ ", StringComparison.OrdinalIgnoreCase)
            .Replace("**", "", StringComparison.Ordinal).Replace("~~", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal)));

    private string? Line(int index) => Lines().Skip(index).FirstOrDefault();

    /// <summary>The lines with some text, check boxes left out.</summary>
    private IEnumerable<string> Lines() => Plain.Split('\n')
        .Select(line => Clean(line).Trim())
        .Where(line => line.Length > 0)
        .Select(line => line.Length > 120 ? line[..120] : line);

    private static string Clean(string text) => text.Replace("☐", "", StringComparison.Ordinal).Replace("☑", "", StringComparison.Ordinal);
}
