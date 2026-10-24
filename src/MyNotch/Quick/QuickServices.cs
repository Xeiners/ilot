using MyNotch.Modules.Media.Audio;
using MyNotch.Modules.Search;

namespace MyNotch.Quick;

/// <summary>
/// What the quick controls beside the open notch work with: the system volume, the screen's brightness, and the volume
/// mixer (with the apps' icons, as the search draws them).
/// </summary>
public sealed record QuickServices(IAudioService Audio, VolumeWatcher Volume, BrightnessService Brightness, VolumeMixer Mixer, ShellIcons Icons);
