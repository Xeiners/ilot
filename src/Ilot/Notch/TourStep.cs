namespace Ilot.Notch;

/// <summary>What a step of the tour rings in the open notch (see <see cref="NotchView.StartTour"/>).</summary>
public enum TourTarget
{
    /// <summary>Nothing in particular: the ring goes.</summary>
    None,

    /// <summary>The open notch as a whole.</summary>
    Notch,

    /// <summary>Its tabs.</summary>
    Tabs,

    /// <summary>The quick controls on its left (volume, brightness).</summary>
    Quick,

    /// <summary>The mascot, in its little notch on the right.</summary>
    Mascot,
}

/// <summary>What a step of the tour waits for the user to do, to try it (see <see cref="TourStep.Try"/>).</summary>
public enum TourWait
{
    /// <summary>Nothing: it is read, then « Suivant ».</summary>
    None,

    /// <summary>Another tab chosen.</summary>
    Tab,

    /// <summary>The card's sample file dropped on the notch (nothing is sent).</summary>
    Drop,

    /// <summary>The volume or the brightness turned, or the sound muted.</summary>
    Quick,

    /// <summary>The mascot clicked (it does not open the assistant then).</summary>
    Mascot,

    /// <summary>The notch's menu opened (a right click).</summary>
    Menu,

    /// <summary>Whatever <see cref="TourStep.Await"/> watches (a shortcut pressed…).</summary>
    Custom,
}

/// <summary>
/// A step of the tour of the notch: a card under the open notch (<paramref name="Glyph"/>, <paramref name="Title"/>,
/// <paramref name="Text"/>) while <paramref name="Target"/> is ringed. Without its target (no mascot, no quick controls),
/// a step that <paramref name="NeedsTarget"/> is left out, any other rings the notch instead. An
/// <paramref name="Action"/> adds a button (<paramref name="ActionLabel"/>) that ends the tour, then does it.
/// </summary>
public sealed record TourStep(
    TourTarget Target,
    string Glyph,
    string Title,
    string Text,
    bool NeedsTarget = false,
    string? ActionLabel = null,
    Action? Action = null)
{
    /// <summary>What the user is asked to do to try it (« Cliquez sur un autre onglet. »); the step waits for it.</summary>
    public TourWait Wait { get; init; }

    /// <summary>The module whose tab the notch shows for the step (its type: the Révisions…); <c>null</c>: as it is.</summary>
    public Type? Module { get; init; }

    /// <summary>The invitation to try it, shown while it waits (or just shown, on a step that waits for nothing).</summary>
    public string? Try { get; init; }

    /// <summary>Said once it is done; the tour then goes on by itself.</summary>
    public string? Done { get; init; }

    /// <summary>
    /// For <see cref="TourWait.Custom"/>: starts watching, calls its argument when the user did it, and stops when the
    /// returned token is disposed (the step left).
    /// </summary>
    public Func<Action, IDisposable>? Await { get; init; }

    /// <summary>
    /// Tried, it opens something that takes the notch (the search): the tour steps aside, and comes back on its next
    /// step once that is closed (the notch closed, or back on a tab).
    /// </summary>
    public bool StepsAside { get; init; }
}
