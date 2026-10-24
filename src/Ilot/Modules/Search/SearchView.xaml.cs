using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Ilot.Controls;
using Ilot.Interop;
using Ilot.Modules.Code;
using Ilot.Notch;
using Windows.System;
using Windows.UI.Core;
using Ilot.Localization;

namespace Ilot.Modules.Search;

/// <summary>
/// The search bar the notch becomes on Ctrl + Q, and the drop of results under it: it falls from the notch with the
/// first results, stretches and gathers as they come and go, and is drawn back in when the search is over. « code »
/// typed (and words after it): VS Code's recent projects instead, as a list in the Code tiles' look (see <see cref="CodeShelf"/>).
/// </summary>
/// <remarks>
/// The keyboard is taken for as long as the search shows (the notch never takes it on its own). Opening something
/// leaves it to what opened; Escape or a click elsewhere hands it back to the app that had it.
/// </remarks>
public sealed partial class SearchView : UserControl
{
    private readonly SearchViewModel _viewModel;
    private readonly NotchContext _context;
    private readonly SearchResultsPanel _panel = new();

    // What the drop holds: the results, or the projects (« code »), one shown at a time.
    private readonly Grid _drop = new();
    private CodeShelf? _shelf;
    private string[]? _code;
    private NotchBubble? _bubble;
    private readonly bool _satellite;
    private IDisposable? _keyboard;
    private bool _open;

    /// <summary>Something was opened from the last search: it is done with, the next one starts empty.</summary>
    private bool _used;

    /// <param name="satellite">In the search notch of another screen (see <c>SearchRouter</c>), rather than the notch.</param>
    public SearchView(SearchViewModel viewModel, NotchContext context, bool satellite)
    {
        _viewModel = viewModel;
        _context = context;
        _satellite = satellite;
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.

        // The bar blooms in as the notch opens: the content fades and settles, the magnifier turns and grows into place.
        ElementCompositionPreview.SetIsTranslationEnabled(LayoutRoot, true);
        var root = ElementCompositionPreview.GetElementVisual(LayoutRoot);
        context.Bind($"Clamp((n.Expansion - 0.4) * 2.2, 0, 1) * {NotchContext.TabOpacity}").Start(root, "Opacity");
        context.Bind("Vector3(0, (1 - n.Progress) * -6, 0)").Start(root, "Translation");
        var glyph = ElementCompositionPreview.GetElementVisual(SearchGlyph);
        glyph.CenterPoint = new Vector3(10, 10, 0);
        context.Bind("Vector3(Lerp(0.55, 1, n.Progress), Lerp(0.55, 1, n.Progress), 1)").Start(glyph, "Scale");
        context.Bind("(1 - n.Progress) * -50").Start(glyph, "RotationAngleInDegrees");

        _drop.Children.Add(_panel);
        QueryBox.TextChanged += (_, _) =>
        {
            Hint.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchGlyph.Opacity = QueryBox.Text.Length == 0 ? 0.55 : 1;
            if (CodeWords(QueryBox.Text) is { } words)
            {
                ShowProjects(words);
                return;
            }

            LeaveProjects();
            _viewModel.Search(QueryBox.Text);
        };
        _viewModel.Projects.Launched += (_, _) =>
        {
            if (_code is not null && _open)
            {
                Launched(); // A project opened from here: VS Code (or the Explorer) takes over.
            }
        };
        QueryBox.PreviewKeyDown += OnPreviewKeyDown;
        _viewModel.ResultsChanged += (_, _) => ShowResults();
        _viewModel.Everything.StatusChanged += (_, _) => ShowEverything();
        _panel.Invoked += (_, result) => Open(result, SearchOpening.Open);
        _panel.ActionChosen += Open;
        _panel.MenuClosed += (_, _) =>
        {
            if (_keyboard is not null)
            {
                QueryBox.Focus(FocusState.Programmatic); // Back to typing, if the search is still there.
            }
        };

        // The notch closed (a click elsewhere…), or another tab took over: the search is over.
        context.ExpandedChanged += (_, _) =>
        {
            if (!context.IsExpanded)
            {
                _bubble = null; // Drawn back in with the notch.
                LeaveProjects();
                EndTyping();
                End();
            }
        };
        context.SelectionChanged += (_, _) =>
        {
            if (!context.IsSelected)
            {
                Leave();
            }
        };
    }

