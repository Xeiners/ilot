using Microsoft.UI.Dispatching;
using MyNotch.Settings;
using MyNotch.Windowing;

namespace MyNotch.Modules.Clipboard;

/// <summary>
/// The clipboard shortcut (Windows + V by default): the notch's clipboard opens instead of Windows' own (see
/// <see cref="GlobalShortcut"/>).
/// </summary>
public sealed class ClipboardShortcut(DispatcherQueue dispatcher, AppSettings settings) : GlobalShortcut(
    dispatcher,
    settings,
    "Clipboard",
    () => settings.ClipboardShortcut,
    () => settings.ReplaceWindowsClipboard,
    KeyGesture.Default,
    nameof(AppSettings.ReplaceWindowsClipboard),
    nameof(AppSettings.ClipboardShortcut));
