using Microsoft.UI.Dispatching;
using Ilot.Modules.Clipboard;
using Ilot.Settings;
using Ilot.Windowing;

namespace Ilot.Modules.Search;

/// <summary>
/// The search's shortcut (Ctrl + Q by default), anywhere: the search opens in the notch (and closes, pressed again).
/// Chosen in the settings, like the clipboard's and the assistant's (see <see cref="GlobalShortcut"/>): the hook sees the
/// keys before any app or registered hotkey (another launcher keeping the same combination, say).
/// </summary>
public sealed class SearchShortcut(DispatcherQueue dispatcher, AppSettings settings) : GlobalShortcut(
    dispatcher,
    settings,
    "Search",
    () => settings.SearchShortcut,
    () => settings.SearchShortcutOn,
    Default,
    nameof(AppSettings.SearchShortcut),
    nameof(AppSettings.SearchShortcutOn))
{
    public static KeyGesture Default { get; } = KeyGesture.Parse(AppSettings.DefaultSearchShortcut)!.Value;
}
