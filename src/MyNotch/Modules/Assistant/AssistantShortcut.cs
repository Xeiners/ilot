using Microsoft.UI.Dispatching;
using MyNotch.Modules.Clipboard;
using MyNotch.Settings;
using MyNotch.Windowing;

namespace MyNotch.Modules.Assistant;

/// <summary>The assistant's shortcut (Ctrl + Shift + Space by default), from anywhere (see <see cref="GlobalShortcut"/>).</summary>
public sealed class AssistantShortcut(DispatcherQueue dispatcher, AppSettings settings) : GlobalShortcut(
    dispatcher,
    settings,
    "Assistant",
    () => settings.AssistantShortcut,
    () => settings.AssistantShortcutOn,
    Default,
    nameof(AppSettings.AssistantShortcut),
    nameof(AppSettings.AssistantShortcutOn))
{
    public static KeyGesture Default { get; } = KeyGesture.Parse(AppSettings.DefaultAssistantShortcut)!.Value;
}
