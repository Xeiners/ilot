using Microsoft.UI.Xaml;
using Ilot.Modules.Media.Audio;
using Ilot.Modules.Media.Lyrics;
using Ilot.Notch;
using Ilot.Settings;
using Ilot.Localization;

namespace Ilot.Modules.Media;

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
