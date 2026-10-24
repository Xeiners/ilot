using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Ilot.Controls;
using Ilot.Localization;
using Ilot.Settings;
using Windows.Foundation;
using Windows.UI;

namespace Ilot.Modules.Pomodoro;

/// <summary>
/// The Pomodoro's settings in a drop under the notch (its tab's gear): the lengths and the series on rulers (see
/// <see cref="RulerPicker"/>) with their value, what starts on its own and the chime as switches. Applied as they move (the same settings as the settings
/// window's, which follows).
/// </summary>
public sealed partial class PomodoroSettingsPanel : Grid
{
    public static readonly Size Size = new(660, 300);

    private static readonly SolidColorBrush Text = new(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Faint = new(Color.FromArgb(0x8F, 0xFF, 0xFF, 0xFF));

    private readonly AppSettings _settings;
    private readonly List<Action> _refresh = [];
    private readonly List<RulerPicker> _rulers = [];
    private bool _showing;

    public PomodoroSettingsPanel(AppSettings settings)
    {
        _settings = settings;
        Width = Size.Width;
        Padding = new Thickness(28, 20, 28, 20);
        RowSpacing = 14;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        title.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = Text });
        title.Children.Add(new TextBlock
        {
            Text = Loc.T("Réglages du Pomodoro"),
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Text,
        });
        Children.Add(title);

        // The lengths and the series: two columns of sliders.
        var sliders = new Grid { ColumnSpacing = 28, RowSpacing = 12 };
        sliders.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sliders.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sliders.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sliders.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(sliders, Ruler(Loc.T("Focus"), 5, 90, 5, 15, () => settings.PomodoroFocusMinutes, v => settings.PomodoroFocusMinutes = v, Minutes), 0, 0);
        Place(sliders, Ruler(Loc.T("Petite pause"), 1, 30, 1, 5, () => settings.PomodoroShortBreakMinutes, v => settings.PomodoroShortBreakMinutes = v, Minutes), 0, 1);
        Place(sliders, Ruler(Loc.T("Grande pause"), 5, 60, 5, 15, () => settings.PomodoroLongBreakMinutes, v => settings.PomodoroLongBreakMinutes = v, Minutes), 1, 0);
        Place(sliders, Ruler(Loc.T("Focus avant la grande pause"), 2, 8, 1, 1, () => settings.PomodoroSessions, v => settings.PomodoroSessions = v, v => v.ToString("0", Loc.Culture)), 1, 1);
        Grid.SetRow(sliders, 1);
        Children.Add(sliders);

        // What starts on its own, and the chime.
        var switches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        switches.Children.Add(Switch(Loc.T("Pauses toutes seules"), () => settings.PomodoroAutoStartBreaks, v => settings.PomodoroAutoStartBreaks = v));
        switches.Children.Add(Switch(Loc.T("Focus tout seul"), () => settings.PomodoroAutoStartFocus, v => settings.PomodoroAutoStartFocus = v));
        switches.Children.Add(Switch(Loc.T("Carillon"), () => settings.PomodoroSound, v => settings.PomodoroSound = v));
        Grid.SetRow(switches, 2);
        Children.Add(switches);

        // Changed elsewhere (the settings window): shown here too.
        settings.PropertyChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.MascotColor))
        {
            foreach (var ruler in _rulers)
            {
                ruler.Tint(Mascot.MascotPalette.Find(_settings.MascotColor).Accent); // Its mark in the mascot's colour.
            }
        }

        if (e.PropertyName?.StartsWith("Pomodoro", StringComparison.Ordinal) == true)
        {
            _showing = true;
            foreach (var refresh in _refresh)
            {
                refresh();
            }

            _showing = false;
        }
    }

    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    /// <summary>A setting on a ruler, its name above it on the left, its value on the right.</summary>
    private StackPanel Ruler(string name, double minimum, double maximum, double step, double major, Func<double> read, Action<double> write, Func<double, string> show)
    {
        var value = new TextBlock { FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Text, HorizontalAlignment = HorizontalAlignment.Right };
        var header = new Grid();
        header.Children.Add(new TextBlock { Text = name, FontSize = 12.5, Foreground = Faint });
        header.Children.Add(value);
        var ruler = new RulerPicker(minimum, maximum, step, major, read(), Mascot.MascotPalette.Find(_settings.MascotColor).Accent);
        AutomationProperties.SetName(ruler, name);
        _rulers.Add(ruler);
        value.Text = show(read());
        ruler.ValueChanged += (_, picked) =>
        {
            write(picked);
            value.Text = show(read());
        };
        _refresh.Add(() =>
        {
            ruler.Show(read());
            value.Text = show(read());
        });
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(header);
        panel.Children.Add(ruler);
        return panel;
    }

    /// <summary>A setting as a switch, its name on its left.</summary>
    private StackPanel Switch(string name, Func<bool> read, Action<bool> write)
    {
        var toggle = new ToggleSwitch { IsOn = read(), OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(toggle, name);
        toggle.Toggled += (_, _) =>
        {
            if (!_showing)
            {
                write(toggle.IsOn);
            }
        };
        _refresh.Add(() => toggle.IsOn = read());
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = name, FontSize = 12.5, Foreground = Faint, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(toggle);
        return panel;
    }

    private static string Minutes(double minutes) => Loc.F("{0} min", minutes);
}
