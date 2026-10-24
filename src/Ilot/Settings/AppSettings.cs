using CommunityToolkit.Mvvm.ComponentModel;

namespace Ilot.Settings;

/// <summary>User preferences, applied live: every change is observed by the views that use it.</summary>
public sealed class AppSettings : ObservableObject
{
    public const double MinCompactLyricsFontSize = 11;
    public const double MaxCompactLyricsFontSize = 18;
    public const double MinLyricsTaskbarWidth = 120;
    public const double MaxLyricsTaskbarWidth = 640;
    public const double MinClipboardLimit = 5;
    public const double MaxClipboardLimit = 100;
    public const double MinHandleWidth = 40;
    public const double MaxHandleWidth = 200;
    public const double MinHandleReach = 0;
    public const double MaxHandleReach = 40;
    public const double DefaultHandleWidth = 76;
    public const double DefaultHandleReach = 6;

    private bool _showLyrics = true;
    private double _compactLyricsFontSize = 14;
    private string _lyricsPlace = LyricsInNotch;
    private double _lyricsTaskbarWidth = 300;
    private bool _hidePlayerInNotch;

    /// <summary>
    /// While the taskbar shows the track (its sung line, its artwork: <see cref="LyricsInTaskbar"/>), the closed notch leaves
    /// the player out (artwork, progress, visualizer), as if nothing played; it comes back when the taskbar shows nothing.
    /// </summary>
    public bool HidePlayerInNotch
    {
        get => _hidePlayerInNotch;
        set => SetProperty(ref _hidePlayerInNotch, value);
    }

    /// <summary>The sung line on the closed notch (see <see cref="LyricsPlace"/>).</summary>
    public const string LyricsInNotch = "notch";

    /// <summary>The sung line on the Windows taskbar, in an empty stretch of it (see <see cref="LyricsPlace"/>).</summary>
    public const string LyricsInTaskbar = "taskbar";
    private bool _hideInFullscreen = true;
    private bool _recordClipboard = true;
    private bool _signalCopies = true;
    private bool _replaceWindowsClipboard = true;
    private string _clipboardShortcut = "Win+V";
    private double _clipboardLimit = 25;
    private string _uploadExpiry = "24h";
    private string _uploadHost = "send";
    private double _noteSheetWidth;
    private double _noteSheetHeight;
    private double _localChatWidth;
    private double _localChatHeight;
    public const double MinSearchResults = 4;
    public const double MaxSearchResults = 12;

    /// <summary>Folders the search leaves out (one per line, as Everything reads them: "\node_modules\" anywhere).</summary>
    /// <remarks>Apps' own data (caches, logs) would bury what one looks for: AppData is left out whole.</remarks>
    public const string DefaultSearchExclusions =
        "C:\\Windows\\\n\\$Recycle.Bin\\\n\\AppData\\\n\\WindowsApps\\\n\\ProgramData\\Microsoft\\\n\\node_modules\\\n\\.git\\\n"
        + "\\.gradle\\\n\\.vscode\\\n\\.vs\\\n\\obj\\";

    private bool _searchApps = true;
    private bool _searchFiles = true;
    private bool _searchProjects = true;
    private bool _searchSettings = true;
    private bool _searchCommands = true;
    private bool _searchCalculator = true;
    private bool _searchWeb = true;
    private double _searchMaxResults = 8;
    private string _searchExclusions = DefaultSearchExclusions;
    private string _language = "auto";

