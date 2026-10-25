using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace Ilot.Settings;

/// <summary>
/// Loads <see cref="AppSettings"/> from <c>%LOCALAPPDATA%\Ilot\settings.json</c> and saves every change, a moment
/// after the last one (dragging a slider writes the file once, not at every step).
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly string FilePath = Path.Combine(
        AppData.Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly DispatcherQueueTimer _saveTimer;

    public SettingsStore(DispatcherQueue dispatcher)
    {
        Settings = Load();
        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();
        Settings.PropertyChanged += (_, _) =>
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        };
    }

    public AppSettings Settings { get; }

    public void Dispose()
    {
        if (_saveTimer.IsRunning)
        {
            _saveTimer.Stop();
            Save();
        }
    }

    private static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(FilePath)) is { } stored)
            {
                settings.ShowLyrics = stored.ShowLyrics;
                settings.CompactLyricsFontSize = stored.CompactLyricsFontSize;
                settings.HideInFullscreen = stored.HideInFullscreen;
                settings.RecordClipboard = stored.RecordClipboard;
                settings.SignalCopies = stored.SignalCopies;
                settings.ClipboardLimit = stored.ClipboardLimit;
                settings.UploadExpiry = stored.UploadExpiry;
                settings.UploadHost = stored.UploadHost;
                settings.ReplaceWindowsClipboard = stored.ReplaceWindowsClipboard;
                settings.ClipboardShortcut = stored.ClipboardShortcut;
                settings.NoteSheetWidth = stored.NoteSheetWidth;
                settings.NoteSheetHeight = stored.NoteSheetHeight;
                settings.LocalChatWidth = stored.LocalChatWidth;
                settings.LocalChatHeight = stored.LocalChatHeight;
                settings.HandleWidth = stored.HandleWidth;
                settings.HandleReach = stored.HandleReach;
                settings.SearchApps = stored.SearchApps;
                settings.SearchFiles = stored.SearchFiles;
                settings.SearchProjects = stored.SearchProjects;
                settings.SearchSettings = stored.SearchSettings;
                settings.SearchCommands = stored.SearchCommands;
                settings.SearchCalculator = stored.SearchCalculator;
                settings.SearchWeb = stored.SearchWeb;
                settings.SearchMaxResults = stored.SearchMaxResults;
                settings.SearchExclusions = stored.SearchExclusions ?? AppSettings.DefaultSearchExclusions;
                settings.SearchScreen = stored.SearchScreen;
                settings.SearchKeepQuery = stored.SearchKeepQuery;
                settings.HideWhenIdle = stored.HideWhenIdle;
                settings.IdleSeconds = stored.IdleSeconds ?? stored.IdleMinutes * 60; // Saved in minutes before.
                settings.Language = stored.Language;
                settings.MascotColor = stored.MascotColor;
                settings.AssistantModel = stored.AssistantModel;
                settings.AssistantShortcut = stored.AssistantShortcut;
                settings.AssistantShortcutOn = stored.AssistantShortcutOn;
                settings.AssistantAnswered = stored.AssistantAnswered;
                settings.AssistantChatWidth = stored.AssistantChatWidth;
                settings.AssistantChatHeight = stored.AssistantChatHeight;
                settings.AssistantVoiceVolume = stored.AssistantVoiceVolume;
                settings.PomodoroFocusMinutes = stored.PomodoroFocusMinutes;
                settings.PomodoroShortBreakMinutes = stored.PomodoroShortBreakMinutes;
                settings.PomodoroLongBreakMinutes = stored.PomodoroLongBreakMinutes;
                settings.PomodoroSessions = stored.PomodoroSessions;
                settings.PomodoroAutoStartBreaks = stored.PomodoroAutoStartBreaks;
                settings.PomodoroAutoStartFocus = stored.PomodoroAutoStartFocus;
                settings.PomodoroSound = stored.PomodoroSound;
                settings.Minimized = stored.Minimized;
                settings.SearchShortcut = stored.SearchShortcut;
                settings.SearchShortcutOn = stored.SearchShortcutOn;
                settings.Welcomed = stored.Welcomed;
                settings.StudySounds = stored.StudySounds;
                settings.StudySoundVolume = stored.StudySoundVolume;
                settings.CourseWindowBounds = stored.CourseWindowBounds;
                settings.CourseWindowOnTop = stored.CourseWindowOnTop;
                settings.NoteWindowBounds = stored.NoteWindowBounds;
                settings.NoteWindowOnTop = stored.NoteWindowOnTop;
                settings.LyricsPlace = stored.LyricsPlace;
                settings.LyricsTaskbarWidth = stored.LyricsTaskbarWidth;
                settings.HidePlayerInNotch = stored.HidePlayerInNotch;
                settings.StudySection = stored.StudySection;
                settings.StayOpenAfterClick = stored.StayOpenAfterClick;
                settings.AutoUpdate = stored.AutoUpdate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or damaged file just means defaults.
            Debug.WriteLine($"[Settings] Using defaults: {ex.Message}");
        }

        return settings;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var stored = new StoredSettings(
                Settings.ShowLyrics, Settings.CompactLyricsFontSize, Settings.HideInFullscreen,
                Settings.RecordClipboard, Settings.SignalCopies, Settings.ClipboardLimit,
                Settings.UploadExpiry, Settings.UploadHost, Settings.ReplaceWindowsClipboard,
                Settings.ClipboardShortcut, Settings.NoteSheetWidth, Settings.NoteSheetHeight,
                Settings.LocalChatWidth, Settings.LocalChatHeight, Settings.HandleWidth, Settings.HandleReach,
                Settings.SearchApps, Settings.SearchFiles, Settings.SearchProjects, Settings.SearchSettings,
                Settings.SearchCommands, Settings.SearchCalculator, Settings.SearchWeb, Settings.SearchMaxResults,
                Settings.SearchExclusions, Settings.SearchScreen, Settings.SearchKeepQuery,
                Settings.HideWhenIdle, Settings.IdleSeconds / 60, Settings.Language, Settings.ShowMascot, Settings.MascotColor,
                Settings.IdleSeconds, Settings.AssistantModel, Settings.AssistantShortcut, Settings.AssistantShortcutOn,
                Settings.AssistantAnswered, Settings.AssistantChatWidth, Settings.AssistantChatHeight,
                Settings.AssistantVoiceVolume, Settings.PomodoroFocusMinutes, Settings.PomodoroShortBreakMinutes,
                Settings.PomodoroLongBreakMinutes, Settings.PomodoroSessions, Settings.PomodoroAutoStartBreaks,
                Settings.PomodoroAutoStartFocus, Settings.PomodoroSound, Settings.Minimized,
                Settings.SearchShortcut, Settings.SearchShortcutOn, Settings.Welcomed, Settings.StudySounds, Settings.StudySoundVolume,
                Settings.CourseWindowBounds, Settings.CourseWindowOnTop, Settings.NoteWindowBounds, Settings.NoteWindowOnTop,
                Settings.LyricsPlace, Settings.LyricsTaskbarWidth, Settings.StudySection, Settings.StayOpenAfterClick,
                Settings.HidePlayerInNotch, Settings.AutoUpdate);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Settings] Not saved: {ex.Message}");
        }
    }

    private sealed record StoredSettings(
        bool ShowLyrics = true,
        double CompactLyricsFontSize = 14,
        bool HideInFullscreen = true,
        bool RecordClipboard = true,
        bool SignalCopies = true,
        double ClipboardLimit = 25,
        string UploadExpiry = "24h",
        string UploadHost = "send",
        bool ReplaceWindowsClipboard = true,
        string ClipboardShortcut = "Win+V",
        double NoteSheetWidth = 0,
        double NoteSheetHeight = 0,
        double LocalChatWidth = 0,
        double LocalChatHeight = 0,
        double HandleWidth = AppSettings.DefaultHandleWidth,
        double HandleReach = AppSettings.DefaultHandleReach,
        bool SearchApps = true,
        bool SearchFiles = true,
        bool SearchProjects = true,
        bool SearchSettings = true,
        bool SearchCommands = true,
        bool SearchCalculator = true,
        bool SearchWeb = true,
        double SearchMaxResults = 8,
        string? SearchExclusions = AppSettings.DefaultSearchExclusions,
        string SearchScreen = "pointer",
        bool SearchKeepQuery = true,
        bool HideWhenIdle = true,
        double IdleMinutes = 3,
        string Language = "auto",
        bool ShowMascot = true,
        string MascotColor = Mascot.MascotPalette.DefaultId,
        double? IdleSeconds = null,
        string AssistantModel = "",
        string AssistantShortcut = AppSettings.DefaultAssistantShortcut,
        bool AssistantShortcutOn = true,
        string AssistantAnswered = "",
        double AssistantChatWidth = 0,
        double AssistantChatHeight = 0,
        double AssistantVoiceVolume = 100,
        double PomodoroFocusMinutes = 25,
        double PomodoroShortBreakMinutes = 5,
        double PomodoroLongBreakMinutes = 15,
        double PomodoroSessions = 4,
        bool PomodoroAutoStartBreaks = true,
        bool PomodoroAutoStartFocus = false,
        bool PomodoroSound = true,
        bool Minimized = false,
        string SearchShortcut = AppSettings.DefaultSearchShortcut,
        bool SearchShortcutOn = true,
        bool Welcomed = true, // Settings saved before the welcome existed: that notch is set up already.
        bool StudySounds = true,
        double StudySoundVolume = 60,
        string CourseWindowBounds = "",
        bool CourseWindowOnTop = true,
        string NoteWindowBounds = "",
        bool NoteWindowOnTop = true,
        string LyricsPlace = AppSettings.LyricsInNotch,
        double LyricsTaskbarWidth = 300,
        long StudySection = 0,
        bool StayOpenAfterClick = true,
        bool HidePlayerInNotch = false,
        bool AutoUpdate = true);
}
