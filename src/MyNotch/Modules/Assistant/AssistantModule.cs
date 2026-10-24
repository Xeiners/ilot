using Microsoft.UI.Xaml;
using MyNotch.Localization;
using MyNotch.Modules.Assistant.Voice;
using MyNotch.Notch;
using MyNotch.Settings;

namespace MyNotch.Modules.Assistant;

/// <summary>
/// The assistant: the mascot, clicked (or its shortcut), answers questions through Gemini. The notch opens as a bar to
/// ask in, and the conversation falls from it as a drop. No tab: the mascot and the shortcut are the ways in.
/// </summary>
public sealed class AssistantModule : INotchModule
{
    /// <summary>The open notch while asking: a bar, as wide as the notch usually opens (taller with each line, see AssistantView.Shape).</summary>
    public static readonly NotchShape Bar = new(660, 60, 26);

    private readonly AssistantConversation _conversation;
    private readonly AppSettings _settings;
    private readonly AssistantVoice _voice;

    public AssistantModule(AssistantConversation conversation, AssistantVoice voice, AssistantShortcut shortcut, AppSettings settings)
    {
        _conversation = conversation;
        _voice = voice;
        _settings = settings;
        shortcut.Pressed += (_, _) => View?.Toggle();
    }

    public string Title => Loc.T("Assistant");

    public string Glyph => "";

    public bool HasTab => false;

    public NotchShape? OpenShape => View?.Shape ?? Bar;

    /// <summary>The mascot is in the drop, large: its pod steps aside.</summary>
    public bool HostsMascot => true;

    /// <summary>The bar, once the notch has made it.</summary>
    public AssistantView? View { get; private set; }

    public FrameworkElement CreateView(NotchContext context) => View = new AssistantView(_conversation, _voice, context, _settings);

    public bool OpenFromMascot()
    {
        View?.Open();
        return View is not null;
    }
}
