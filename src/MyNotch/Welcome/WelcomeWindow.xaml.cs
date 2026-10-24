using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using MyNotch.Interop;
using MyNotch.Localization;
using MyNotch.Mascot;
using MyNotch.Modules.Assistant;
using MyNotch.Modules.Clipboard;
using MyNotch.Modules.Search;
using MyNotch.Settings;
using Windows.Graphics;

namespace MyNotch.Welcome;

/// <summary>
/// The first launch, after the installation: a few steps to set the notch up (the language, the mascot, how the notch
/// stays open and rests, the music and where its sung line goes, the shortcuts, the assistant's key), the mascot greeting
/// at the top, then the tour of the notch, or later.
/// Every choice applies at once, as in the settings; closing it is skipping the rest.
/// </summary>
public sealed partial class WelcomeWindow : Window
{
    private const int LogicalWidth = 620;
    private const int LogicalHeight = 760;

    /// <summary>Where a Gemini API key is made (Google AI Studio).</summary>
    private static readonly Uri AiStudioKeys = new("https://aistudio.google.com/apikey");
    private static readonly string[] Languages = ["auto", "fr", "en"];

    private readonly ApiKeyStore _keys;
    private readonly GeminiClient _gemini;
    private readonly FrameworkElement[] _steps;
    private int _step;
    private bool _done;
    private bool _ready;

