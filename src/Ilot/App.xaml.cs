using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Ilot.Localization;
using Ilot.Modules.Clipboard;
using Ilot.Modules.Code;
using Ilot.Modules.DropZone;
using Ilot.Modules.DropZone.Local;
using Ilot.Modules.Media;
using Ilot.Modules.Media.Audio;
using Ilot.Modules.Media.Lyrics;
using Ilot.Modules.Notes;
using Ilot.Modules.Search;
using Ilot.Modules.Search.Everything;
using Ilot.Modules.Assistant;
using Ilot.Notch;
using Ilot.Quick;
using Ilot.Settings;
using Ilot.Welcome;
using Ilot.Windowing;

namespace Ilot;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\Ilot.SingleInstance";

    private Mutex? _singleInstance;
    private ServiceProvider? _services;
    private SettingsWindow? _settingsWindow;
    private WelcomeWindow? _welcome;

    // The welcome closed, the tour starts a moment later: the notch has the screen to itself by then.
    private static readonly TimeSpan TourDelay = TimeSpan.FromMilliseconds(450);

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One notch is plenty: a second launch simply exits. For development, ILOT_INSTANCE=<name> runs a copy beside
        // the one in use (a quick check that a build starts).
        string instance = Environment.GetEnvironmentVariable("ILOT_INSTANCE") is { Length: > 0 } name
            ? $"{SingleInstanceMutexName}.{name}"
            : SingleInstanceMutexName;
        _singleInstance = new Mutex(initiallyOwned: true, instance, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            Exit();
            return;
        }

        StartupLaunch.Migrate(); // Renamed from MyNotch: starting with Windows goes on, under the new name.

        try
        {
            _services = ConfigureServices();
            Loc.Initialize(_services.GetRequiredService<AppSettings>().Language); // Before any window is made.
            var window = _services.GetRequiredService<NotchWindow>();
            window.Closed += (_, _) => Quit();
            window.SettingsRequested += (_, _) => ShowSettings();
            window.ShowNotch();

            // The first launch after the installation: a few steps to set it up, then its tour.
            if (!_services.GetRequiredService<AppSettings>().Welcomed)
            {
                ShowWelcome();
            }

            _services.GetRequiredService<SearchRouter>();

            // The file search's engine starts now, so its index is ready for the first Ctrl + Q.
            if (_services.GetRequiredService<AppSettings>().SearchFiles)
            {
                _services.GetRequiredService<EverythingEngine>().Start();
            }
        }
        catch (Exception ex)
        {
            // No notch: better gone (and said why) than a process holding the single instance with nothing on screen.
            Log(ex);
            Shutdown();
            Exit();
            return;
        }

        await _services.GetRequiredService<IMediaSessionService>().StartAsync();
    }

    /// <summary>
    /// Composition root. A new notch module is a service implementing <see cref="INotchModule"/>
    /// plus whatever services it needs.
    /// </summary>
    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddSingleton(DispatcherQueue.GetForCurrentThread());
        services.AddSingleton(NotchLayout.Default);

        // Settings, saved as they change
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(provider => provider.GetRequiredService<SettingsStore>().Settings);
        services.AddTransient<SettingsWindow>();
        services.AddTransient<WelcomeWindow>();

        // Media module (SMTC: Spotify, YouTube, VLC, Media Player…) and its live audio visualizer
        services.AddSingleton<IMediaSessionService, MediaSessionService>();
        services.AddSingleton<IAudioSpectrumService, AudioSpectrumService>();
        services.AddSingleton<IAudioService, AudioService>();
        services.AddSingleton<AudioDeviceMonitor>();
        services.AddSingleton<VolumeWatcher>();
        services.AddSingleton<BrightnessService>();
        services.AddSingleton<VolumeMixer>();
        services.AddSingleton<QuickServices>();
        services.AddSingleton<ILyricsService, LrcLibLyricsService>();
        services.AddSingleton<LyricsViewModel>();
        services.AddSingleton<MediaViewModel>();
        services.AddSingleton<INotchModule, MediaModule>();

        // Clipboard module: history of copies, pins, saved locally. Modules get their tab in this order.
        services.AddSingleton<ClipboardStore>();
        services.AddSingleton<ClipboardHistory>();
        services.AddSingleton<ClipboardViewModel>();
        services.AddSingleton<ClipboardShortcut>();
        services.AddSingleton<INotchModule, ClipboardModule>();

        // Drop Zone: files dropped on the notch go to Send (encrypted) or Gofile, their link to the clipboard.
        services.AddSingleton<UploadQueueStore>();
        services.AddSingleton<FileUploadService>();
        services.AddSingleton<LocalTransferService>();
        services.AddSingleton<LocalShareViewModel>();
        services.AddSingleton<DropZoneViewModel>();
        services.AddSingleton<INotchModule, DropZoneModule>();

        // Code: VS Code's recent projects (no tab: « code » in the search shows them, see CodeShelf).
        services.AddSingleton<CodeService>();
        services.AddSingleton<CodeViewModel>();

        // Notes: Markdown notes in a notch grown tall for them, kept in a local SQLite database.
        services.AddSingleton<NotesService>();
        services.AddSingleton<NotesViewModel>();
        services.AddSingleton<INotchModule, NotesModule>();

        // Révisions: a course made into a summary sheet and flashcards by Gemini (the assistant's key), revised in time.
        services.AddSingleton<Ilot.Modules.Study.StudyStore>();
        services.AddSingleton<Ilot.Modules.Study.StudyGenerator>();
        services.AddSingleton<Ilot.Modules.Study.StudyViewModel>();
        services.AddSingleton<INotchModule, Ilot.Modules.Study.StudyModule>();

        // Pomodoro: focus and breaks, the timer at the top of the closed notch, the mascot reading meanwhile.
        services.AddSingleton(provider => new Ilot.Modules.Pomodoro.PomodoroTimer());
        services.AddSingleton(provider => new Ilot.Modules.Pomodoro.PomodoroStats());
        services.AddSingleton<INotchModule, Ilot.Modules.Pomodoro.PomodoroModule>();

        // Search: Ctrl + Q, apps, files (Everything, run out of sight), projects, settings, commands, calculations. No tab.
        services.AddSingleton<SearchHistory>();
        services.AddSingleton<AppCatalog>();
        services.AddSingleton<ShellIcons>();
        services.AddSingleton<EverythingEngine>();
        services.AddSingleton<SearchShortcut>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton(provider => new SearchModule(provider.GetRequiredService<SearchViewModel>()));
        services.AddSingleton<INotchModule>(provider => provider.GetRequiredService<SearchModule>());
        services.AddSingleton<SearchRouter>(); // Ctrl + Q: in the notch, or a search notch of its own on another screen.

        // Assistant: the mascot, clicked (or Ctrl + Shift + Space), answers through Gemini (the key in the settings). No tab.
        services.AddSingleton<ApiKeyStore>();
        services.AddSingleton<GeminiClient>();
        services.AddSingleton(provider => new AssistantHistory(provider.GetRequiredService<DispatcherQueue>()));
        services.AddSingleton<AssistantConversation>();
        services.AddSingleton<Ilot.Modules.Assistant.Voice.AssistantVoice>();
        services.AddSingleton<AssistantShortcut>();
        services.AddSingleton<AssistantModule>();
        services.AddSingleton<INotchModule>(provider => provider.GetRequiredService<AssistantModule>());

        // Shell
        services.AddSingleton<NotchView>();
        services.AddSingleton<NotchWindow>();

        return services.BuildServiceProvider();
    }

    /// <summary>One settings window at a time: asking again brings it to the front (on <paramref name="section"/>, if given).</summary>
    private void ShowSettings(string? section = null)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = _services!.GetRequiredService<SettingsWindow>();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.WelcomeRequested += (_, _) => ShowWelcome();
            _settingsWindow.TourRequested += (_, _) => StartTour();
        }

        if (section is not null)
        {
            _settingsWindow.ShowSection(section);
        }

        _settingsWindow.Activate();
    }

    /// <summary>
    /// The welcome's steps (see <see cref="WelcomeWindow"/>), from <paramref name="step"/>: one window at a time. Done or
    /// skipped, it is not shown again by itself; its tour follows if asked for.
    /// </summary>
    private void ShowWelcome(int step = 0)
    {
        if (_welcome is not null)
        {
            _welcome.Activate();
            return;
        }

        var welcome = _services!.GetRequiredService<WelcomeWindow>();
        _welcome = welcome;
        welcome.Closed += (_, _) =>
        {
            if (_welcome == welcome)
            {
                _welcome = null;
            }
        };
        welcome.Finished += (_, tour) =>
        {
            _services!.GetRequiredService<AppSettings>().Welcomed = true;
            if (tour)
            {
                var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                timer.Interval = TourDelay;
                timer.IsRepeating = false;
                timer.Tick += (_, _) => StartTour();
                timer.Start();
            }
        };
        welcome.LanguageChanged += (_, at) =>
        {
            _welcome = null;
            ShowWelcome(at); // Made again in the language chosen, where it was.
        };
        welcome.Go(step);
        welcome.Activate();
    }

    /// <summary>The tour of the notch, its steps as the notch is set up now (its shortcuts, the assistant's key).</summary>
    private void StartTour()
    {
        var services = _services!;
        var steps = TourSteps.Build(
            services.GetRequiredService<AppSettings>(),
            services.GetRequiredService<SearchShortcut>(),
            services.GetRequiredService<ClipboardShortcut>(),
            services.GetRequiredService<AssistantShortcut>(),
            services.GetRequiredService<ApiKeyStore>(),
            openSearch: () => services.GetRequiredService<SearchRouter>().OpenInNotch(),
            tryAssistant: () => services.GetRequiredService<AssistantModule>().OpenFromMascot(),
            addKey: () => ShowSettings("Assistant"));
        services.GetRequiredService<NotchWindow>().StartTour(steps);
    }

    private void Shutdown()
    {
        _services?.Dispose();
        _singleInstance?.Dispose();
    }

    /// <summary>
    /// The notch's window closed (« Quitter »): everything saved and let go, then the process ends, even with another of
    /// its windows still there, hidden (the taskbar's lyrics, a detached sheet): it would keep it running unseen, holding
    /// the single instance and its files.
    /// </summary>
    private void Quit()
    {
        Shutdown();
        Environment.Exit(0);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // A glitch in a media session must never take the notch down with it.
        Log(e.Exception);
        e.Handled = true;
    }

    private static void Log(Exception exception) => ErrorLog.Write($"[App] Unhandled exception: {exception}");
}
