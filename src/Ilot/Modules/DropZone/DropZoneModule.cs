using Microsoft.UI.Xaml;
using Ilot.Notch;

namespace Ilot.Modules.DropZone;

/// <summary>Drop files on the notch: they go to Send (encrypted) or Gofile, and their link lands in the clipboard.</summary>
public sealed class DropZoneModule(DropZoneViewModel viewModel) : INotchModule
{
    public string Title => "Drop Zone";

    public string Glyph => "";

    public FrameworkElement CreateView(NotchContext context) => new DropZoneView(viewModel, context);
}
