using Microsoft.UI.Xaml;
using Ilot.Notch;
using Ilot.Localization;

namespace Ilot.Modules.Clipboard;

/// <summary>Clipboard manager: history of copies (text, links, colours, images), with pins.</summary>
public sealed class ClipboardModule(ClipboardViewModel viewModel, ClipboardShortcut shortcut) : INotchModule
{
    public string Title => Loc.T("Presse-papier");

    public string Glyph => "";

    public FrameworkElement CreateView(NotchContext context) => new ClipboardView(viewModel, shortcut, context);
}
