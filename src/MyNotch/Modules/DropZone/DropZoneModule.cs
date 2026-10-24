using Microsoft.UI.Xaml;
using MyNotch.Notch;

namespace MyNotch.Modules.DropZone;

/// <summary>Drop files on the notch: they go to Send (encrypted) or Gofile, and their link lands in the clipboard.</summary>
public sealed class DropZoneModule(DropZoneViewModel viewModel) : INotchModule
{
    public string Title => "Drop Zone";

    public string Glyph => "";

    public FrameworkElement CreateView(NotchContext context) => new DropZoneView(viewModel, context);
}
