namespace Ilot.Modules.Media.Audio;

/// <summary>Live frequency spectrum of what the speakers are playing.</summary>
public interface IAudioSpectrumService
{
    /// <summary>Number of frequency bands, from bass to treble.</summary>
    int BandCount { get; }

    /// <summary>True while the audio output is being analysed.</summary>
    bool IsListening { get; }

    /// <summary>Starts or stops listening. Listening costs a little CPU: only do it while something plays.</summary>
    void SetEnabled(bool enabled);

    /// <summary>Copies the latest band levels (0 to 1); false when nothing has been heard for a moment.</summary>
    bool TryGetLevels(Span<float> levels);
}
