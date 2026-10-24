using System.Diagnostics;

namespace MyNotch.Modules.DropZone.Local;

public enum TransferDirection
{
    /// <summary>The phone downloads a file shared by the PC.</summary>
    ToPhone,

    /// <summary>The phone sends a file to the PC.</summary>
    FromPhone,
}

/// <summary>A file (or a folder, zipped as it is downloaded) offered to the phone.</summary>
/// <param name="Size">In bytes; for a folder, the size of its files (the archive is stored, not compressed).</param>
public sealed record SharedFile(string Path, string Name, long Size, bool IsFolder);

/// <summary>One file on its way over the local network. Its progress is written by the server's thread.</summary>
public sealed class LocalTransfer(TransferDirection direction, string name, long size)
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);

    private long _done;
    private long _sampledBytes;
    private long _sampledAt = Stopwatch.GetTimestamp();

    public TransferDirection Direction { get; } = direction;

    public string Name { get; } = name;

    /// <summary>In bytes.</summary>
    public long Size { get; } = size;

    public long Done => Interlocked.Read(ref _done);

    public double Progress => Size > 0 ? Math.Clamp((double)Done / Size, 0, 1) : 0;

    /// <summary>The speed, smoothed: brought up to date by <see cref="Sample"/>, on the UI thread.</summary>
    public double BytesPerSecond { get; private set; }

    internal void Add(long bytes) => Interlocked.Add(ref _done, bytes);

    /// <summary>Measures the speed since the last sample (at most every <see cref="SampleInterval"/>).</summary>
    internal void Sample()
    {
        long done = Done;
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(_sampledAt, now).TotalSeconds;
        if (seconds < SampleInterval.TotalSeconds)
        {
            return;
        }

        double speed = (done - _sampledBytes) / seconds;
        BytesPerSecond = BytesPerSecond <= 0 ? speed : BytesPerSecond * 0.7 + speed * 0.3;
        _sampledBytes = done;
        _sampledAt = now;
    }
}
