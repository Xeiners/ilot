namespace MyNotch.Modules.Search.Everything;

/// <summary>A file or folder Everything found.</summary>
/// <param name="Name">Its name ("Facture.pdf"; "C:" for a drive).</param>
/// <param name="Folder">The folder it is in ("C:\Users\…"; empty for a drive).</param>
/// <param name="Matches">Where the search matched in <paramref name="Name"/> (start, length), as Everything highlights it.</param>
/// <param name="RunCount">How many times it was opened through Everything (or the notch).</param>
public sealed record EverythingItem(
    string Name,
    string Folder,
    bool IsFolder,
    long? Size,
    DateTimeOffset? Modified,
    int RunCount,
    IReadOnlyList<(int Start, int Length)> Matches)
{
    public string FullPath => Folder.Length == 0 ? Name + "\\" : Path.Combine(Folder, Name);
}
