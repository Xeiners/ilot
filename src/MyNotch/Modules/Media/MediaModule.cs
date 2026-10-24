using Microsoft.UI.Xaml;
using MyNotch.Modules.Media.Audio;
using MyNotch.Modules.Media.Lyrics;
using MyNotch.Notch;
using MyNotch.Settings;
using MyNotch.Localization;

namespace MyNotch.Modules.Media;

/// <summary>Mini player for whatever is playing on the system: live audio visualizer and synced lyrics.</summary>
public sealed class MediaModule(
    MediaViewModel viewModel,
    LyricsViewModel lyrics,
    IAudioSpectrumService spectrum,
    IAudioService audio,
    AudioDeviceMonitor devices,
    VolumeWatcher volume,
    AppSettings settings) : INotchModule
{
    public string Title => Loc.T("Média");

    public string Glyph => "";

    public FrameworkElement CreateView(NotchContext context) => new MediaView(viewModel, lyrics, spectrum, audio, devices, volume, settings, context);
}
