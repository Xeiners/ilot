using System.Text;
using Ilot.Interop;
using Ilot.Localization;

namespace Ilot.Modules.Assistant;

/// <summary>
/// Where the assistant may create a folder or a text file (its « create_folder » and « create_text_file » tools), and
/// only create: in the user's own folders (their profile: Desktop, Documents, Downloads…, never its hidden AppData),
/// or anywhere on another drive of this PC (a fixed or removable one, not its recycle bin); never in Windows, the
/// programs' folders, ProgramData, nor elsewhere on the system drive, nor on the network. A path that goes through a
/// link (a junction, a symbolic link) is judged where it really leads. The user still allows each one (see AssistantTools).
/// </summary>
public static class CreationPlaces
{
    /// <summary>A path longer than this is not taken (the classic Windows limit, which Explorer still keeps to).</summary>
    private const int LongestPath = 259;

    /// <summary>Its names, as Gemini or the user say them (the first word of a path): the folder they stand for.</summary>
    private static readonly Dictionary<string, Func<string?>> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Desktop"] = () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ["Bureau"] = () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ["Documents"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ["Mes documents"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ["Downloads"] = Downloads,
        ["Téléchargements"] = Downloads,
        ["Telechargements"] = Downloads,
        ["Pictures"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        ["Images"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        ["Music"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        ["Musique"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        ["Videos"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        ["Vidéos"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        ["Home"] = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    };

    /// <summary>Downloads, where the user moved it (Windows says), else the usual one in their profile.</summary>
    private static string Downloads() =>
        NativeMethods.KnownFolder(NativeMethods.FOLDERID_Downloads) is { Length: > 0 } moved
            ? moved
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>What this PC's places are (the tests give their own: nothing is ever created in the user's real folders).</summary>
    internal sealed record Places(string Profile, string SystemRoot, IReadOnlyList<string> Forbidden, Func<string, string?> Known);

    internal static Places Current { get; set; } = OfThisPc();

    private static Places OfThisPc()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string system = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        string[] forbidden =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(profile, "AppData"),
        ];
        return new Places(profile, system, forbidden.Where(folder => folder.Length > 0).ToList(),
            name => Named.TryGetValue(name, out var find) ? find() : null);
    }

    /// <summary>
    /// <paramref name="asked"/> (a full path, « ~\… », « %USERPROFILE%\… », or one of the user's folders by name:
    /// « Documents\Factures 2026 », « Bureau\Projet ») as the full path to create, or why it may not be.
    /// </summary>
    public static (string? Path, string? Refusal) Resolve(string asked)
    {
        var (path, refusal) = Locate(asked);
        return path is null ? (null, refusal) : Check(path) is { } why ? (null, why) : (path, null);
    }

    /// <summary>
    /// <paramref name="asked"/> as a full path (said as for <see cref="Resolve"/>), on a drive of this PC, wherever it is:
    /// what is to be copied or moved is found so. Nothing is judged here but the path itself.
    /// </summary>
    public static (string? Path, string? Refusal) Locate(string asked)
    {
        var places = Current;
        string path = Environment.ExpandEnvironmentVariables(asked.Trim().Trim('"')).Replace('/', '\\').Trim();
        if (path.Length == 0)
        {
            return (null, Loc.T("Aucun chemin donné."));
        }

        if (path == "~" || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = places.Profile + path[1..];
        }
        else if (!Path.IsPathFullyQualified(path))
        {
            // « Documents\Factures »: the user's folder of that name (the path's first part, « Mes documents » too).
            string[] words = path.Split('\\', 2);
            string? folder = places.Known(words[0]);
            if (folder is null || path.StartsWith('\\'))
            {
                return (null, Loc.T("Dites où : un chemin complet (C:\\…), ou un de vos dossiers (Bureau, Documents, Téléchargements, Images, Musique, Vidéos) suivi du nom."));
            }

            if (words.Length > 1 && words[1].Contains(':'))
            {
                return (null, Loc.F("Nom impossible sous Windows : « {0} ».", words[1])); // « a:b » would be a path of drive A.
            }

            path = words.Length > 1 ? Path.Combine(folder, words[1]) : folder;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return (null, Loc.T("Seulement sur un disque de ce PC, pas sur le réseau."));
        }

        try
        {
            path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); // « .. » resolved: judged where it really is (« C:\ » kept whole).
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, Loc.T("Ce chemin n'est pas valable."));
        }

        return (path, null);
    }

