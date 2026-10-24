using System.Net;

namespace Ilot.Modules.DropZone;

/// <summary>Request content that streams a file and reports how many bytes went out (from the sending thread).</summary>
internal sealed class ProgressStreamContent(Stream source, Action<long> sent) : HttpContent
{
    private const int BufferSize = 128 * 1024;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            sent(total);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = source.Length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            source.Dispose();
        }

        base.Dispose(disposing);
    }
}
