namespace Ilot.Modules.DropZone.Local;

/// <summary>
/// A file the phone has sent, waiting for the user to accept it (it then joins Downloads) or refuse it (it is
/// deleted). Until then it is a hidden part file, so nothing shows in Downloads.
/// </summary>
public sealed class IncomingFile(string id, string name, long size, string partialPath)
{
    private readonly TaskCompletionSource<string?> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Its handle for the phone's page, which asks what became of it.</summary>
    public string Id { get; } = id;

    public string Name { get; } = name;

    /// <summary>In bytes.</summary>
    public long Size { get; } = size;

    internal string PartialPath { get; } = partialPath;

    /// <summary>Where it was saved once accepted; <c>null</c> if refused. Completes on the decision.</summary>
    public Task<string?> Decision => _decision.Task;

    public bool IsDecided => _decision.Task.IsCompleted;

    internal void Decide(string? savedPath) => _decision.TrySetResult(savedPath);
}