    /// <summary>
    /// Why <paramref name="path"/> (full, there) may not be moved away, or <c>null</c>: it may. Only from where the
    /// assistant may create (see <see cref="Check"/>), and never the user's folders themselves (their profile,
    /// Desktop, Documents, Downloads…) nor one holding them (OneDrive's): what is inside them, yes.
    /// </summary>
    internal static string? Movable(string path)
    {
        if (Check(path) is { } refusal)
        {
            return refusal;
        }

        var places = Current;
        var own = Named.Keys.Select(places.Known).Append(places.Profile).OfType<string>().Where(folder => folder.Length > 0);
        return own.Any(folder => Within(Path.TrimEndingDirectorySeparator(folder), path))
            ? Loc.T("Pas vos dossiers eux-mêmes (Bureau, Documents, Téléchargements…) : seulement ce qu'ils contiennent.")
            : null;
    }

    /// <summary><paramref name="path"/> is <paramref name="folder"/>, or inside it (both full).</summary>
    internal static bool Inside(string path, string folder) => Within(path, folder);

    /// <summary>Why <paramref name="path"/> (full) may not be created, or <c>null</c>: it may.</summary>
    internal static string? Check(string path)
    {
        if (path.Length > LongestPath)
        {
            return Loc.T("Ce chemin est trop long.");
        }

        if (Path.GetPathRoot(path) is not { Length: 3 } root || !char.IsAsciiLetter(root[0]) || root[1] != ':')
        {
            return Loc.T("Seulement sur un disque de ce PC, pas sur le réseau.");
        }

        if (path.Length <= root.Length)
        {
            return Loc.T("Pas à la racine d'un disque : dans un dossier.");
        }

        foreach (string name in path[root.Length..].Split('\\'))
        {
            if (Unfit(name) is { } why)
            {
                return why;
            }
        }

        try
        {
            if (new DriveInfo(root) is not { IsReady: true, DriveType: DriveType.Fixed or DriveType.Removable })
            {
                return Loc.T("Seulement sur un disque de ce PC, pas sur le réseau.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Loc.T("Ce disque n'est pas accessible.");
        }

        if (Place(path) is { } refusal)
        {
            return refusal;
        }

        // Through a link (a junction, a symbolic link): where it really leads is judged too.
        return Real(path) is { } real && !real.Equals(path, StringComparison.OrdinalIgnoreCase) ? Place(real) : null;
    }

    /// <summary>Why a full path's place is not one to create in: Windows' own, the programs', elsewhere on the system drive.</summary>
    private static string? Place(string path)
    {
        var places = Current;
        foreach (string forbidden in places.Forbidden)
        {
            if (Within(path, forbidden))
            {
                return Loc.T("Pas dans les dossiers de Windows ni des programmes : dans vos dossiers (Bureau, Documents…) ou sur un autre disque.");
            }
        }

        string root = Path.GetPathRoot(path)!;
        string first = path[root.Length..].Split('\\')[0];
        if (first.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || first.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("Pas dans les dossiers de Windows ni des programmes : dans vos dossiers (Bureau, Documents…) ou sur un autre disque.");
        }

        if (root.Equals(places.SystemRoot, StringComparison.OrdinalIgnoreCase) && !Within(path, places.Profile))
        {
            return Loc.T("Sur le disque de Windows, seulement dans vos dossiers (Bureau, Documents…).");
        }

        return null;
    }

    /// <summary>Why a name of the path is not a folder's or a file's name Windows takes (« CON », « a:b », « fin. »).</summary>
    private static string? Unfit(string name)
    {
        if (name.Length == 0 || name.Length > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
        {
            return Loc.F("Nom impossible sous Windows : « {0} ».", name);
        }

        string stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        bool reserved = stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9');
        return reserved ? Loc.F("Nom réservé par Windows : « {0} ».", name) : null;
    }

    /// <summary><paramref name="path"/> where it really leads, its folders that are links followed; <c>null</c> if none is.</summary>
    private static string? Real(string path)
    {
        string root = Path.GetPathRoot(path)!;
        string real = root;
        bool linked = false;
        foreach (string name in path[root.Length..].Split('\\'))
        {
            real = Path.Combine(real, name);
            try
            {
                if (!Directory.Exists(real))
                {
                    continue; // To be created: nothing to follow from here on (each one after it is new too, or a plain folder).
                }

                if (Directory.ResolveLinkTarget(real, returnFinalTarget: true) is { } target)
                {
                    real = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
                    linked = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return path; // Not readable: judged as written.
            }
        }

        return linked ? real : null;
    }

    /// <summary><paramref name="path"/> is <paramref name="folder"/>, or inside it.</summary>
    private static bool Within(string path, string folder)
    {
        folder = folder.TrimEnd('\\');
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A text, as the user reads it before allowing it to be written: its first lines, cut if long.</summary>
    public static string Preview(string text)
    {
        const int MostLines = 10, MostCharacters = 500;
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var preview = new StringBuilder(string.Join('\n', lines.Take(MostLines)));
        bool cut = lines.Length > MostLines || preview.Length > MostCharacters;
        if (preview.Length > MostCharacters)
        {
            preview.Length = MostCharacters;
        }

        return cut ? preview.ToString().TrimEnd() + "\n…" : preview.ToString();
    }
}
