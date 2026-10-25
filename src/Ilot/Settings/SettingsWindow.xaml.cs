using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Ilot.Interop;
using Ilot.Modules.Clipboard;
using Ilot.Modules.DropZone.Hosts;
using Ilot.Modules.Search;
using Ilot.Modules.Search.Everything;
using Windows.Graphics;
using Ilot.Localization;
using Ilot.Mascot;
using Ilot.Modules.Assistant;

namespace Ilot.Settings;

/// <summary>
/// Settings window: Windows 11 style (Mica, custom title bar, cards), applied live; by section, chosen in a pane on the
/// left (General, the handle, the search…), one shown at a time.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const int LogicalWidth = 900;
    private const int LogicalHeight = 780;

    /// <summary>Where a Gemini API key is made (Google AI Studio).</summary>
    private static readonly Uri AiStudioKeys = new("https://aistudio.google.com/apikey");

    // The search's screens, and the languages, in the order the settings offer them.
    private static readonly string[] SearchScreens = ["pointer", "active", "home"];
    private static readonly string[] Languages = ["auto", "fr", "en"];

    private readonly ClipboardHistory _clipboard;
    private readonly ClipboardShortcut _shortcut;
    private readonly EverythingEngine _everything;
    private readonly ApiKeyStore _keys;
    private readonly GeminiClient _gemini;
    private readonly AssistantShortcut _assistantShortcut;
    private readonly SearchShortcut _searchShortcut;
    private bool _recording;
    private bool _recordingAssistant;
    private bool _recordingSearch;
    private bool _fillingModels;

    public SettingsWindow(
        AppSettings settings, ClipboardHistory clipboard, ClipboardShortcut shortcut, EverythingEngine everything,
        ApiKeyStore keys, GeminiClient gemini, AssistantShortcut assistantShortcut, SearchShortcut searchShortcut,
        Ilot.Updates.AppUpdater updater)
    {
        Settings = settings;
        _clipboard = clipboard;
        _shortcut = shortcut;
        _everything = everything;
        _keys = keys;
        _gemini = gemini;
        _assistantShortcut = assistantShortcut;
        _searchShortcut = searchShortcut;
        Updater = updater;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        Sections.SelectedItem = Sections.MenuItems[0]; // General first.
        StartupToggle.IsOn = StartupLaunch.IsEnabled; // As Windows has it (the handler then finds nothing to write).
        ShowEverything();
        everything.StatusChanged += OnEverythingChanged;
        Closed += (_, _) => everything.StatusChanged -= OnEverythingChanged;
        Title = Loc.T("Paramètres d'Ilot");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Ilot.ico")); // The taskbar's, Alt + Tab's.
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        CenterOnPrimaryDisplay();

        ShowShortcut(_shortcut.Gesture);
        BuildMascotColors();
        ShowAssistantShortcut();
        ShowSearchShortcut();
        ShowKeyState();
        _ = FillModelsAsync(null);
        Settings.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) =>
        {
            Settings.PropertyChanged -= OnSettingsChanged;
            _shortcut.CancelRecording();
            _assistantShortcut.CancelRecording();
            _searchShortcut.CancelRecording();
        };
    }

    // ── Shortcut ────────────────────────────────────────────────────────────────────────────────────────────

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ClipboardShortcut) && !_recording)
        {
            ShowShortcut(_shortcut.Gesture);
        }
        else if (e.PropertyName == nameof(AppSettings.MascotColor))
        {
            ShowMascotColor();
        }
        else if (e.PropertyName == nameof(AppSettings.AssistantShortcut) && !_recordingAssistant)
        {
            ShowAssistantShortcut();
        }
        else if (e.PropertyName == nameof(AppSettings.SearchShortcut) && !_recordingSearch)
        {
            ShowSearchShortcut();
        }
    }

    // ── Assistant ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A key is saved, or not: the field says so (the key itself is never shown again).</summary>
    private void ShowKeyState()
    {
        ApiKeyBox.PlaceholderText = _keys.HasKey
            ? Loc.T("Clé enregistrée : collez-en une autre pour la remplacer")
            : Loc.T("Collez votre clé API");
        ForgetKeyButton.IsEnabled = _keys.HasKey;
        KeyGuide.IsExpanded = !_keys.HasKey; // The steps to get one, while there is none.
    }

    /// <summary>A length in minutes, as the Pomodoro's sliders show it (« 25 min »).</summary>
    public string MinutesText(double minutes) => Loc.F("{0} min", minutes);

    /// <summary>A number of sessions, as the Pomodoro's slider shows it.</summary>
    public string CountText(double count) => count.ToString("0", Loc.Culture);

    /// <summary>The first launch's welcome, asked for again (General, « Présentation »).</summary>
    public event EventHandler? WelcomeRequested;

    /// <summary>The tour of the notch, asked for again.</summary>
    public event EventHandler? TourRequested;

    /// <summary>Opens on a section (its tag in the pane: « Assistant »…).</summary>
    public void ShowSection(string tag)
    {
        if (Sections.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => (string)item.Tag == tag) is { } item)
        {
            Sections.SelectedItem = item;
        }
    }

    private void OnWelcomeClick(object sender, RoutedEventArgs e) => WelcomeRequested?.Invoke(this, EventArgs.Empty);

    private void OnTourClick(object sender, RoutedEventArgs e) => TourRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>A section chosen in the pane: it shows, alone, from its top.</summary>
    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        foreach (var (name, section) in new (string, FrameworkElement)[]
        {
            ("General", GeneralSection), ("Handle", HandleSection), ("Search", SearchSection), ("Lyrics", LyricsSection),
            ("Clipboard", ClipboardSection), ("DropZone", DropZoneSection), ("Pomodoro", PomodoroSection), ("Assistant", AssistantSection),
        })
        {
            section.Visibility = name == tag ? Visibility.Visible : Visibility.Collapsed;
        }

        SectionScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>Step 1 of getting a key: Google AI Studio's page of API keys, in the browser.</summary>
    private async void OnOpenAiStudioClick(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(AiStudioKeys);

    /// <summary>
    /// Step 5: the key copied in AI Studio, pasted from the clipboard (only now, on this click) and saved as « Enregistrer »
    /// does: tried with Google first.
    /// </summary>
    private async void OnPasteKeyClick(object sender, RoutedEventArgs e)
    {
        string text = "";
        try
        {
            var clipboard = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (clipboard.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                text = (await clipboard.GetTextAsync()).Trim();
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // The clipboard busy elsewhere: as if empty.
        }

        if (text.Length == 0 || text.Contains(' ') || text.Contains('\n'))
        {
            ApiKeyStatus.Text = Loc.T("Copiez d'abord la clé dans AI Studio (le bouton à côté d'elle), puis cliquez ici.");
            return;
        }

        ApiKeyBox.Password = text;
        OnSaveKeyClick(sender, e);
    }

    /// <summary>
    /// The key pasted is first tried with Google (the models it may use are asked for): saved only if it works, and the
    /// model list filled from that answer.
    /// </summary>
    private async void OnSaveKeyClick(object sender, RoutedEventArgs e)
    {
        string key = ApiKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            return;
        }

        SaveKeyButton.IsEnabled = false;
        ApiKeyStatus.Text = Loc.T("Vérification auprès de Google…");
        try
        {
            var models = await _gemini.ListModelsAsync(key);
            if (!_keys.Save(key))
            {
                ApiKeyStatus.Text = Loc.T("Windows n'a pas pu garder la clé.");
                return;
            }

            ApiKeyBox.Password = "";
            ApiKeyStatus.Text = Loc.F("Clé valide : {0} modèles disponibles. Cliquez sur la mascotte pour lui parler.", models.Count);
            await FillModelsAsync(models);
        }
        catch (GeminiException ex)
        {
            ApiKeyStatus.Text = ex.Message;
        }
        finally
        {
            SaveKeyButton.IsEnabled = true;
            ShowKeyState();
        }
    }

    private void OnForgetKeyClick(object sender, RoutedEventArgs e)
    {
        if (_keys.Save(null))
        {
            ApiKeyStatus.Text = Loc.T("Clé oubliée.");
            ShowKeyState();
            _ = FillModelsAsync([]);
        }
    }

    /// <summary>
    /// The models the key may use, « Automatique » first; read from Google when not given (the window opening).
    /// </summary>
    private async Task FillModelsAsync(IReadOnlyList<GeminiModel>? models)
    {
        if (models is null && _keys.Key is { Length: > 0 } key)
        {
            try
            {
                models = await _gemini.ListModelsAsync(key);
            }
            catch (GeminiException ex)
            {
                ApiKeyStatus.Text = ex.Message;
            }
        }

        _fillingModels = true;
        ModelBox.Items.Clear();
        ModelBox.Items.Add(new ComboBoxItem { Content = Loc.T("Automatique"), Tag = "" });
        foreach (var model in models ?? [])
        {
            ModelBox.Items.Add(new ComboBoxItem { Content = model.Name, Tag = model.Id });
        }

        // The model chosen, kept even if this key does not list it (it says so when asked).
        var chosen = ModelBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == Settings.AssistantModel);
        if (chosen is null && Settings.AssistantModel.Length > 0)
        {
            chosen = new ComboBoxItem { Content = Settings.AssistantModel, Tag = Settings.AssistantModel };
            ModelBox.Items.Add(chosen);
        }

        ModelBox.SelectedItem = chosen ?? ModelBox.Items[0];
        ModelBox.IsEnabled = _keys.HasKey;
        _fillingModels = false;
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingModels && ModelBox.SelectedItem is ComboBoxItem { Tag: string id })
        {
            Settings.AssistantModel = id;
        }
    }

    private void ShowAssistantShortcut() => ShowKeys(AssistantKeys, _assistantShortcut.Gesture.Parts, accent: true);

    /// <summary>As for the clipboard's: the next combination pressed becomes the assistant's shortcut.</summary>
    private async void OnRecordAssistantShortcutClick(object sender, RoutedEventArgs e)
    {
        if (_recordingAssistant)
        {
            _assistantShortcut.CancelRecording();
            return;
        }

        _recordingAssistant = true;
        RecordAssistantShortcutButton.Content = Loc.T("Annuler");
        ResetAssistantShortcutButton.IsEnabled = false;
        AssistantShortcutHint.Text = Loc.T("Appuyez sur la nouvelle combinaison… (Échap pour annuler)");
        ShowKeys(AssistantKeys, ["…"], accent: false);

        var gesture = await _assistantShortcut.RecordAsync();

        _recordingAssistant = false;
        RecordAssistantShortcutButton.Content = Loc.T("Modifier");
        ResetAssistantShortcutButton.IsEnabled = true;
        AssistantShortcutHint.Text = Loc.T("Depuis n'importe où, comme un clic sur la mascotte.");
        if (gesture is { } chosen)
        {
            Settings.AssistantShortcut = chosen.ToString();
        }

        ShowAssistantShortcut();
    }

    private void OnResetAssistantShortcutClick(object sender, RoutedEventArgs e) =>
        Settings.AssistantShortcut = AppSettings.DefaultAssistantShortcut;

    // ── Mascot ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A round swatch per colour the mascot can wear (see <see cref="MascotPalette"/>): a click puts it on. The last one
    /// opens Windows' colour picker, for any colour: the mascot follows it live.
    /// </summary>
    private void BuildMascotColors()
    {
        foreach (var color in MascotPalette.Colors)
        {
            var swatch = CreateSwatch(color.Id, Loc.T(color.Name), new SolidColorBrush(color.Tone));
            swatch.Click += (_, _) => Settings.MascotColor = color.Id;
            MascotColors.Children.Add(swatch);
        }

        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorPreviewVisible = false,
            IsHexInputVisible = true,
        };
        bool syncing = false; // The picker shown the colour worn: not a pick (white would become "#FFFFFF").
        picker.ColorChanged += (_, e) =>
        {
            if (!syncing)
            {
                Settings.MascotColor = MascotPalette.Id(e.NewColor);
            }
        };
        var custom = CreateSwatch(CustomSwatch, Loc.T("Personnalisée…"), RainbowBrush());
        custom.Flyout = new Flyout { Content = picker };
        custom.Flyout.Opening += (_, _) =>
        {
            syncing = true;
            picker.Color = MascotPalette.Find(Settings.MascotColor).Tone;
            syncing = false;
        };
        MascotColors.Children.Add(custom);

        ShowMascotColor();
    }

    /// <summary>The custom swatch's tag (the others carry their colour's id).</summary>
    private const string CustomSwatch = "custom";

    internal static Button CreateSwatch(string tag, string name, Brush fill)
    {
        var swatch = new Button
        {
            Tag = tag,
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Content = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 24,
                Height = 24,
                Fill = fill,
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(48, 0, 0, 0)),
                StrokeThickness = 1,
            },
        };
        ToolTipService.SetToolTip(swatch, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, name);
        return swatch;
    }

    /// <summary>The custom swatch, before a colour is picked: every hue, saying « any colour ».</summary>
    private static LinearGradientBrush RainbowBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        Windows.UI.Color[] hues =
        [
            Windows.UI.Color.FromArgb(255, 0xFF, 0x6B, 0x6B), Windows.UI.Color.FromArgb(255, 0xFF, 0xD9, 0x5C),
            Windows.UI.Color.FromArgb(255, 0x6B, 0xE3, 0x8F), Windows.UI.Color.FromArgb(255, 0x5C, 0xB8, 0xFF),
            Windows.UI.Color.FromArgb(255, 0xB4, 0x7C, 0xFF),
        ];
        for (int i = 0; i < hues.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop { Color = hues[i], Offset = i / (double)(hues.Length - 1) });
        }

        return brush;
    }

    /// <summary>The colour worn is ringed with the accent colour; a colour picked freely shows on the custom swatch.</summary>
    private void ShowMascotColor()
    {
        var worn = MascotPalette.Find(Settings.MascotColor);
        var ring = Resource("AccentFillColorDefaultBrush", Microsoft.UI.Colors.SteelBlue);
        var none = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var swatch in MascotColors.Children.OfType<Button>())
        {
            bool custom = (string)swatch.Tag == CustomSwatch;
            swatch.BorderBrush = (custom ? worn.IsCustom : (string)swatch.Tag == worn.Id) ? ring : none;
            if (custom && swatch.Content is Microsoft.UI.Xaml.Shapes.Ellipse dot)
            {
                dot.Fill = worn.IsCustom ? new SolidColorBrush(worn.Tone) : RainbowBrush();
            }
        }
    }

    /// <summary>The shortcut as keycaps, the way the keyboard prints them.</summary>
    private void ShowShortcut(KeyGesture gesture) => ShowKeys(ShortcutKeys, gesture.Parts, accent: true);

    internal static void ShowKeys(Panel target, IEnumerable<string> keys, bool accent)
    {
        target.Children.Clear();
        foreach (string key in keys)
        {
            target.Children.Add(new Border
            {
                MinWidth = 30,
                Padding = new Thickness(9, 3, 9, 4),
                CornerRadius = new CornerRadius(5),
                Background = Resource(accent ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush", accent ? Microsoft.UI.Colors.SteelBlue : Microsoft.UI.Colors.DimGray),
                BorderBrush = Resource("ControlStrokeColorDefaultBrush", Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = key,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Resource(accent ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush", Microsoft.UI.Colors.White),
                },
            });
        }
    }

    /// <summary>A theme brush, or a plain colour should the theme not have it.</summary>
    private static Brush Resource(string key, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush ? brush : new SolidColorBrush(fallback);

    /// <summary>
    /// The next combination pressed, anywhere, becomes the shortcut: it is caught by the notch's keyboard hook, so even
    /// the ones Windows keeps for itself can be chosen. Escape, or the button again, cancels.
    /// </summary>
    private async void OnRecordShortcutClick(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            _shortcut.CancelRecording();
            return;
        }

        _recording = true;
        RecordShortcutButton.Content = Loc.T("Annuler");
        ResetShortcutButton.IsEnabled = false;
        ShortcutHint.Text = Loc.T("Appuyez sur la nouvelle combinaison… (Échap pour annuler)");
        ShowKeys(ShortcutKeys, ["…"], accent: false);

        var gesture = await _shortcut.RecordAsync();

        _recording = false;
        RecordShortcutButton.Content = Loc.T("Modifier");
        ResetShortcutButton.IsEnabled = true;
        ShortcutHint.Text = Loc.T("Avec Ctrl, Alt ou Windows, ou une touche F seule.");
        if (gesture is { } chosen)
        {
            Settings.ClipboardShortcut = chosen.ToString();
        }

        ShowShortcut(_shortcut.Gesture);
    }

    private void OnResetShortcutClick(object sender, RoutedEventArgs e) => Settings.ClipboardShortcut = KeyGesture.Default.ToString();

    public AppSettings Settings { get; }

    public double MinSize => AppSettings.MinCompactLyricsFontSize;

    public double MaxSize => AppSettings.MaxCompactLyricsFontSize;

    public string FormatSize(double size) => $"{size:0} px";

    /// <summary>Where the sung line goes, the notch closed: 0 on the notch, 1 on the taskbar.</summary>
    public int LyricsPlaceIndex
    {
        get => Settings.LyricsPlace == AppSettings.LyricsInTaskbar ? 1 : 0;
        set => Settings.LyricsPlace = value == 1 ? AppSettings.LyricsInTaskbar : AppSettings.LyricsInNotch;
    }

    /// <summary>The preview of the line on the closed notch, when it goes there.</summary>
    public Visibility InNotch(string place) => place == AppSettings.LyricsInTaskbar ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The updates (GitHub's releases): where they stand, looked for, installed.</summary>
    public Ilot.Updates.AppUpdater Updater { get; }

    /// <summary>« Rechercher »: not while it looks or downloads already.</summary>
    public bool CanCheck(Ilot.Updates.UpdateState state) =>
        state is not (Ilot.Updates.UpdateState.Checking or Ilot.Updates.UpdateState.Downloading);

    /// <summary>« Installer »: a new version downloaded, ready.</summary>
    public Visibility ReadyToInstall(Ilot.Updates.UpdateState state) =>
        state == Ilot.Updates.UpdateState.Ready ? Visibility.Visible : Visibility.Collapsed;

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e) => await Updater.CheckAsync();

    private void OnInstallUpdateClick(object sender, RoutedEventArgs e) => Updater.Install();

    /// <summary>« Ilot 1.0.0 »: the version, as built (Directory.Build.props).</summary>
    public string AboutTitle => $"Ilot {typeof(App).Assembly.GetName().Version?.ToString(3)}";

    public double MinLyricsWidth => AppSettings.MinLyricsTaskbarWidth;

    public double MaxLyricsWidth => AppSettings.MaxLyricsTaskbarWidth;

    /// <summary>The preview of the line on the taskbar, when it goes there.</summary>
    public Visibility OnTaskbar(string place) => place == AppSettings.LyricsInTaskbar ? Visibility.Visible : Visibility.Collapsed;

    public double MinHandleWidth => AppSettings.MinHandleWidth;

    public double MaxHandleWidth => AppSettings.MaxHandleWidth;

    public double MinHandleReach => AppSettings.MinHandleReach;

    public double MaxHandleReach => AppSettings.MaxHandleReach;

    public string FormatReach(double reach) => reach == 0
        ? Loc.T("La poignée seule ouvre la notch.")
        : Loc.F("{0:0} px autour de la poignée ouvrent aussi la notch.", reach);

    // The preview draws the handle like the notch does (NotchSurface.CreateGrabber, NotchLayout.InputBoundsFor).
    public double GrabberWidth(double width) => Math.Min(width * 0.47, 56);

    public double ZoneWidth(double width, double reach) => width + 2 * reach;

    public double ZoneHeight(double reach) => 9 + reach / 2;

    // ── Search ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts the notch with Windows, or no longer; the switch goes back if Windows refused.</summary>
    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (StartupToggle.IsOn != StartupLaunch.IsEnabled && !StartupLaunch.Set(StartupToggle.IsOn))
        {
            StartupToggle.IsOn = StartupLaunch.IsEnabled;
        }
    }

    public double MinIdleSeconds => AppSettings.MinIdleSeconds;

    public double MaxIdleSeconds => AppSettings.MaxIdleSeconds;

    public double IdleSecondsStep => AppSettings.IdleSecondsStep;

    /// <summary>« Après 45 s sans rien », « Après 1 minute sans rien », « Après 2 min 30 s sans rien »…</summary>
    public string FormatIdle(double seconds)
    {
        int total = (int)Math.Round(seconds);
        int minutes = total / 60;
        int rest = total % 60;
        return (minutes, rest) switch
        {
            (0, _) => Loc.F("Après {0} s sans rien", rest),
            (1, 0) => Loc.T("Après 1 minute sans rien"),
            (_, 0) => Loc.F("Après {0} minutes sans rien", minutes),
            _ => Loc.F("Après {0} min {1:00} s sans rien", minutes, rest),
        };
    }

    private void ShowSearchShortcut() => ShowKeys(SearchKeys, _searchShortcut.Gesture.Parts, accent: true);

    /// <summary>As for the clipboard's: the next combination pressed becomes the search's shortcut.</summary>
    private async void OnRecordSearchShortcutClick(object sender, RoutedEventArgs e)
    {
        if (_recordingSearch)
        {
            _searchShortcut.CancelRecording();
            return;
        }

        _recordingSearch = true;
        RecordSearchShortcutButton.Content = Loc.T("Annuler");
        ResetSearchShortcutButton.IsEnabled = false;
        SearchShortcutHint.Text = Loc.T("Appuyez sur la nouvelle combinaison… (Échap pour annuler)");
        ShowKeys(SearchKeys, ["…"], accent: false);

        var gesture = await _searchShortcut.RecordAsync();

        _recordingSearch = false;
        RecordSearchShortcutButton.Content = Loc.T("Modifier");
        ResetSearchShortcutButton.IsEnabled = true;
        SearchShortcutHint.Text = Loc.T("Partout : la notch devient une barre de recherche, les résultats tombent dessous.");
        if (gesture is { } chosen)
        {
            Settings.SearchShortcut = chosen.ToString();
        }

        ShowSearchShortcut();
    }

    private void OnResetSearchShortcutClick(object sender, RoutedEventArgs e) =>
        Settings.SearchShortcut = AppSettings.DefaultSearchShortcut;

    /// <summary>
    /// The language as an index in the list offered (Windows', French, English). Read at startup: once changed, the hint
    /// says it shows at the next one.
    /// </summary>
    public int LanguageIndex
    {
        get => Math.Max(0, Array.IndexOf(Languages, Settings.Language));
        set
        {
            if (value >= 0 && value < Languages.Length && Languages[value] != Settings.Language)
            {
                Settings.Language = Languages[value];
                LanguageHint.Text = Loc.T("Ce sera la langue de la notch au prochain lancement (Quitter, puis la relancer).");
            }
        }
    }

    /// <summary>The search's screen as an index in the list offered (the pointer's, the active window's, the primary).</summary>
    public int SearchScreenIndex
    {
        get => Math.Max(0, Array.IndexOf(SearchScreens, Settings.SearchScreen));
        set
        {
            if (value >= 0 && value < SearchScreens.Length)
            {
                Settings.SearchScreen = SearchScreens[value];
            }
        }
    }

    public double MinSearchResults => AppSettings.MinSearchResults;

    public double MaxSearchResults => AppSettings.MaxSearchResults;

    public string FormatResults(double count) => Loc.F("{0:0} au plus, les meilleurs d'abord.", count);

    private void OnEverythingChanged(object? sender, EventArgs e) => ShowEverything();

    /// <summary>Where Everything stands, and the way to full speed when it is not there yet.</summary>
    private void ShowEverything()
    {
        bool fullSpeed = EverythingEngine.IsFullSpeed;
        string reach = _everything.IsUsersOwn
            ? Loc.T("votre Everything, déjà lancé")
            : Loc.T(fullSpeed ? "disques entiers, instantanément" : "votre dossier personnel (Pleine vitesse : les disques entiers)");
        EverythingStatusText.Text = _everything.Status switch
        {
            EverythingStatus.Ready => Loc.F("Prêt : {0}.", reach),
            EverythingStatus.Starting => Loc.T("Everything prépare son index…"),
            _ => Loc.T(Settings.SearchFiles ? "Everything n'a pas pu démarrer : la recherche de fichiers attend." : "La recherche de fichiers est désactivée."),
        };
        FullSpeedButton.Visibility = fullSpeed || _everything.IsUsersOwn ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnFullSpeedClick(object sender, RoutedEventArgs e)
    {
        FullSpeedButton.IsEnabled = false;
        EverythingStatusText.Text = Loc.T("Autorisez Windows à installer le service d'Everything…");
        bool done = await _everything.EnableFullSpeedAsync();
        FullSpeedButton.IsEnabled = true;
        ShowEverything();
        if (!done)
        {
            EverythingStatusText.Text += Loc.T(" (pleine vitesse non activée)");
        }
    }

    private void OnResetExclusionsClick(object sender, RoutedEventArgs e) => Settings.SearchExclusions = AppSettings.DefaultSearchExclusions;

    public string DefaultHandleText => Loc.F("Largeur {0:0} px, zone de détection {1:0} px.", AppSettings.DefaultHandleWidth, AppSettings.DefaultHandleReach);

    public bool IsHandleChanged(double width, double reach) =>
        width != AppSettings.DefaultHandleWidth || reach != AppSettings.DefaultHandleReach;

    private void OnResetHandleClick(object sender, RoutedEventArgs e)
    {
        Settings.HandleWidth = AppSettings.DefaultHandleWidth;
        Settings.HandleReach = AppSettings.DefaultHandleReach;
    }

    public double MinLimit => AppSettings.MinClipboardLimit;

    public double MaxLimit => AppSettings.MaxClipboardLimit;

    /// <summary>The host as an index in the list offered (Send, Gofile).</summary>
    public int HostIndex
    {
        get => Math.Max(0, UploadHosts.All.ToList().FindIndex(host => host.Id == Settings.UploadHost));
        set
        {
            if (value >= 0 && value < UploadHosts.All.Count)
            {
                Settings.UploadHost = UploadHosts.All[value].Id;
            }
        }
    }

    /// <summary>Only some hosts let links expire after a chosen time.</summary>
    public bool HasExpiry(string host) => UploadHosts.Get(host).HasExpiry;

    /// <summary>The link lifetime as an index in the list offered (1 h, 12 h, 24 h, 72 h).</summary>
    public int ExpiryIndex
    {
        get => Math.Max(0, AppSettings.UploadExpiries.ToList().IndexOf(Settings.UploadExpiry));
        set
        {
            if (value >= 0 && value < AppSettings.UploadExpiries.Count)
            {
                Settings.UploadExpiry = AppSettings.UploadExpiries[value];
            }
        }
    }

    public string FormatLimit(double limit) =>
        Loc.F("{0:0} éléments, sans compter les épinglés. Au-delà, chaque nouvelle copie remplace la plus ancienne.", limit);

    /// <summary>Nothing is removed without asking.</summary>
    private async void OnClearClipboardClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Loc.T("Nettoyer le presse-papier ?"),
            Content = Loc.T("Tout l'historique sera supprimé, sauf les éléments épinglés."),
            PrimaryButtonText = Loc.T("Effacer"),
            CloseButtonText = Loc.T("Annuler"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _clipboard.Clear();
        ClearClipboardButton.Content = Loc.T("Effacé");
        ClearClipboardButton.IsEnabled = false;
    }

    private void CenterOnPrimaryDisplay()
    {
        var display = DisplayArea.Primary;
        double scale = NativeMethods.GetScaleForMonitor(Win32Interop.GetMonitorFromDisplayId(display.DisplayId));
        int width = (int)(LogicalWidth * scale);
        int height = (int)(LogicalHeight * scale);
        var area = display.WorkArea;
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }
}
