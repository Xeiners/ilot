namespace MyNotch.Modules.Media.Lyrics;

/// <summary>One line of synced lyrics: when it starts and what it says (empty for an instrumental break).</summary>
public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>Time-ordered lyric lines.</summary>
public sealed class SyncedLyrics(IReadOnlyList<LyricLine> lines)
{
    public IReadOnlyList<LyricLine> Lines { get; } = lines;

    /// <summary>Index of the line being sung at <paramref name="position"/>; -1 before the first line.</summary>
    public int IndexAt(TimeSpan position)
    {
        int low = 0;
        int high = Lines.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (Lines[middle].Time <= position)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }
}
