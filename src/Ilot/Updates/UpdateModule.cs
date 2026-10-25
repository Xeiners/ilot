using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ilot.Localization;
using Ilot.Notch;

namespace Ilot.Updates;

/// <summary>
/// The updates on the notch: no tab, nothing open; when a new version is ready (<see cref="AppUpdater.Ready"/>), the
/// closed notch asks, like an incoming call: install it now, or later (the next time Ilot starts).
/// </summary>
public sealed class UpdateModule(AppUpdater updater) : INotchModule
{
    private NotchContext? _context;
    private UpdateCard? _card;
    private IDisposable? _question;

    public string Title => Loc.T("Mise à jour");

    public string Glyph => "";

    public bool HasTab => false;

    public FrameworkElement CreateView(NotchContext context)
    {
        _context = context;
        updater.Ready += (_, _) => Ask();
        return new Grid { IsHitTestVisible = false };
    }

    private void Ask()
    {
        if (_context is not { } context || updater.Available is not { } version)
        {
            return;
        }

        if (_card is null)
        {
            _card = new UpdateCard(context);
            _card.InstallRequested += (_, _) =>
            {
                Dismiss();
                updater.Install();
            };
            _card.Later += (_, _) => Dismiss();
        }

        _card.Show(version);
        _question ??= context.Ask(_card, _card.CardSize);
    }

    private void Dismiss()
    {
        _question?.Dispose();
        _question = null;
    }
}
