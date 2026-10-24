namespace MyNotch.Modules.DropZone.Hosts;

/// <summary>
/// A file host the Drop Zone can send to. Adding one is a class implementing this, listed in
/// <see cref="UploadHosts.All"/>; most are a form post (<see cref="MultipartUploadHost"/>).
/// </summary>
public interface IUploadHost
{
    /// <summary>Stable identifier, saved in the settings and with each upload.</summary>
    string Id { get; }

    /// <summary>Name shown to the user.</summary>
    string Name { get; }

    /// <summary>Largest file the host takes.</summary>
    long MaxSize { get; }

    /// <summary>The same, as shown to the user: "2,5 Go".</summary>
    string MaxSizeText { get; }

    /// <summary>Whether links expire after the duration chosen in the settings (else the host decides).</summary>
    bool HasExpiry { get; }

    /// <summary>Encrypted on this computer before it leaves: the host cannot read the file.</summary>
    bool IsEncrypted { get; }

    /// <summary>
    /// Sends the file and returns its link. <paramref name="sent"/> is called with the bytes of the file sent so far.
    /// Throws <see cref="HttpRequestException"/> with a status when the host is busy or failing (worth a few more
    /// tries), <see cref="UploadRejectedException"/> when it refuses the file for good, anything else (I/O, socket,
    /// cancellation) when the connection is lost.
    /// </summary>
    Task<string> UploadAsync(UploadItem item, Stream file, Action<long> sent, CancellationToken cancellation);
}

/// <summary>The host said no, for a reason retrying will not fix.</summary>
public sealed class UploadRejectedException(string message) : Exception(message);
