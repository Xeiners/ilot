using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Ilot.Notch;

/// <summary>
/// A module that takes things dragged onto the notch (files…). Dragging something it accepts over the notch, even
/// closed, opens it on the module's tab; see <see cref="NotchContext.AcceptDrops"/>.
/// </summary>
public interface INotchDropTarget
{
    /// <summary>Whether the dragged data is for this module. Called often while dragging: keep it cheap.</summary>
    bool Accepts(DataPackageView data);

    /// <summary>Caption shown next to the pointer while dragging over the notch.</summary>
    string DropCaption { get; }

    /// <summary>The drag is over the notch (true) or has left it (false): a cue to light the drop area up.</summary>
    void OnDragHover(bool over);

    /// <summary>Where the drag is, in the module's view, as it moves over the notch.</summary>
    void OnDragMoved(Point position);

    Task DropAsync(DataPackageView data);
}
