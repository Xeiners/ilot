using Microsoft.UI.Dispatching;
using Ilot.Modules.Clipboard;
using Ilot.Settings;
using Ilot.Windowing;

namespace Ilot.Modules.Assistant.Voice;

/// <summary>
/// Ctrl + Alt + M, from anywhere, while talking aloud with the assistant: the microphone cut, or back (see
/// <see cref="AssistantVoice.SetMuted"/>), the notch closed or not. Only while a talk is on: the rest of the time the
/// key is the apps' own (no hook at all). In a game or a video full screen, it does nothing (see <see cref="GlobalShortcut"/>).
/// </summary>
public sealed class MicrophoneShortcut : GlobalShortcut
{
    public const string Shortcut = "Ctrl+Alt+M";

    public MicrophoneShortcut(DispatcherQueue dispatcher, AppSettings settings, AssistantVoice voice)
        : base(dispatcher, settings, "Microphone", () => Shortcut, () => voice.IsOn, KeyGesture.Parse(Shortcut)!.Value)
    {
        voice.StateChanged += (_, _) => Update(); // Taken while talking, let go after.
        Pressed += (_, _) => voice.ToggleMute();
    }

    /// <summary>As the user reads it: « Ctrl + Alt + M ».</summary>
    public static string Display => KeyGesture.Parse(Shortcut)!.Value.DisplayText;
}