    public WelcomeWindow(
        AppSettings settings, ApiKeyStore keys, GeminiClient gemini, ClipboardShortcut clipboard, SearchShortcut search,
        AssistantShortcut assistant)
    {
        Settings = settings;
        _keys = keys;
        _gemini = gemini;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        _steps = [WelcomeStep, MascotStep, NotchStep, MusicStep, ShortcutsStep, AssistantStep, ReadyStep];

        Title = Loc.T("Bienvenue dans MyNotch");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "MyNotch.ico"));
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        CenterOnPrimaryDisplay();

        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, settings.Language));
        StartupToggle.IsOn = StartupLaunch.IsEnabled; // As Windows has it (the installer may have set it).
        AiStudioLink.NavigateUri = AiStudioKeys;
        BuildMascotColors();
        SettingsWindow.ShowKeys(SearchKeys, search.Gesture.Parts, accent: false);
        SettingsWindow.ShowKeys(ClipboardKeys, clipboard.Gesture.Parts, accent: false);
        SettingsWindow.ShowKeys(AssistantKeys, assistant.Gesture.Parts, accent: false);
        ShowKeyState();

        // The mascot is awake, looking at whoever reads, in the colour chosen.
        Buddy.Wear(MascotPalette.Find(settings.MascotColor), animate: false);
        Buddy.IsShown = true;
        Buddy.IsAttentive = true;
        Buddy.Clicked += (_, _) => Buddy.Exclaim();
        settings.PropertyChanged += OnSettingsChanged;

        Closed += (_, _) =>
        {
            settings.PropertyChanged -= OnSettingsChanged;
            Buddy.IsShown = false; // Its timers and its watch on the keyboard stop with the window.
            Finish(tour: false); // Closed: the rest is skipped.
        };

        BuildDots();
        _ready = true;
    }

    public AppSettings Settings { get; }

    /// <summary>The steps are done or skipped (<c>true</c>: the tour of the notch is wanted). Raised once.</summary>
    public event EventHandler<bool>? Finished;

    /// <summary>
    /// The language was changed to one this window does not speak: it must be made again, in it, at this step (see
    /// <see cref="Go"/>). It closes without <see cref="Finished"/>.
    /// </summary>
    public event EventHandler<int>? LanguageChanged;

    /// <summary>Where the sung line goes, the notch closed, as the radio buttons have it: 0 on the notch, 1 on the taskbar.</summary>
    public int LyricsPlaceIndex
    {
        get => Settings.LyricsPlace == AppSettings.LyricsInTaskbar ? 1 : 0;
        set => Settings.LyricsPlace = value == 1 ? AppSettings.LyricsInTaskbar : AppSettings.LyricsInNotch;
    }

    /// <summary>The player can leave the closed notch only when the sung line goes on the taskbar.</summary>
    public bool OnTaskbar(string place) => place == AppSettings.LyricsInTaskbar;

    /// <summary>The notch's choice, as the radio buttons have it: 0 normal, 1 minimized.</summary>
    public int NotchModeIndex
    {
        get => Settings.Minimized ? 1 : 0;
        set => Settings.Minimized = value == 1;
    }

    /// <summary>Shows step <paramref name="step"/> (0 to 6): it slides in from the side it comes from.</summary>
    public void Go(int step)
    {
        step = Math.Clamp(step, 0, _steps.Length - 1);
        int direction = step >= _step ? 1 : -1;
        _step = step;
        for (int i = 0; i < _steps.Length; i++)
        {
            _steps[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        }

        bool last = step == _steps.Length - 1;
        BackButton.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = Loc.T(last ? "Commencer la visite" : "Suivant");
        SkipButton.Content = Loc.T(last ? "Plus tard" : "Passer");
        ShowDots();
        SlideIn(_steps[step], direction);
        if (step > 0)
        {
            Buddy.Exclaim(); // A little word at each step.
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_step == _steps.Length - 1)
        {
            Finish(tour: true);
            Close();
            return;
        }

        Go(_step + 1);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => Go(_step - 1);

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        Finish(tour: false);
        Close();
    }

    private void Finish(bool tour)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        Finished?.Invoke(this, tour);
    }

    // ── Language ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Kept for the next launch (the notch is made in its language then); this window speaks it at once, made again
    /// in it when it is another one than it speaks.
    /// </summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageBox.SelectedIndex < 0)
        {
            return;
        }

        Settings.Language = Languages[LanguageBox.SelectedIndex];
        bool english = Loc.IsEnglish;
        Loc.Initialize(Settings.Language);
        if (Loc.IsEnglish != english)
        {
            _done = true; // Made again, not finished.
            LanguageChanged?.Invoke(this, _step);
            Close();
        }
    }

    // ── Mascot ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The palette's colours, as round swatches: a click puts one on, the mascot above wears it at once.</summary>
    private void BuildMascotColors()
    {
        foreach (var color in MascotPalette.Colors)
        {
            var swatch = SettingsWindow.CreateSwatch(color.Id, Loc.T(color.Name), new SolidColorBrush(color.Tone));
            swatch.Click += (_, _) => Settings.MascotColor = color.Id;
            MascotColors.Children.Add(swatch);
        }

        ShowMascotColor();
    }

    private void ShowMascotColor()
    {
        string worn = MascotPalette.Find(Settings.MascotColor).Id;
        var ring = Resource("AccentFillColorDefaultBrush", Microsoft.UI.Colors.SteelBlue);
        var none = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var swatch in MascotColors.Children.OfType<Button>())
        {
            swatch.BorderBrush = (string)swatch.Tag == worn ? ring : none;
        }
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.MascotColor))
        {
            Buddy.Wear(MascotPalette.Find(Settings.MascotColor));
            ShowMascotColor();
        }
    }

    // ── Notch ───────────────────────────────────────────────────────────────────────────────────────────────

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (StartupToggle.IsOn != StartupLaunch.IsEnabled && !StartupLaunch.Set(StartupToggle.IsOn))
        {
            StartupToggle.IsOn = StartupLaunch.IsEnabled; // Windows said no: as it is.
        }
    }

    // ── Assistant ───────────────────────────────────────────────────────────────────────────────────────────

    private void ShowKeyState() => ApiKeyBox.PlaceholderText = _keys.HasKey
        ? Loc.T("Clé enregistrée : collez-en une autre pour la remplacer")
        : Loc.T("Collez votre clé API");

    /// <summary>As in the settings: the key is first tried with Google, and saved only if it works.</summary>
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
            Buddy.Exclaim();
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

    // ── Steps ───────────────────────────────────────────────────────────────────────────────────────────────

    private void BuildDots()
    {
        for (int i = 0; i < _steps.Length; i++)
        {
            Dots.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3) });
        }

        ShowDots();
    }

    /// <summary>One dot per step; the current one long, in the accent colour.</summary>
    private void ShowDots()
    {
        var current = Resource("AccentFillColorDefaultBrush", Microsoft.UI.Colors.SteelBlue);
        var other = Resource("ControlStrongFillColorDisabledBrush", Microsoft.UI.Colors.Gray);
        for (int i = 0; i < Dots.Children.Count; i++)
        {
            var dot = (Border)Dots.Children[i];
            dot.Width = i == _step ? 20 : 6;
            dot.Background = i == _step ? current : other;
        }
    }

    /// <summary>The step comes in from its side and fades in, on the compositor.</summary>
    private static void SlideIn(UIElement step, int direction)
    {
        var visual = ElementCompositionPreview.GetElementVisual(step);
        var compositor = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(step, true);
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, new Vector3(28 * direction, 0, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, easing);
        slide.Duration = TimeSpan.FromMilliseconds(380);
        visual.StartAnimation("Translation", slide);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, easing);
        fade.Duration = TimeSpan.FromMilliseconds(300);
        visual.StartAnimation("Opacity", fade);
    }

    private static Brush Resource(string key, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush ? brush : new SolidColorBrush(fallback);

    private void CenterOnPrimaryDisplay()
    {
        var display = DisplayArea.Primary;
        double scale = NativeMethods.GetScaleForMonitor(Win32Interop.GetMonitorFromDisplayId(display.DisplayId));
        int width = (int)(LogicalWidth * scale);
        int height = (int)Math.Min(LogicalHeight * scale, display.WorkArea.Height);
        var area = display.WorkArea;
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }
}
