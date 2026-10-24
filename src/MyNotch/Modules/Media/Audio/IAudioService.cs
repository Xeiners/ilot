using MyNotch.Localization;

namespace MyNotch.Modules.Media.Audio;

/// <summary>The system's master volume, as it is now.</summary>
/// <param name="Level">0 to 1.</param>
/// <param name="IsAvailable">An output device is there to have a volume.</param>
public readonly record struct VolumeState(double Level, bool IsMuted, bool IsAvailable)
{
    public static VolumeState Unavailable { get; } = new(0, false, false);

    /// <summary>"70 %", or "Muet".</summary>
    public string Text => IsMuted ? Loc.T("Muet") : $"{Level * 100:0} %";

    /// <summary>Segoe Fluent Icons speaker, with as many waves as the level (crossed out when muted).</summary>
    public string Glyph => IsMuted || !IsAvailable ? ""
        : Level <= 0.001 ? ""
        : Level < 0.34 ? ""
        : Level < 0.67 ? ""
        : "";
}

/// <summary>
/// The system's master volume and mute (the default output device's). Read and changed on demand only: nothing is
/// polled, nothing listens in between.
/// </summary>
public interface IAudioService
{
    VolumeState Read();

    /// <summary>One step up (+1) or down (-1), 5 % each, landing on multiples of 5 %. Turning it up unmutes.</summary>
    VolumeState Step(int direction);

    /// <summary>Sets the level (0 to 1) directly, as a slider does; anything above zero unmutes.</summary>
    VolumeState SetLevel(double level);

    VolumeState ToggleMute();
}
