using Microsoft.UI.Dispatching;
using Ilot.Settings;
using Ilot.Windowing;

namespace Ilot.Modules.Clipboard;

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
