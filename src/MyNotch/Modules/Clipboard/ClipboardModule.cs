using Microsoft.UI.Xaml;
using MyNotch.Notch;
using MyNotch.Localization;

namespace MyNotch.Modules.Clipboard;

/// <summary>Clipboard manager: history of copies (text, links, colours, images), with pins.</summary>
public sealed class ClipboardModule(ClipboardViewModel viewModel, ClipboardShortcut shortcut) : INotchModule
{
    public string Title => Loc.T("Presse-papier");

    public string Glyph => "";

    public FrameworkElement CreateView(NotchContext context) => new ClipboardView(viewModel, shortcut, context);
}
