using Microsoft.UI.Xaml;

namespace MyNotch.Notch;

/// <summary>
/// A feature living inside the notch, with its own tab in the open notch: media, clipboard… and tomorrow timers,
/// notifications or a calendar.
/// </summary>
/// <remarks>
/// All module views are stacked over the whole open notch and clipped to the morphing shape; each one lays its open
/// content out below <see cref="NotchContext.ContentTop"/> (the tab bar). To move with the notch, a view binds
/// composition properties to the shared state through <see cref="NotchMotion.Bind"/>, and to its tab selection
/// through <see cref="NotchContext.Selection"/>.
/// </remarks>
public interface INotchModule
{
    /// <summary>Tab label.</summary>
    string Title { get; }

    /// <summary>Tab icon: a Segoe Fluent Icons glyph.</summary>
    string Glyph { get; }

    /// <summary>
    /// Whether the module has a tab. One without (the search) is reached through its shortcut only
    /// (<see cref="NotchContext.Show"/>): the tab bar steps aside while it shows, and the notch opens on the last tab
    /// again the next time it is hovered.
    /// </summary>
    bool HasTab => true;

    /// <summary>
    /// The open notch's shape while the module shows (the search bar…), whatever the others ask; <c>null</c>: the
    /// notch's usual one (see <see cref="NotchContext.RequestExpandedShape"/>). It may change (the assistant's bar, taller
    /// with each line): the module tells the notch with <see cref="NotchContext.Reshape"/>.
    /// </summary>
    NotchShape? OpenShape => null;

    /// <summary>
    /// Open on it, the module shows the mascot itself (the assistant, large in its drop): its pod beside the notch steps
    /// aside meanwhile, so there is only one.
    /// </summary>
    bool HostsMascot => false;

    /// <summary>The mascot was clicked: a module that answers to it opens (the assistant) and says so.</summary>
    bool OpenFromMascot() => false;

    FrameworkElement CreateView(NotchContext context);
}
