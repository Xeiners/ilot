using Microsoft.UI.Xaml;

namespace Ilot.Windowing;

/// <summary>
/// A sheet that leaves the notch's bubble for a window of its own (see <see cref="DetachedWindow"/>): the same sheet, with
/// the window's buttons among its own.
/// </summary>
public interface IDetachableSheet
{
    /// <summary>The window's buttons: its pin (before « Rattacher »), « Réduire » (before « Fermer »).</summary>
    void AddWindowTools(UIElement pin, UIElement minimize);

    /// <summary>The window came in front: the keys go where they are expected (the card, the text).</summary>
    void FocusContent();

    /// <summary>Its corners rounded as the window's, for what it paints from edge to edge.</summary>
    void RoundCorners(double radius);
}
