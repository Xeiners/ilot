using Ilot.Controls;

namespace Ilot.Modules.Clipboard;

/// <summary>What a clipboard entry can be found by (see <see cref="TextSearch"/> for how).</summary>
public static class ClipboardSearch
{
    public static bool Matches(ClipboardEntry entry, IReadOnlyList<string> words) => TextSearch.Matches(Haystack(entry), words);

    /// <summary>Its content, its kind, and what its tile shows.</summary>
    private static string Haystack(ClipboardEntry entry) => entry.Kind switch
    {
        ClipboardKind.Color => $"{entry.Text} {entry.Rgb} {entry.KindLabel}",
        ClipboardKind.Image => $"{entry.KindLabel} {entry.Headline}",
        _ => $"{entry.Text} {entry.KindLabel}",
    };
}
