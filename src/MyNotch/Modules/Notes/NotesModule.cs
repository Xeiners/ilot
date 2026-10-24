using Microsoft.UI.Xaml;
using MyNotch.Notch;
using MyNotch.Localization;

namespace MyNotch.Modules.Notes;

/// <summary>Notes in Markdown, written in a notch that grows tall for them, saved locally as they are typed.</summary>
public sealed class NotesModule(NotesViewModel viewModel) : INotchModule
{
    public string Title => Loc.T("Notes");

    public string Glyph => "";

    public FrameworkElement CreateView(NotchContext context) => new NotesView(viewModel, context);
}
