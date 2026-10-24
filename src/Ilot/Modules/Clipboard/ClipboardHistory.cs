using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Ilot.Settings;

namespace Ilot.Modules.Clipboard;

/// <summary>
/// The clipboard history: every copy is recorded as it happens, pinned entries first, then the latest copies.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Copying something already there brings it back up (or leaves it in place if pinned): no duplicates.</item>
/// <item>Only the latest unpinned copies are kept, up to <see cref="AppSettings.ClipboardLimit"/>: past it, each new copy
/// pushes the oldest out. Pinned entries are never pushed out, and do not count.</item>
/// <item>What the notch puts on the clipboard itself (copying an entry again) is not recorded a second time.</item>
/// </list>
/// </remarks>
public sealed class ClipboardHistory : IDisposable
{
    // Our own writes come back as clipboard changes: recognised for a moment, then forgotten.
    private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(2);

    private readonly ObservableCollection<ClipboardEntry> _entries = [];
    private readonly AppSettings _settings;
    private readonly ClipboardStore _store;
    private readonly ClipboardMonitor _monitor;
    private bool _reading;
    private bool _readAgain;
    private Echo? _echo;

    public ClipboardHistory(DispatcherQueue dispatcher, AppSettings settings, ClipboardStore store)
    {
        _settings = settings;
        _store = store;
        Entries = new ReadOnlyObservableCollection<ClipboardEntry>(_entries);

        var saved = store.Load();
        foreach (var entry in saved.Where(entry => entry.IsPinned).OrderByDescending(entry => entry.PinnedAt))
        {
            _entries.Add(entry);
        }

        foreach (var entry in saved.Where(entry => !entry.IsPinned).OrderByDescending(entry => entry.CopiedAt).Take(Limit))
        {
            _entries.Add(entry);
        }

        _monitor = new ClipboardMonitor(dispatcher);
        _monitor.Changed += OnClipboardChanged;
    }

    /// <summary>Pinned entries (latest pin first), then the latest copies (latest first).</summary>
    public ReadOnlyObservableCollection<ClipboardEntry> Entries { get; }

    /// <summary>
    /// How many unpinned copies are kept. Lowering it in the settings removes nothing at once: the extra copies go
    /// with the next one, never while a slider is being dragged.
    /// </summary>
    private int Limit => (int)_settings.ClipboardLimit;

    /// <summary>Raised when a copy is recorded, new or brought back up.</summary>
    public event EventHandler<ClipboardEntry>? Recorded;

    private int PinnedCount
    {
        get
        {
            int count = 0;
            while (count < _entries.Count && _entries[count].IsPinned)
            {
                count++;
            }

            return count;
        }
    }

    /// <summary>Puts the entry back on the clipboard, as it was copied.</summary>
    public Task<bool> CopyAsync(ClipboardEntry entry)
    {
        bool image = entry.Kind == ClipboardKind.Image;
        _echo = new Echo(entry.Signature, image, DateTimeOffset.Now + EchoWindow);
        return image ? SystemClipboard.WriteImageAsync(entry.ImagePath!) : SystemClipboard.WriteTextAsync(entry.Text!);
    }

    /// <summary>Copies a text derived from an entry (a colour in RGB…), without recording it.</summary>
    public Task<bool> CopyTextAsync(string text)
    {
        _echo = new Echo(ClipboardEntry.TextSignature(text), Image: false, DateTimeOffset.Now + EchoWindow);
        return SystemClipboard.WriteTextAsync(text);
    }

    public void SetPinned(ClipboardEntry entry, bool pinned)
    {
        int index = _entries.IndexOf(entry);
        if (index < 0 || entry.IsPinned == pinned)
        {
            return;
        }

        entry.IsPinned = pinned;
        entry.PinnedAt = pinned ? DateTimeOffset.Now : null;
        if (pinned)
        {
            _entries.Move(index, 0);
        }
        else
        {
            // Back among the copies, where its date puts it. It may briefly make one copy too many: the oldest
            // goes with the next copy, rather than the entry the user just unpinned.
            int pinnedCount = PinnedCount;
            int newer = _entries.Skip(pinnedCount).Count(other => other != entry && other.CopiedAt > entry.CopiedAt);
            _entries.Move(index, pinnedCount + newer);
        }

        Save();
    }

