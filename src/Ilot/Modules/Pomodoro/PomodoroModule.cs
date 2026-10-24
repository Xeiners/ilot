using Microsoft.UI.Xaml;
using Ilot.Localization;
using Ilot.Notch;
using Ilot.Settings;

namespace Ilot.Modules.Pomodoro;

/// <summary>
/// The Pomodoro: focus, short break, again, a long break after so many; the timer stays at the top of the closed notch,
/// and the mascot reads its book while you focus.
/// </summary>
public sealed class PomodoroModule(PomodoroTimer timer, PomodoroStats stats, AppSettings settings) : INotchModule
{
    public string Title => Loc.T("Pomodoro");

    public string Glyph => "";

    public FrameworkElement CreateView(NotchContext context) => new PomodoroView(timer, stats, settings, context);
}
