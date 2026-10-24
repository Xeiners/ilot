using Microsoft.UI.Dispatching;
using MyNotch.Interop;
using MyNotch.Modules.Search;
using MyNotch.Notch;
using MyNotch.Settings;

namespace MyNotch.Windowing;

/// <summary>
/// Ctrl + Q: where the search opens. On the notch's screen (the primary one), in the notch itself. On another screen
/// (the pointer's, or the active window's, as the settings say), in a search notch of its own: a second, satellite
/// window that drops from that screen's top edge for the search, and withdraws into it after. The notch stays where it
/// is. Pressed again while a search shows, it closes it.
/// </summary>
/// <remarks>
/// The satellite is made the first time it is needed, then kept, hidden (it costs nothing out of sight). Both searches
/// share the same results (one <see cref="SearchViewModel"/>): only the one open shows them.
/// </remarks>
internal sealed class SearchRouter
{
    private readonly AppSettings _settings;
    private readonly SearchViewModel _viewModel;
    private readonly SearchModule _notchSearch;
    private readonly NotchWindow _notch;
    private readonly NotchLayout _layout;
    private readonly DispatcherQueue _dispatcher;
    private SearchModule? _satelliteSearch;
    private NotchWindow? _satellite;

    public SearchRouter(
        SearchShortcut shortcut, AppSettings settings, SearchViewModel viewModel, SearchModule notchSearch, NotchWindow notch,
        NotchLayout layout, DispatcherQueue dispatcher)
    {
        _settings = settings;
        _viewModel = viewModel;
        _notchSearch = notchSearch;
        _notch = notch;
        _layout = layout;
        _dispatcher = dispatcher;
        shortcut.Pressed += (_, _) => OnShortcut();
    }

    /// <summary>Opens the search in the notch itself, whatever screen the settings would choose (the tour shows it there).</summary>
    public void OpenInNotch()
    {
        if (_notchSearch.View is { IsOpen: false } view)
        {
            view.Open();
        }
    }

    private void OnShortcut()
    {
        if (_notchSearch.View is { IsOpen: true } open)
        {
            open.Close();
            return;
        }

        if (_satelliteSearch?.View is { IsOpen: true } away)
        {
            away.Close();
            return;
        }

        nint screen = Screen();
        if (screen == 0 || screen == _notch.Monitor)
        {
            _notchSearch.View?.Open();
            return;
        }

        var satellite = Satellite();
        satellite.ShowOn(screen);
        _satelliteSearch!.View?.Open();
    }

    /// <summary>The screen the settings ask for; 0: the notch's own.</summary>
    private nint Screen() => _settings.SearchScreen switch
    {
        "active" => NativeMethods.MonitorFromWindow(NativeMethods.GetForegroundWindow(), NativeMethods.MONITOR_DEFAULTTONEAREST),
        "home" => 0,
        _ => NativeMethods.CursorMonitor(),
    };

    /// <summary>The search notch for other screens: a notch window of its own, holding only the search.</summary>
    private NotchWindow Satellite()
    {
        if (_satellite is not null)
        {
            return _satellite;
        }

        _satelliteSearch = new SearchModule(_viewModel, satellite: true);
        var view = new NotchView(_layout, [_satelliteSearch], _dispatcher, _settings);
        var satellite = new NotchWindow(view, _layout, _settings, satellite: true);
        _satelliteSearch.View!.Ended += (_, _) => satellite.Withdraw();
        _satellite = satellite;
        return satellite;
    }
}