    /// <summary>The search is on screen, being typed into.</summary>
    public bool IsOpen => _open;

    /// <summary>The search is over (closed, given up, or something opened from it): a satellite goes away.</summary>
    public event EventHandler? Ended;

    /// <summary>
    /// Ctrl + Q (see <c>SearchRouter</c>): the search opens, ready to type, with the last search there and selected (unless
    /// the settings say otherwise, or something was opened from it).
    /// </summary>
    public void Open()
    {
        _open = true;
        if (!_viewModel.KeepsQuery || _used)
        {
            _used = false;
            QueryBox.Text = "";
        }

        _context.Show();
        _keyboard ??= _context.TakeKeyboard(OnKeyboardLost);
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();
        _viewModel.Opened();
        ShowEverything();
        if (CodeWords(QueryBox.Text) is { } words)
        {
            ShowProjects(words);
        }
        else if (QueryBox.Text.Length > 0)
        {
            _viewModel.Search(QueryBox.Text);
        }
    }

    // ── « code »: the projects ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The words after « code » when the query asks for the projects (« code », or « code » and words: « code react »);
    /// <c>null</c> for any other search.
    /// </summary>
    private static string[]? CodeWords(string query)
    {
        string text = query.TrimStart();
        if (!text.StartsWith("code", StringComparison.OrdinalIgnoreCase) || (text.Length > 4 && !char.IsWhiteSpace(text[4])))
        {
            return null;
        }

        return TextSearch.Words(text[4..]);
    }

    /// <summary>
    /// The projects in the drop, for <paramref name="words"/>: the row of tiles takes the results' place (read again
    /// from VS Code's history each time « code » is typed anew), the first match chosen.
    /// </summary>
    private async void ShowProjects(string[] words)
    {
        bool entering = _code is null;
        _code = words;
        if (_shelf is null)
        {
            _shelf = new CodeShelf(_viewModel.Projects);
            _shelf.Changed += (_, _) =>
            {
                if (_code is not null)
                {
                    ShowDrop(_shelf.Fit()); // As tall as the list, as it filters or is read again.
                }
            };
            _drop.Children.Add(_shelf);
        }

        _panel.Visibility = Visibility.Collapsed;
        _shelf.Visibility = Visibility.Visible;
        _shelf.Show(words);
        ShowDrop(_shelf.Fit());
        if (entering)
        {
            _viewModel.Projects.Attach(_context);
            await _viewModel.Projects.RefreshAsync();
            if (_code is not null)
            {
                _shelf.Show(_code);
            }
        }
    }

    /// <summary>Another search than « code »: the results are back in the drop.</summary>
    private void LeaveProjects()
    {
        if (_code is null)
        {
            return;
        }

        _code = null;
        _shelf!.Visibility = Visibility.Collapsed;
        _panel.Visibility = Visibility.Visible;
    }

    /// <summary>The drop at <paramref name="size"/>: it falls from the notch the first time, then springs to each new size.</summary>
    private void ShowDrop(Windows.Foundation.Size size)
    {
        if (!_context.IsVisible || _keyboard is null)
        {
            return;
        }

        if (_bubble is null)
        {
            _bubble = _context.ShowBubble(_drop, size);
        }
        else
        {
            _bubble.Resize(size);
        }
    }

    /// <summary>
    /// Done (Escape, Ctrl + Q again): the keyboard goes back to the app that had it, and the notch closes at once, the
    /// drop drawn back in with it.
    /// </summary>
    public void Close()
    {
        Leave();
        _context.Close();
        End();
    }

