namespace Ilot.Modules.Assistant;

/// <summary>What a copy or a move did: how many files, how many bytes.</summary>
public sealed record TransferResult(int Files, long Bytes);

/// <summary>
/// Copying or moving a file or a folder for the assistant (its « copy_item » and « move_item » tools), cleanly: never
/// over anything (the destination must not be there yet), never through a link (a folder that is a junction or a
/// symbolic link is not copied into: a move of a folder holding one, to another drive, is refused), checked for room
/// first, and on a failure or a stop what was begun is taken away again (only what this copy made). A move on the
/// same drive is a rename (instant, whole); to another drive, a copy, checked, then the original removed.
/// </summary>
public static class FileTransfer
{
    /// <summary>Room left on the destination drive past the copy, at least (Windows needs some to go on).</summary>
    private const long Margin = 200L * 1024 * 1024;

    /// <summary>Copies <paramref name="source"/> (a file or a folder) to <paramref name="destination"/>, which is not there yet.</summary>
    public static Task<TransferResult> CopyAsync(string source, string destination, CancellationToken cancellation) =>
        Task.Run(() => Copy(source, destination, cancellation), cancellation);

    /// <summary>Moves <paramref name="source"/> to <paramref name="destination"/>, which is not there yet.</summary>
    public static Task<TransferResult> MoveAsync(string source, string destination, CancellationToken cancellation) =>
        Task.Run(() =>
        {
            Ready(source, destination);
            bool folder = Directory.Exists(source);
            if (SameDrive(source, destination))
            {
                // A rename: whole at once, nothing copied.
                var (files, bytes, _) = Measure(source);
                if (folder)
                {
                    Directory.Move(source, destination);
                }
                else
                {
                    File.Move(source, destination, overwrite: false);
                }

                return new TransferResult(files, bytes);
            }

            if (Measure(source).Links > 0)
            {
                throw new IOException(Ilot.Localization.Loc.T("Ce dossier contient des liens : il ne peut pas être déplacé vers un autre disque (copiez-le plutôt)."));
            }

            // To another drive: copied, checked, then the original removed (not stopped halfway once it is being removed).
            var copied = Copy(source, destination, cancellation);
            var there = Measure(destination);
            if (there.Files != copied.Files || there.Bytes != copied.Bytes)
            {
                Remove(destination);
                throw new IOException(Ilot.Localization.Loc.T("La copie ne correspond pas à l'original : rien n'a été déplacé."));
            }

            try
            {
                if (folder)
                {
                    Directory.Delete(source, recursive: true);
                }
                else
                {
                    File.Delete(source);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(Ilot.Localization.Loc.F("Copié, mais l'original n'a pas pu être retiré en entier (un fichier ouvert ?) : {0}", ex.Message));
            }

            return copied;
        }, cancellation);

    private static TransferResult Copy(string source, string destination, CancellationToken cancellation)
    {
        Ready(source, destination);
        var (files, bytes, _) = Measure(source);
        string root = Path.GetPathRoot(destination)!;
        if (new DriveInfo(root).AvailableFreeSpace < bytes + Margin)
        {
            throw new IOException(Ilot.Localization.Loc.F("Pas assez de place sur {0} pour {1}.", root, Size(bytes)));
        }

        try
        {
            if (File.Exists(source))
            {
                File.Copy(source, destination, overwrite: false);
            }
            else
            {
                CopyFolder(new DirectoryInfo(source), destination, cancellation);
            }
        }
        catch
        {
            Remove(destination); // Begun by this copy only (it was not there): taken away again.
            throw;
        }

        return new TransferResult(files, bytes);
    }

    private static void CopyFolder(DirectoryInfo source, string destination, CancellationToken cancellation)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in source.EnumerateFileSystemInfos())
        {
            cancellation.ThrowIfCancellationRequested();
            string to = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo folder)
            {
                if (!folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) // A link: not followed.
                {
                    CopyFolder(folder, to, cancellation);
                }
            }
            else
            {
                File.Copy(entry.FullName, to, overwrite: false);
            }
        }
    }

    /// <summary>The source is there, the destination is not (nothing is ever replaced), and not inside the source.</summary>
    private static void Ready(string source, string destination)
    {
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new IOException(Ilot.Localization.Loc.T("Introuvable : il a peut-être été déplacé ou supprimé."));
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException(Ilot.Localization.Loc.T("Il y a déjà quelque chose à ce nom à l'arrivée : rien n'est jamais remplacé. Choisissez un autre nom."));
        }

        if (CreationPlaces.Inside(destination, source))
        {
            throw new IOException(Ilot.Localization.Loc.T("Pas dans lui-même."));
        }
    }

    /// <summary>A file's, or a folder's files' count and size (its links not followed, but counted).</summary>
    internal static (int Files, long Bytes, int Links) Measure(string path)
    {
        if (File.Exists(path))
        {
            return (1, new FileInfo(path).Length, 0);
        }

        int files = 0, links = 0;
        long bytes = 0;
        var waiting = new Stack<DirectoryInfo>([new DirectoryInfo(path)]);
        while (waiting.TryPop(out var folder))
        {
            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo inner)
                {
                    if (inner.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        links++;
                    }
                    else
                    {
                        waiting.Push(inner);
                    }
                }
                else
                {
                    files++;
                    bytes += ((FileInfo)entry).Length;
                }
            }
        }

        return (files, bytes, links);
    }

    /// <summary>What a copy began, taken away (it was not there before it: only ever what it made).</summary>
    private static void Remove(string destination)
    {
        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true); // It holds no link (none is copied).
            }
            else if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[Assistant] What was begun not taken away: {ex.Message}");
        }
    }

    private static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>« 12 Mo », « 1,4 Go ».</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => Ilot.Localization.Loc.F("{0} o", bytes),
        < 1024 * 1024 => Ilot.Localization.Loc.F("{0} Ko", Math.Round(bytes / 1024.0)),
        < 1024L * 1024 * 1024 => Ilot.Localization.Loc.F("{0} Mo", Math.Round(bytes / (1024.0 * 1024), 1)),
        _ => Ilot.Localization.Loc.F("{0} Go", Math.Round(bytes / (1024.0 * 1024 * 1024), 1)),
    };
}