    /// <summary>
    /// The notch's language: "auto" (French when Windows is, else English), "fr" or "en". Read at startup: a change shows
    /// at the next one.
    /// </summary>
    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, value is "auto" or "fr" or "en" ? value : "auto");
    }

    public const double MinIdleSeconds = 5;
    public const double MaxIdleSeconds = 300;
    public const double IdleSecondsStep = 5;

    private bool _hideWhenIdle = true;
    private string _assistantModel = "";
    private string _assistantShortcut = DefaultAssistantShortcut;
    private string _searchShortcut = DefaultSearchShortcut;
    private bool _searchShortcutOn = true;

    /// <summary>The search's shortcut, by default.</summary>
    public const string DefaultSearchShortcut = "Ctrl+Q";

    /// <summary>The shortcut that opens the search from anywhere (Ctrl + Q by default).</summary>
    public string SearchShortcut
    {
        get => _searchShortcut;
        set => SetProperty(ref _searchShortcut, value);
    }

    public bool SearchShortcutOn
    {
        get => _searchShortcutOn;
        set => SetProperty(ref _searchShortcutOn, value);
    }
    private bool _assistantShortcutOn = true;

    /// <summary>The assistant's shortcut, unless one is chosen in the settings.</summary>
    public const string DefaultAssistantShortcut = "Ctrl+Shift+0x20";

    /// <summary>The Gemini model the assistant uses ("gemini-…"); empty: the quickest everyday one the key may use.</summary>
    public string AssistantModel
    {
        get => _assistantModel;
        set => SetProperty(ref _assistantModel, value ?? "");
    }

    /// <summary>
    /// On « Automatique », the model that answered last (not a choice: remembered so the next launch asks it first,
    /// rather than trying the models one by one again).
    /// </summary>
    public string AssistantAnswered
    {
        get => _assistantAnswered;
        set => SetProperty(ref _assistantAnswered, value ?? "");
    }

    private string _assistantAnswered = "";
    private double _assistantChatWidth;
    private double _assistantVoiceVolume = 100;

    // ── Pomodoro ──
    private double _pomodoroFocusMinutes = 25;
    private double _pomodoroShortBreakMinutes = 5;
    private double _pomodoroLongBreakMinutes = 15;
    private double _pomodoroSessions = 4;
    private bool _pomodoroAutoStartBreaks = true;
    private bool _pomodoroAutoStartFocus;
    private bool _pomodoroSound = true;
    private bool _minimized;
    private bool _welcomed;
    private bool _studySounds = true;
    private long _studySection;

    /// <summary>The section of the courses chosen in Révisions (see Modules/Study/StudySection); 0: « Global », all of them.</summary>
    public long StudySection
    {
        get => _studySection;
        set => SetProperty(ref _studySection, Math.Max(0, value));
    }
    private double _studySoundVolume = 60;
    private string _courseWindowBounds = "";
    private bool _courseWindowOnTop = true;
    private string _noteWindowBounds = "";
    private bool _noteWindowOnTop = true;

    /// <summary>
    /// Where a course's sheet detached from the notch was last (see DetachedWindow): « x,y,width,height » in the screen's
    /// pixels; empty: never moved, it opens where the bubble was.
    /// </summary>
    public string CourseWindowBounds
    {
        get => _courseWindowBounds;
        set => SetProperty(ref _courseWindowBounds, value);
    }

    /// <summary>The detached sheet stays above the other windows (its pin).</summary>
    public bool CourseWindowOnTop
    {
        get => _courseWindowOnTop;
        set => SetProperty(ref _courseWindowOnTop, value);
    }

    /// <summary>Where a note detached from the notch was last (as <see cref="CourseWindowBounds"/>).</summary>
    public string NoteWindowBounds
    {
        get => _noteWindowBounds;
        set => SetProperty(ref _noteWindowBounds, value);
    }

    /// <summary>The detached note stays above the other windows (its pin).</summary>
    public bool NoteWindowOnTop
    {
        get => _noteWindowOnTop;
        set => SetProperty(ref _noteWindowOnTop, value);
    }

    /// <summary>How loud the revisions' sounds are, in percent (they are soft at 100 already).</summary>
    public double StudySoundVolume
    {
        get => _studySoundVolume;
        set => SetProperty(ref _studySoundVolume, Math.Clamp(Math.Round(value), 0, 100));
    }

    /// <summary>The revisions' little sounds (a right answer, a wrong one, a session done, a course ready).</summary>
    public bool StudySounds
    {
        get => _studySounds;
        set => SetProperty(ref _studySounds, value);
    }

    /// <summary>
    /// The first launch's welcome is behind (its steps done or skipped): it shows once, after the installation. A notch
    /// that already had its settings never sees it (see SettingsStore); the settings can show it again.
    /// </summary>
    public bool Welcomed
    {
        get => _welcomed;
        set => SetProperty(ref _welcomed, value);
    }

    /// <summary>
    /// Minimized: closed, the notch is only its handle, whatever goes on (music, a live activity, the mascot's nest).
    /// Hovering it opens it as ever; announcements, cards and questions still come out for their time.
    /// </summary>
    public bool Minimized
    {
        get => _minimized;
        set => SetProperty(ref _minimized, value);
    }

    /// <summary>A focus's length, in minutes (5 to 90).</summary>
    public double PomodoroFocusMinutes
    {
        get => _pomodoroFocusMinutes;
        set => SetProperty(ref _pomodoroFocusMinutes, Math.Clamp(Math.Round(value), 5, 90));
    }

    /// <summary>A short break's length, in minutes (1 to 30).</summary>
    public double PomodoroShortBreakMinutes
    {
        get => _pomodoroShortBreakMinutes;
        set => SetProperty(ref _pomodoroShortBreakMinutes, Math.Clamp(Math.Round(value), 1, 30));
    }

    /// <summary>A long break's length, in minutes (5 to 60).</summary>
    public double PomodoroLongBreakMinutes
    {
        get => _pomodoroLongBreakMinutes;
        set => SetProperty(ref _pomodoroLongBreakMinutes, Math.Clamp(Math.Round(value), 5, 60));
    }

    /// <summary>How many focuses before a long break (2 to 8).</summary>
    public double PomodoroSessions
    {
        get => _pomodoroSessions;
        set => SetProperty(ref _pomodoroSessions, Math.Clamp(Math.Round(value), 2, 8));
    }

    /// <summary>A break starts on its own when a focus ends.</summary>
    public bool PomodoroAutoStartBreaks
    {
        get => _pomodoroAutoStartBreaks;
        set => SetProperty(ref _pomodoroAutoStartBreaks, value);
    }

    /// <summary>A focus starts on its own when a break ends.</summary>
    public bool PomodoroAutoStartFocus
    {
        get => _pomodoroAutoStartFocus;
        set => SetProperty(ref _pomodoroAutoStartFocus, value);
    }

    /// <summary>A chime at the end of each phase.</summary>
    public bool PomodoroSound
    {
        get => _pomodoroSound;
        set => SetProperty(ref _pomodoroSound, value);
    }

    /// <summary>How loud the assistant's voice is, talking aloud with it (percent of its own level).</summary>
    public double AssistantVoiceVolume
    {
        get => _assistantVoiceVolume;
        set => SetProperty(ref _assistantVoiceVolume, Math.Clamp(Math.Round(value), 0, 100));
    }
    private double _assistantChatHeight;

    /// <summary>The assistant's drop, as the user resized it; 0: never resized, it fits the conversation.</summary>
    public double AssistantChatWidth
    {
        get => _assistantChatWidth;
        set => SetProperty(ref _assistantChatWidth, Math.Max(0, value));
    }

    /// <inheritdoc cref="AssistantChatWidth"/>
    public double AssistantChatHeight
    {
        get => _assistantChatHeight;
        set => SetProperty(ref _assistantChatHeight, Math.Max(0, value));
    }

    /// <summary>The shortcut that opens the assistant from anywhere (Ctrl + Shift + Space by default).</summary>
    public string AssistantShortcut
    {
        get => _assistantShortcut;
        set => SetProperty(ref _assistantShortcut, value);
    }

    public bool AssistantShortcutOn
    {
        get => _assistantShortcutOn;
        set => SetProperty(ref _assistantShortcutOn, value);
    }
    private string _mascotColor = Mascot.MascotPalette.DefaultId;
    private double _idleSeconds = 180;

    private bool _stayOpenAfterClick = true;

    /// <summary>
    /// Opened by hovering, the notch closes as the pointer leaves; once clicked in, it stays open until a click outside
    /// (see NotchController.Engage).
    /// </summary>
    public bool StayOpenAfterClick
    {
        get => _stayOpenAfterClick;
        set => SetProperty(ref _stayOpenAfterClick, value);
    }

    /// <summary>
    /// Nothing going on (no music, no upload…): the closed notch shrinks to its handle after <see cref="IdleSeconds"/>,
    /// and at startup. Hovering the handle opens it; announcements still bring the pill out.
    /// </summary>
    public bool HideWhenIdle
    {
        get => _hideWhenIdle;
        set => SetProperty(ref _hideWhenIdle, value);
    }

    /// <summary>How long with nothing going on before the notch withdraws, in seconds (by steps of five).</summary>
    public double IdleSeconds
    {
        get => _idleSeconds;
        set => SetProperty(
            ref _idleSeconds,
            Math.Clamp(Math.Round(value / IdleSecondsStep) * IdleSecondsStep, MinIdleSeconds, MaxIdleSeconds));
    }

    private string _searchScreen = "pointer";
    private bool _searchKeepQuery = true;

    /// <summary>
    /// Where the search opens: "pointer" (the screen the mouse is on, the default), "active" (the screen of the window
    /// in use) or "home" (the primary screen, the notch's). The notch goes back home after.
    /// </summary>
    public string SearchScreen
    {
        get => _searchScreen;
        set => SetProperty(ref _searchScreen, value is "pointer" or "active" or "home" ? value : "pointer");
    }

    /// <summary>The search opens with the last one typed, selected (typing replaces it); off: empty each time.</summary>
    public bool SearchKeepQuery
    {
        get => _searchKeepQuery;
        set => SetProperty(ref _searchKeepQuery, value);
    }

    /// <summary>The search (Ctrl + Q) lists the installed apps.</summary>
    public bool SearchApps
    {
        get => _searchApps;
        set => SetProperty(ref _searchApps, value);
    }

    /// <summary>The search lists files and folders, found by Everything.</summary>
    public bool SearchFiles
    {
        get => _searchFiles;
        set => SetProperty(ref _searchFiles, value);
    }

    /// <summary>The search lists VS Code's recent projects.</summary>
    public bool SearchProjects
    {
        get => _searchProjects;
        set => SetProperty(ref _searchProjects, value);
    }

    /// <summary>The search lists Windows' settings pages.</summary>
    public bool SearchSettings
    {
        get => _searchSettings;
        set => SetProperty(ref _searchSettings, value);
    }

    /// <summary>The search offers the system's commands (lock, sleep, restart…).</summary>
    public bool SearchCommands
    {
        get => _searchCommands;
        set => SetProperty(ref _searchCommands, value);
    }

    /// <summary>The search works out calculations ("18*1,2").</summary>
    public bool SearchCalculator
    {
        get => _searchCalculator;
        set => SetProperty(ref _searchCalculator, value);
    }

    /// <summary>The search ends with a web search for what was typed.</summary>
    public bool SearchWeb
    {
        get => _searchWeb;
        set => SetProperty(ref _searchWeb, value);
    }

    /// <summary>How many results the search shows at most.</summary>
    public double SearchMaxResults
    {
        get => _searchMaxResults;
        set => SetProperty(ref _searchMaxResults, Math.Clamp(Math.Round(value), MinSearchResults, MaxSearchResults));
    }

    /// <summary>Folders the search leaves out, one per line (see <see cref="DefaultSearchExclusions"/>).</summary>
    public string SearchExclusions
    {
        get => _searchExclusions;
        set => SetProperty(ref _searchExclusions, value ?? "");
    }

    private double _handleWidth = DefaultHandleWidth;
    private double _handleReach = DefaultHandleReach;

    /// <summary>
    /// Width of the handle the closed notch goes back into under the pointer (the only spot that opens the notch), in
    /// pixels.
    /// </summary>
    public double HandleWidth
    {
        get => _handleWidth;
        set => SetProperty(ref _handleWidth, Math.Clamp(Math.Round(value / 2) * 2, MinHandleWidth, MaxHandleWidth));
    }

    /// <summary>
    /// How far around the handle the pointer already counts as on it, in pixels: on each side, and half that below (up
    /// to the screen edge above).
    /// </summary>
    public double HandleReach
    {
        get => _handleReach;
        set => SetProperty(ref _handleReach, Math.Clamp(Math.Round(value), MinHandleReach, MaxHandleReach));
    }

    /// <summary>Keep a history of what is copied (clipboard tab). Off: nothing new is recorded, the history stays.</summary>
    public bool RecordClipboard
    {
        get => _recordClipboard;
        set => SetProperty(ref _recordClipboard, value);
    }

    /// <summary>
    /// How many copies the history keeps, pinned ones not counted: past it, each new copy pushes the oldest out.
    /// </summary>
    public double ClipboardLimit
    {
        get => _clipboardLimit;
        set => SetProperty(ref _clipboardLimit, Math.Clamp(Math.Round(value), MinClipboardLimit, MaxClipboardLimit));
    }

    /// <summary>The shortcut opens the notch's clipboard (instead of Windows' own, with the default Windows + V).</summary>
    public bool ReplaceWindowsClipboard
    {
        get => _replaceWindowsClipboard;
        set => SetProperty(ref _replaceWindowsClipboard, value);
    }

    /// <summary>The clipboard shortcut, as text: "Win+V" (the default), "Ctrl+Shift+V"…</summary>
    public string ClipboardShortcut
    {
        get => _clipboardShortcut;
        set => SetProperty(ref _clipboardShortcut, string.IsNullOrWhiteSpace(value) ? "Win+V" : value);
    }

    /// <summary>The closed notch briefly announces each copy ("Texte copié"…).</summary>
    public bool SignalCopies
    {
        get => _signalCopies;
        set => SetProperty(ref _signalCopies, value);
    }

    /// <summary>How long dropped files are kept, when the host lets us choose (Send): "1h", "12h", "24h" or "72h".</summary>
    public string UploadExpiry
    {
        get => _uploadExpiry;
        set => SetProperty(ref _uploadExpiry, UploadExpiries.Contains(value) ? value : "24h");
    }

    /// <summary>Where dropped files go: "send" (encrypted end to end, 2.5 GB; the default) or "gofile" (anonymous, 5 GB).</summary>
    public string UploadHost
    {
        get => _uploadHost;
        set => SetProperty(ref _uploadHost, string.IsNullOrEmpty(value) ? "send" : value);
    }

    /// <summary>The durations offered.</summary>
    public static IReadOnlyList<string> UploadExpiries { get; } = ["1h", "12h", "24h", "72h"];

    /// <summary>The note sheet's size, as last set by pulling its corners; 0: as large as the screen allows.</summary>
    public double NoteSheetWidth
    {
        get => _noteSheetWidth;
        set => SetProperty(ref _noteSheetWidth, Math.Max(0, Math.Round(value)));
    }

    /// <inheritdoc cref="NoteSheetWidth"/>
    public double NoteSheetHeight
    {
        get => _noteSheetHeight;
        set => SetProperty(ref _noteSheetHeight, Math.Max(0, Math.Round(value)));
    }

    /// <summary>The local Wi-Fi chat bubble's size, as last set by pulling its corners; 0: the default size.</summary>
    public double LocalChatWidth
    {
        get => _localChatWidth;
        set => SetProperty(ref _localChatWidth, Math.Max(0, Math.Round(value)));
    }

    /// <inheritdoc cref="LocalChatWidth"/>
    public double LocalChatHeight
    {
        get => _localChatHeight;
        set => SetProperty(ref _localChatHeight, Math.Max(0, Math.Round(value)));
    }

    /// <summary>Put the notch away while an app is full screen on its display (game, video, presentation…).</summary>
    public bool HideInFullscreen
    {
        get => _hideInFullscreen;
        set => SetProperty(ref _hideInFullscreen, value);
    }

    /// <summary>
    /// The mascot, where the artwork goes when nothing plays (see <c>Mascot/</c>): always there, it is the notch's face
    /// (it could be put away before; a setting saved off is no longer read).
    /// </summary>
    public bool ShowMascot => true;

    /// <summary>The mascot's colour: the id of one of <see cref="Mascot.MascotPalette.Colors"/>.</summary>
    public string MascotColor
    {
        get => _mascotColor;
        set => SetProperty(ref _mascotColor, Mascot.MascotPalette.Find(value).Id);
    }

    /// <summary>Show synced lyrics when the track has some (right column, and the sung line in the closed pill).</summary>
    public bool ShowLyrics
    {
        get => _showLyrics;
        set => SetProperty(ref _showLyrics, value);
    }

    /// <summary>
    /// Where the sung line shows while the notch is closed: on it (<see cref="LyricsInNotch"/>), or on the Windows taskbar
    /// (<see cref="LyricsInTaskbar"/>). The open notch shows the lyrics either way.
    /// </summary>
    public string LyricsPlace
    {
        get => _lyricsPlace;
        set => SetProperty(ref _lyricsPlace, value == LyricsInTaskbar ? LyricsInTaskbar : LyricsInNotch);
    }

    /// <summary>
    /// How wide the sung line's lines may go on the taskbar, leftwards from the artwork (against the icons), in logical
    /// pixels: set by pulling its left edge, or in the settings. Less when the taskbar has less room.
    /// </summary>
    public double LyricsTaskbarWidth
    {
        get => _lyricsTaskbarWidth;
        set => SetProperty(ref _lyricsTaskbarWidth, double.IsNaN(value) ? 300 : Math.Clamp(Math.Round(value), MinLyricsTaskbarWidth, MaxLyricsTaskbarWidth));
    }

    /// <summary>Text size of the sung line in the closed pill (or on the taskbar), in pixels.</summary>
    public double CompactLyricsFontSize
    {
        get => _compactLyricsFontSize;
        set => SetProperty(ref _compactLyricsFontSize, Math.Clamp(Math.Round(value), MinCompactLyricsFontSize, MaxCompactLyricsFontSize));
    }
}
