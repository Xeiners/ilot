using System.Text.Json;
using System.Text.Json.Serialization;
using Ilot.Localization;

namespace Ilot.Modules.Assistant;

/// <summary>What the assistant did to the user's files.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FileChange>))]
public enum FileChange
{
    /// <summary>A folder created.</summary>
    Folder,

    /// <summary>A text file created.</summary>
    File,

    /// <summary>A file or a folder copied (<see cref="FileEntry.From"/> to <see cref="FileEntry.To"/>).</summary>
    Copy,

    /// <summary>A file or a folder moved, or renamed.</summary>
    Move,

    /// <summary>A file or a folder deleted: put in the Recycle Bin (it is restored from there, by the user).</summary>
    Delete,
}

/// <summary>One thing the assistant did to the user's files: when, what, from where (a copy, a move) and to where.</summary>
public sealed class FileEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    public FileChange Change { get; init; }

    public string? From { get; init; }

    public string To { get; init; } = "";

    /// <summary>Taken back (« Annuler »): what it made in the Recycle Bin, or what it moved back where it was.</summary>
    public bool Undone { get; set; }

    /// <summary>In words, for the journal: « Copié Projet → Bureau ».</summary>
    public string Describe() => Change switch
    {
        FileChange.Folder => Loc.F("Dossier créé : {0}", To),
        FileChange.File => Loc.F("Fichier créé : {0}", To),
        FileChange.Copy => Loc.F("Copié : {0} → {1}", From ?? "", To),
        FileChange.Delete => Loc.F("Supprimé (à la Corbeille) : {0}", To),
        _ => Loc.F("Déplacé : {0} → {1}", From ?? "", To),
    };

    /// <summary>Taken back from here: a deletion is not (it is in the Recycle Bin, restored from there).</summary>
    public bool CanUndo => !Undone && Change != FileChange.Delete;
}

/// <summary>
/// The journal of what the assistant did to the user's files (created, copied, moved, renamed): the last ones kept on
/// this PC (<c>%LOCALAPPDATA%\Ilot\assistant-files.json</c>), shown in the settings, each one to take back
/// (<see cref="UndoAsync"/>): what it made goes to the Recycle Bin (recoverable from there: never destroyed silently),
/// what it moved goes back where it was. Nothing is ever replaced doing so.
/// </summary>
public sealed class FileJournal
{
    /// <summary>The journal keeps this many, the latest.</summary>
    private const int Kept = 200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly List<FileEntry> _entries;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly object _lock = new();

    public FileJournal()
        : this(Path.Combine(AppData.Folder, "assistant-files.json"), Interop.NativeMethods.Recycle)
    {
    }

    /// <summary>A journal there, putting things in the Recycle Bin so (the tests: elsewhere, never in the user's bin).</summary>
    internal FileJournal(string path, Func<string, string?> recycle)
    {
        _path = path;
        Recycle = recycle;
        _entries = Load(path);
    }

    private Func<string, string?> Recycle { get; }

    /// <summary>Raised when an entry is added or taken back (on the thread that did it).</summary>
    public event EventHandler? Changed;

    /// <summary>The entries, the latest first.</summary>
    public IReadOnlyList<FileEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>The latest one done since Ilot started, not taken back yet: Ctrl + Z takes it back.</summary>
    public FileEntry? LastOfThisRun
    {
        get
        {
            lock (_lock)
            {
                return _entries.FirstOrDefault(entry => entry.CanUndo && entry.At >= _started);
            }
        }
    }

    /// <summary>Notes what was done.</summary>
    public FileEntry Add(FileChange change, string? from, string to)
    {
        var entry = new FileEntry { Change = change, From = from, To = to };
        lock (_lock)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > Kept)
            {
                _entries.RemoveRange(Kept, _entries.Count - Kept);
            }
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    /// <summary>
    /// <paramref name="path"/> deleted, as the user allowed it: into the Recycle Bin (Windows asks before destroying what
    /// does not fit there), and noted. <c>null</c> once done, else why not (nothing changed).
    /// </summary>
    public async Task<(string? Refusal, FileEntry? Entry)> DeleteAsync(string path)
    {
        if (await Task.Run(() => Recycle(path)) is { } why)
        {
            return (why, null);
        }

        return File.Exists(path) || Directory.Exists(path)
            ? (Loc.T("Il est toujours là : Windows ne l'a pas supprimé."), null)
            : (null, Add(FileChange.Delete, null, path));
    }

    /// <summary>
    /// <paramref name="entry"/> taken back: what it made (a folder, a file, a copy) into the Recycle Bin; what it moved,
    /// back where it was (if nothing has taken that place since). <c>null</c> once done, else why not (nothing changed).
    /// </summary>
    public async Task<string?> UndoAsync(FileEntry entry)
    {
        if (entry.Undone)
        {
            return Loc.T("Déjà annulé.");
        }

        if (entry.Change == FileChange.Delete)
        {
            return Loc.T("Il est dans la Corbeille : restaurez-le de là.");
        }

        bool there = File.Exists(entry.To) || Directory.Exists(entry.To);
        if (!there)
        {
            return Loc.F("{0} n'est plus là : rien à annuler.", Path.GetFileName(entry.To));
        }

        try
        {
            if (entry.Change == FileChange.Move)
            {
                string back = entry.From!;
                if (File.Exists(back) || Directory.Exists(back))
                {
                    return Loc.F("Quelque chose porte déjà son ancien nom : {0}", back);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(back)!);
                await FileTransfer.MoveAsync(entry.To, back, CancellationToken.None);
            }
            else
            {
                if (Path.GetPathRoot(entry.To) is { } root && new DriveInfo(root).DriveType != DriveType.Fixed)
                {
                    return Loc.T("Pas de Corbeille sur ce disque : supprimez-le vous-même si vous le voulez.");
                }

                if (await Task.Run(() => Recycle(entry.To)) is { } why)
                {
                    return why;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ex.Message;
        }

        entry.Undone = true;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    private static List<FileEntry> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<FileEntry>>(File.ReadAllText(path), Json) is { } kept)
            {
                return [.. kept.Where(entry => entry.To.Length > 0).OrderByDescending(entry => entry.At).Take(Kept)];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ErrorLog.Write($"[FileJournal] Not read: {ex.Message}");
        }

        return [];
    }

    /// <summary>Written at once (small, and rare): a temporary file, then put in place.</summary>
    private void Save()
    {
        try
        {
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_entries, Json);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write($"[FileJournal] Not written: {ex.Message}");
        }
    }
}
