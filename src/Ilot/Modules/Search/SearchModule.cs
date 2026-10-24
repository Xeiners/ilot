using Microsoft.UI.Xaml;
using Ilot.Notch;
using Ilot.Localization;

namespace Ilot.Modules.Search;

/// <summary>
/// The search, Ctrl + Q from anywhere: the notch opens as a search bar, and the results fall from it as a drop. Apps,
/// files (Everything), VS Code projects, Windows' settings, commands, calculations. No tab: the shortcut is the way in
/// (see <c>SearchRouter</c>, which also opens it in a notch of its own on another screen).
/// </summary>
/// <param name="satellite">The search notch of another screen's, rather than the notch's own.</param>
public sealed class SearchModule(SearchViewModel viewModel, bool satellite = false) : INotchModule
{
    /// <summary>The open notch while searching: a bar, as wide as the notch usually opens.</summary>
    public static readonly NotchShape Bar = new(660, 60, 26);

    public string Title => Loc.T("Recherche");

    public string Glyph => "";

    public bool HasTab => false;

    public NotchShape? OpenShape => Bar;

    /// <summary>The search bar, once the notch has made it.</summary>
    public SearchView? View { get; private set; }

    public FrameworkElement CreateView(NotchContext context) => View = new SearchView(viewModel, context, satellite);
}