    public void Remove(ClipboardEntry entry)
    {
        if (_entries.Remove(entry))
        {
            ClipboardStore.DeleteImage(entry.ImagePath);
            Save();
        }
    }

    /// <summary>Forgets every copy that is not pinned.</summary>
    public void Clear()
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (!_entries[i].IsPinned)
            {
                var entry = _entries[i];
                _entries.RemoveAt(i);
                ClipboardStore.DeleteImage(entry.ImagePath);
            }
        }

        Save();
    }

    public void Dispose() => _monitor.Dispose();

    private async void OnClipboardChanged(object? sender, EventArgs e)
    {
        // One read at a time: a copy made while the previous one is still being read is read right after.
        if (_reading)
        {
            _readAgain = true;
            return;
        }

        _reading = true;
        try
        {
            do
            {
                _readAgain = false;
                if (_settings.RecordClipboard && await SystemClipboard.ReadAsync() is { } capture)
                {
                    await RecordAsync(capture);
                }
            }
            while (_readAgain);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipboard] Copy not recorded: {ex}");
        }
        finally
        {
            _reading = false;
        }
    }

    private async Task RecordAsync(ClipboardCapture capture)
    {
        var now = DateTimeOffset.Now;
        bool image = capture is CapturedImage;
        string signature = capture switch
        {
            CapturedImage captured => ClipboardEntry.ImageSignature(captured.Hash),
            CapturedText captured => ClipboardEntry.TextSignature(captured.Text),
            _ => throw new ArgumentOutOfRangeException(nameof(capture)),
        };

        if (IsEcho(signature, image, now))
        {
            return;
        }

        var entry = _entries.FirstOrDefault(existing => existing.Signature == signature);
        if (entry is not null)
        {
            entry.CopiedAt = now;
            if (!entry.IsPinned)
            {
                _entries.Move(_entries.IndexOf(entry), PinnedCount);
            }
        }
        else
        {
            entry = capture switch
            {
                CapturedImage captured => await CreateImageEntryAsync(captured, now),
                CapturedText captured => ClipboardEntry.FromText(captured.Text, now),
                _ => null,
            };
            if (entry is null)
            {
                return;
            }

            _entries.Insert(PinnedCount, entry);
            Trim();
        }

        Save();
        Recorded?.Invoke(this, entry);
    }

    private static async Task<ClipboardEntry?> CreateImageEntryAsync(CapturedImage image, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        try
        {
            string path = await ClipboardStore.SaveImageAsync(id, image.Pixels, image.Width, image.Height);
            return ClipboardEntry.FromImage(id, path, image.Width, image.Height, image.Hash, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"[Clipboard] Image not kept: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Our own copy coming back. An image may come back re-encoded, with slightly different pixels: any image
    /// counts while an image copy is expected.
    /// </summary>
    private bool IsEcho(string signature, bool image, DateTimeOffset now)
    {
        if (_echo is not { } echo || now > echo.Until || (echo.Signature != signature && !(image && echo.Image)))
        {
            return false;
        }

        _echo = null;
        return true;
    }

    /// <summary>The oldest copies go once there are more than the limit; the list ends with the oldest one.</summary>
    private void Trim()
    {
        while (_entries.Count - PinnedCount > Limit)
        {
            var oldest = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            ClipboardStore.DeleteImage(oldest.ImagePath);
        }
    }

    private void Save() => _store.ScheduleSave(() => _entries.ToList());

    private readonly record struct Echo(string Signature, bool Image, DateTimeOffset Until);
}
