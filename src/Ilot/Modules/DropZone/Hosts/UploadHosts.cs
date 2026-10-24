namespace Ilot.Modules.DropZone.Hosts;

/// <summary>The hosts on offer, in the order the settings list them; the first is the default.</summary>
public static class UploadHosts
{
    public static SendHost Send { get; } = new();

    public static GofileHost Gofile { get; } = new();

    public static IReadOnlyList<IUploadHost> All { get; } = [Send, Gofile];

    /// <summary>The host with this identifier; the default (Send) for an unknown one, such as a host since removed.</summary>
    public static IUploadHost Get(string? id) => Find(id) ?? All[0];

    public static IUploadHost? Find(string? id) => All.FirstOrDefault(host => host.Id == id);
}
