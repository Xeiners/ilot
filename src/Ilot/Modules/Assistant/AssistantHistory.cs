using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace Ilot.Modules.Assistant;

/// <summary>A message of a conversation kept: who said it, what, and how many images went with it (not kept).</summary>
public sealed record SavedMessage(bool FromUser, string Text, int Images = 0);

/// <summary>A conversation with the assistant, kept: its first question as title, when it last went on, its messages.</summary>
public sealed record SavedConversation(Guid Id, string Title, DateTimeOffset Updated, IReadOnlyList<SavedMessage> Messages);

/// <summary>
/// The conversations with the assistant, kept on disk (<c>%LOCALAPPDATA%\Ilot\assistant-history.json</c>, the most
/// recent <see cref="MostKept"/>), latest first: each one is kept as it goes on (see AssistantConversation), and can be
/// opened again, to go on with it. Written a moment after the last change, off the UI thread, the file replaced in one
/// move: a crash never leaves half a history behind. Images are not kept, only that there were some.
/// </summary>
public sealed class AssistantHistory
{
    public const int MostKept = 100;

    private static readonly string DefaultPath = Path.Combine(
        AppData.Folder, "assistant-history.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly DispatcherQueueTimer _saveTimer;
    private readonly string _path;
    private List<SavedConversation>? _conversations;
    private Task _writing = Task.CompletedTask;

    /// <param name="path">Where it is kept: the default, or another file (the tests', never the user's).</param>
    public AssistantHistory(DispatcherQueue dispatcher, string? path = null)
    {
        _path = path ?? DefaultPath;
        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(800);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();
    }

    /// <summary>The conversations kept, latest first (read from the disk the first time they are asked for).</summary>
    public IReadOnlyList<SavedConversation> Conversations => _conversations ??= Load();

    /// <summary>Raised on the UI thread when a conversation is kept, or forgotten.</summary>
    public event EventHandler? Changed;

    /// <summary>Keeps <paramref name="conversation"/> (as it is now, in place of what was kept of it), first.</summary>
    public void Keep(SavedConversation conversation)
    {
        var all = (List<SavedConversation>)Conversations;
        all.RemoveAll(kept => kept.Id == conversation.Id);
        all.Insert(0, conversation);
        if (all.Count > MostKept)
        {
            all.RemoveRange(MostKept, all.Count - MostKept);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public void Forget(Guid id)
    {
        if (((List<SavedConversation>)Conversations).RemoveAll(kept => kept.Id == id) > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }
    }

    public void ForgetAll()
    {
        ((List<SavedConversation>)Conversations).Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    private List<SavedConversation> Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<List<SavedConversation>>(File.ReadAllText(_path), Json) is { } kept)
            {
                return [.. kept.Where(conversation => conversation.Messages is { Count: > 0 }).OrderByDescending(conversation => conversation.Updated)];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[Assistant] History not loaded: {ex.Message}");
        }

        return [];
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Written off the UI thread, after the write before (never two at once), into a file then moved over the old one.</summary>
    private void Save()
    {
        string json = JsonSerializer.Serialize(Conversations, Json);
        _writing = _writing.ContinueWith(_ =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Assistant] History not saved: {ex.Message}");
            }
        }, TaskScheduler.Default);
    }
}