    private void End()
    {
        if (_open)
        {
            _open = false;
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Another tab took over: the search lets go, the notch stays as it is.</summary>
    private void Leave()
    {
        DropResults();
        EndTyping();
    }

    private void EndTyping()
    {
        var keyboard = _keyboard;
        _keyboard = null;
        keyboard?.Dispose();
    }

    /// <summary>A click elsewhere: the search is over, at once (not after the grace period hovering gets).</summary>
    private void OnKeyboardLost()
    {
        _keyboard = null;
        DropResults();
        _context.Close();
        End();
    }

    private void ShowResults()
    {
        if (!_context.IsVisible || _keyboard is null || _code is not null)
        {
            return; // The projects show instead (« code »): results still coming for the search before are not wanted.
        }

        if (string.IsNullOrWhiteSpace(_viewModel.Query))
        {
            DropResults();
            return;
        }

        var results = _viewModel.Results;
        _panel.Show(results, _viewModel.Query, _viewModel.Icons, _viewModel.Confirming);
        ShowDrop(SearchResultsPanel.SizeFor(results.Count));
    }

    private void DropResults()
    {
        var bubble = _bubble;
        _bubble = null;
        bubble?.Dispose();
        LeaveProjects(); // Read again the next time « code » is typed.
    }

    private void ShowEverything() => _panel.ShowEverything(_viewModel.Everything.Status, _viewModel.Everything.IsUsersOwn);

    private async void Open(SearchResult result, SearchOpening opening)
    {
        if (opening >= SearchOpening.OpenWith)
        {
            RunShell(result, opening);
            return;
        }

        switch (await _viewModel.OpenAsync(result, opening))
        {
            case SearchOutcome.Launched:
                Launched();
                break;

            case SearchOutcome.Copied:
                Copied();
                break;

            case SearchOutcome.Confirm:
                ShowResults();
                break;
        }
    }

    /// <summary>What opened takes the screen and the keyboard: nothing is handed back over it.</summary>
    private void Launched()
    {
        _used = true;
        DropResults();
        if (_keyboard is { } keyboard)
        {
            _keyboard = null;
            NotchContext.HandOffKeyboard(keyboard);
        }

        _context.Close();
        End();
    }

    private void Copied()
    {
        Close();
        if (!_satellite)
        {
            _context.Announce("", Loc.T("Copié")); // The notch is closed by now: it shows on the pill.
        }
    }

    /// <summary>
    /// A result's Explorer command (see ShellMenu), run as the Explorer runs it. The whole menu waits for the result's own
    /// menu to be gone: both are menus, one at a time.
    /// </summary>
    private void RunShell(SearchResult result, SearchOpening opening)
    {
        if (SearchResultsPanel.ShellPath(result) is not { } path || XamlRoot is null)
        {
            return;
        }

        nint hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        switch (opening)
        {
            case SearchOpening.CopyItem when ShellMenu.Run(hwnd, path, "copy"):
                Copied();
                break;

            case SearchOpening.Delete when ShellMenu.Run(hwnd, path, "delete"):
                RefreshSoon(); // Gone from the disk: gone from the list, once Everything has seen it.
                break;

            case SearchOpening.Properties when ShellMenu.Run(hwnd, path, "properties"):
            case SearchOpening.OpenWith when ShellMenu.OpenWith(hwnd, path):
                Launched();
                break;

            case SearchOpening.MoreOptions:
                NativeMethods.GetCursorPos(out var at);
                bool extended = IsDown(VirtualKey.Shift); // Shift: the extended menu, as in the Explorer.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (ShellMenu.Show(hwnd, path, at, extended))
                    {
                        Launched();
                    }
                });
                break;
        }
    }

    private async void RefreshSoon()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        if (_open)
        {
            _viewModel.Search(QueryBox.Text);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        if (_code is not null && _shelf is not null && OnProjectsKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Down:
                _panel.Move(1);
                break;

            case VirtualKey.Up:
                _panel.Move(-1);
                break;

            case VirtualKey.Enter:
                if (_panel.SelectedResult is { } result)
                {
                    Open(result, control ? SearchOpening.Reveal : shift ? SearchOpening.Elevated : SearchOpening.Open);
                }

                break;

            // Ctrl + C copies the chosen line's path (Ctrl + Shift + C too); with no line chosen, the text box copies.
            case VirtualKey.C when control:
                if (_panel.SelectedResult is not { } copied)
                {
                    return;
                }

                Open(copied, SearchOpening.CopyPath);
                break;

            case VirtualKey.Escape:
                if (QueryBox.Text.Length > 0)
                {
                    QueryBox.Text = "";
                }
                else
                {
                    Close();
                }

                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// A key while the projects show: Down / Up choose the next or previous line, Enter opens it. <c>false</c>: not one
    /// of theirs (Escape, typing…).
    /// </summary>
    private bool OnProjectsKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Down:
                _shelf!.Move(1);
                return true;

            case VirtualKey.Up:
                _shelf!.Move(-1);
                return true;

            case VirtualKey.Enter:
                _shelf!.OpenChosen(); // Launched follows (see the projects' Launched).
                return true;

            default:
                return false;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
