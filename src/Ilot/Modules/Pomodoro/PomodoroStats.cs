using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Ilot.Modules.Pomodoro;

/// <summary>What was done on a day: the focuses run to their end, and their minutes.</summary>
public sealed record PomodoroDay(int Sessions, int Minutes);

/// <summary>
/// The focuses done, day by day (<c>%LOCALAPPDATA%\Ilot\pomodoro.json</c>, the last <see cref="DaysKept"/> days):
/// what the tab says of today. Written as each focus ends, off the UI thread, the file replaced in one move.
/// </summary>
public sealed class PomodoroStats
{
    public const int DaysKept = 60;

    private static readonly string DefaultPath = Path.Combine(
        AppData.Folder, "pomodoro.json");

    private readonly string _path;
    private readonly Func<DateTime> _today;
    private Dictionary<string, PomodoroDay>? _days;
    private Task _writing = Task.CompletedTask;

    /// <param name="path">Where it is kept: the default, or another file (the tests', never the user's).</param>
    /// <param name="today">Today (the tests' own day).</param>
    public PomodoroStats(string? path = null, Func<DateTime>? today = null)
    {
        _path = path ?? DefaultPath;
        _today = today ?? (() => DateTime.Today);
    }

    /// <summary>Today's focuses.</summary>
    public PomodoroDay Today => Days.GetValueOrDefault(Key(_today()), new PomodoroDay(0, 0));

    /// <summary>Raised when a focus is counted.</summary>
    public event EventHandler? Changed;

    private Dictionary<string, PomodoroDay> Days => _days ??= Load();

    /// <summary>A focus ran to its end, <paramref name="length"/> long: counted for today.</summary>
    public void Count(TimeSpan length)
    {
        var today = Today;
        Days[Key(_today())] = new PomodoroDay(today.Sessions + 1, today.Minutes + (int)Math.Round(length.TotalMinutes));
        string oldest = Key(_today().AddDays(-DaysKept));
        foreach (string day in Days.Keys.Where(day => string.CompareOrdinal(day, oldest) < 0).ToList())
        {
            Days.Remove(day);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        Save();
    }

    /// <summary>The writing going on, if any (the tests wait for it).</summary>
    internal Task Writing => _writing;

    private static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private Dictionary<string, PomodoroDay> Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Dictionary<string, PomodoroDay>>(File.ReadAllText(_path)) is { } days)
            {
                return days;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[Pomodoro] Stats not loaded: {ex.Message}");
        }

        return [];
    }

    private void Save()
    {
        string json = JsonSerializer.Serialize(Days);
        _writing = _writing.ContinueWith(_ =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path + ".tmp", json);
                File.Move(_path + ".tmp", _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Pomodoro] Stats not saved: {ex.Message}");
            }
        }, TaskScheduler.Default);
    }
}
