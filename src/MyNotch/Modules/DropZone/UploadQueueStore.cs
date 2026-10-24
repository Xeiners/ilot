using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;
using MyNotch.Modules.DropZone.Hosts;

namespace MyNotch.Modules.DropZone;

/// <summary>
/// The upload queue on disk, in <c>%LOCALAPPDATA%\MyNotch\UploadsQueue\</c>: <c>queue.json</c>, plus the files the
/// queue owns (archives made from several files or a folder). Unfinished uploads survive a restart; finished ones
/// are kept with their link until it expires.
/// </summary>
public sealed class UploadQueueStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DispatcherQueueTimer _saveTimer;
    private Func<IReadOnlyList<UploadItem>>? _snapshot;
    private Task _writing = Task.CompletedTask;

    public UploadQueueStore(DispatcherQueue dispatcher)
    {
        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();
    }

    /// <summary>The queue folder: archives and copies waiting to be sent live here.</summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotch", "UploadsQueue");

    private static string FilePath => Path.Combine(Folder, "queue.json");

    /// <summary>
    /// The saved queue, newest first. What was being sent goes back in line; an archive that was still being made
    /// is gone (it was never complete); links that have expired are forgotten.
    /// </summary>
    public IReadOnlyList<UploadItem> Load()
    {
        var items = new List<UploadItem>();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<StoredUpload>>(File.ReadAllText(FilePath), Json) is { } stored)
            {
                var now = DateTimeOffset.Now;
                foreach (var entry in stored)
                {
                    if (Restore(entry, now) is { } item)
                    {
                        items.Add(item);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[Uploads] Queue not loaded: {ex.Message}");
        }

        DeleteOrphans(items);
        return items;
    }

    public void ScheduleSave(Func<IReadOnlyList<UploadItem>> snapshot)
    {
        _snapshot = snapshot;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>A file of the queue folder for a new archive or copy.</summary>
    public static string StagedPath(Guid id, string name)
    {
        Directory.CreateDirectory(Folder);
        return Path.Combine(Folder, $"{id:N}{Path.GetExtension(name)}");
    }

    public static void DeleteStaged(UploadItem item)
    {
        if (!item.IsStaged)
        {
            return;
        }

        try
        {
            File.Delete(item.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere: the next launch cleans it up.
            Debug.WriteLine($"[Uploads] Staged file kept for now: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_saveTimer.IsRunning)
        {
            _saveTimer.Stop();
            Save();
        }

        _writing.Wait(TimeSpan.FromSeconds(2));
    }

    private void Save()
    {
        if (_snapshot is null)
        {
            return;
        }

        var stored = _snapshot().Where(item => item.State != UploadState.Preparing).Select(Store).ToList();
        string json = JsonSerializer.Serialize(stored, Json);
        _writing = _writing.ContinueWith(_ => Write(json), TaskScheduler.Default);
    }

    private static void Write(string json)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Uploads] Queue not saved: {ex.Message}");
        }
    }

    private static StoredUpload Store(UploadItem item) => new(
        item.Id,
        item.Name,
        item.FilePath,
        item.Size,
        item.IsArchive,
        item.IsStaged,
        item.Host,
        item.Expiry,
        item.CreatedAt,
        item.State,
        item.Url,
        item.UploadedAt,
        item.Error);

    private static UploadItem? Restore(StoredUpload stored, DateTimeOffset now)
    {
        // A host since removed (Litterbox): what was waiting goes to the default host, its links are forgotten.
        bool pending = stored.State is UploadState.Queued or UploadState.Uploading or UploadState.WaitingForNetwork;
        var host = UploadHosts.Find(stored.Host);
        if (host is null && !pending)
        {
            return null;
        }

        var item = new UploadItem(
            stored.Id, stored.Name, stored.FilePath, stored.Size, stored.IsArchive, stored.IsStaged,
            (host ?? UploadHosts.All[0]).Id, stored.Expiry, stored.CreatedAt)
        {
            Url = stored.Url,
            UploadedAt = stored.UploadedAt,
            Error = stored.Error,
        };

        switch (stored.State)
        {
            case UploadState.Done when item.ExpiresAt is null || item.ExpiresAt > now:
                item.State = UploadState.Done;
                return item;

            case UploadState.Failed:
                item.State = UploadState.Failed;
                return item;

            case UploadState.Queued or UploadState.Uploading or UploadState.WaitingForNetwork when File.Exists(stored.FilePath):
                item.State = UploadState.Queued;
                return item;

            default:
                return null;
        }
    }

    /// <summary>Archives and copies that no entry of the queue owns any more.</summary>
    private static void DeleteOrphans(IReadOnlyList<UploadItem> items)
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            var owned = items.Where(item => item.IsStaged).Select(item => item.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(Folder))
            {
                if (!file.Equals(FilePath, StringComparison.OrdinalIgnoreCase) && !owned.Contains(file))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Uploads] Queue folder not cleaned up: {ex.Message}");
        }
    }

    private sealed record StoredUpload(
        Guid Id,
        string Name,
        string FilePath,
        long Size,
        bool IsArchive,
        bool IsStaged,
        string? Host,
        string Expiry,
        DateTimeOffset CreatedAt,
        UploadState State,
        string? Url,
        DateTimeOffset? UploadedAt,
        string? Error);
}
