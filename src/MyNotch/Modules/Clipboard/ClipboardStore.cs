using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MyNotch.Modules.Clipboard;

/// <summary>
/// Keeps the history on disk: <c>%LOCALAPPDATA%\MyNotch\clipboard.json</c>, images next to it as PNG files in
/// <c>clipboard\</c>. Writes happen a moment after the last change, off the UI thread, and replace the file in one
/// move, so a crash never leaves half a history behind.
/// </summary>
public sealed class ClipboardStore : IDisposable
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotch");

    private static readonly string FilePath = Path.Combine(Folder, "clipboard.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DispatcherQueueTimer _saveTimer;
    private Func<IReadOnlyList<ClipboardEntry>>? _snapshot;
    private Task _writing = Task.CompletedTask;

    public ClipboardStore(DispatcherQueue dispatcher)
    {
        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();
    }

    /// <summary>Where images are kept.</summary>
    public static string ImagesFolder { get; } = Path.Combine(Folder, "clipboard");

    /// <summary>Reads the saved history, most relevant first; images whose file is gone are dropped.</summary>
    public IReadOnlyList<ClipboardEntry> Load()
    {
        var entries = new List<ClipboardEntry>();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<StoredHistory>(File.ReadAllText(FilePath), Json) is { } stored)
            {
                foreach (var item in stored.Entries ?? [])
                {
                    if (Restore(item) is { } entry)
                    {
                        entries.Add(entry);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[Clipboard] History not loaded: {ex.Message}");
        }

        DeleteOrphanImages(entries);
        return entries;
    }

    /// <summary>Saves a moment after the last change; <paramref name="snapshot"/> is read on the UI thread then.</summary>
    public void ScheduleSave(Func<IReadOnlyList<ClipboardEntry>> snapshot)
    {
        _snapshot = snapshot;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Encodes the image as PNG into the history folder; returns its path.</summary>
    public static async Task<string> SaveImageAsync(Guid id, byte[] pixels, int width, int height)
    {
        Directory.CreateDirectory(ImagesFolder);
        string path = Path.Combine(ImagesFolder, $"{id:N}.png");
        using var memory = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();

        memory.Seek(0);
        await using var file = File.Create(path);
        await memory.AsStreamForRead().CopyToAsync(file);
        return path;
    }

    public static void DeleteImage(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still displayed somewhere: the next launch cleans it up.
            Debug.WriteLine($"[Clipboard] Image kept for now: {ex.Message}");
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

        var history = new StoredHistory(_snapshot().Select(Store).ToList());
        string json = JsonSerializer.Serialize(history, Json);

        // One write at a time, in order: the last snapshot always wins.
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
            Debug.WriteLine($"[Clipboard] History not saved: {ex.Message}");
        }
    }

    private static StoredEntry Store(ClipboardEntry entry) => new(
        entry.Id,
        entry.Kind == ClipboardKind.Image ? ClipboardKind.Image : ClipboardKind.Text,
        entry.Text,
        entry.ImagePath is { } path ? Path.GetFileName(path) : null,
        entry.ImageWidth,
        entry.ImageHeight,
        entry.ImageHash,
        entry.IsPinned,
        entry.PinnedAt,
        entry.CopiedAt);

    private static ClipboardEntry? Restore(StoredEntry item)
    {
        ClipboardEntry entry;
        if (item.Kind == ClipboardKind.Image)
        {
            string? path = item.Image is { } name ? Path.Combine(ImagesFolder, Path.GetFileName(name)) : null;
            if (path is null || item.Hash is null || !File.Exists(path))
            {
                return null;
            }

            entry = ClipboardEntry.FromImage(item.Id, path, item.Width, item.Height, item.Hash, item.CopiedAt);
        }
        else if (!string.IsNullOrEmpty(item.Text))
        {
            // Classified again: links and colours are recognised the same way they were when copied.
            entry = ClipboardEntry.FromText(item.Text, item.CopiedAt, item.Id);
        }
        else
        {
            return null;
        }

        entry.IsPinned = item.Pinned;
        entry.PinnedAt = item.Pinned ? item.PinnedAt ?? item.CopiedAt : null;
        return entry;
    }

    /// <summary>Images left behind by entries that are gone (removed while still on screen, or a crash).</summary>
    private static void DeleteOrphanImages(IReadOnlyList<ClipboardEntry> entries)
    {
        try
        {
            if (!Directory.Exists(ImagesFolder))
            {
                return;
            }

            var kept = entries.Where(entry => entry.ImagePath is not null)
                .Select(entry => Path.GetFileName(entry.ImagePath!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(ImagesFolder, "*.png"))
            {
                if (!kept.Contains(Path.GetFileName(file)))
                {
                    DeleteImage(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Clipboard] Images not cleaned up: {ex.Message}");
        }
    }

    // Text entries are stored as text: link and colour are derived from it when loading.
    private sealed record StoredHistory(List<StoredEntry>? Entries);

    private sealed record StoredEntry(
        Guid Id,
        ClipboardKind Kind,
        string? Text,
        string? Image,
        int Width,
        int Height,
        string? Hash,
        bool Pinned,
        DateTimeOffset? PinnedAt,
        DateTimeOffset CopiedAt);
}
