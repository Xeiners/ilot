using System.Diagnostics;
using System.Text.Json;

namespace Ilot.Modules.Search;

/// <summary>
/// What was opened from the search, how often and when last: what one opens often, and lately, comes first next time.
/// Kept in <c>%LOCALAPPDATA%\Ilot\search.json</c>, written a moment after a use, off the UI thread.
/// </summary>
public sealed class SearchHistory
{
    private const int MaxKept = 500;
    private static readonly TimeSpan WriteDelay = TimeSpan.FromSeconds(1);

    private static readonly string FilePath = Path.Combine(
        AppData.Folder, "search.json");

    private readonly Dictionary<string, Use> _uses;
    private Task _writing = Task.CompletedTask;
    private bool _dirty;

    public SearchHistory() => _uses = Load();

    /// <summary>A bonus for <paramref name="key"/> (about 0 to 0.6): the more it was opened, and the more lately, the more.</summary>
    public double Boost(string key)
    {
        if (!_uses.TryGetValue(key, out var use))
        {
            return 0;
        }

        double age = (DateTimeOffset.Now - use.Last).TotalDays;
        double recency = age < 1 ? 0.2 : age < 7 ? 0.1 : age < 30 ? 0.04 : 0;
        return Math.Min(0.4, Math.Log2(1 + use.Count) * 0.1) + recency;
    }

    public void Record(string key)
    {
        lock (_uses)
        {
            _uses[key] = _uses.TryGetValue(key, out var use) ? use with { Count = use.Count + 1, Last = DateTimeOffset.Now } : new Use(1, DateTimeOffset.Now);
            if (_uses.Count > MaxKept)
            {
                foreach (var old in _uses.OrderBy(pair => pair.Value.Last).Take(_uses.Count - MaxKept).Select(pair => pair.Key).ToList())
                {
                    _uses.Remove(old);
                }
            }

            if (_dirty)
            {
                return;
            }

            _dirty = true;
        }

        // One write for a burst of uses, after the previous one, off the UI thread.
        _writing = _writing.ContinueWith(_ => Task.Delay(WriteDelay), TaskScheduler.Default).Unwrap().ContinueWith(_ => Save(), TaskScheduler.Default);
    }

    private void Save()
    {
        Dictionary<string, Use> copy;
        lock (_uses)
        {
            _dirty = false;
            copy = new Dictionary<string, Use>(_uses);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(copy));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Search] History not saved: {ex.Message}");
        }
    }

    private static Dictionary<string, Use> Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, Use>>(File.ReadAllText(FilePath)) is { } uses)
            {
                return uses;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[Search] History not read: {ex.Message}");
        }

        return [];
    }

    public sealed record Use(int Count, DateTimeOffset Last);
}
