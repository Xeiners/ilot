namespace Ilot.Notch;

/// <summary>Something the closed notch just showed, whichever module it came from (see <see cref="NotchContext.Happened"/>).</summary>
public enum NotchEvent
{
    /// <summary>A brief announcement on the pill ("Texte copié"…).</summary>
    Announcement,

    /// <summary>A card for a moment under the pill (a device connected…).</summary>
    Card,

    /// <summary>A question waiting for an answer (a file from the phone…).</summary>
    Question,
}
