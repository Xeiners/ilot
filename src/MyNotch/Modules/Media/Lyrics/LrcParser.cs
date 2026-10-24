using System.Globalization;
using System.Text.RegularExpressions;

namespace MyNotch.Modules.Media.Lyrics;

/// <summary>
/// Parses the LRC format: <c>[mm:ss.xx]text</c>, several time tags per line (repeated choruses),
/// <c>[offset:±ms]</c>, and enhanced LRC word tags (<c>&lt;mm:ss.xx&gt;</c>), which are dropped.
/// </summary>
public static partial class LrcParser
{
    public static SyncedLyrics? Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return null;
        }

        var lines = new List<LyricLine>();
        var offset = TimeSpan.Zero;
        foreach (string raw in lrc.Split('\n'))
        {
            string line = raw.Trim();
            var offsetTag = OffsetTag().Match(line);
            if (offsetTag.Success)
            {
                // A positive offset shows the lyrics earlier.
                offset = TimeSpan.FromMilliseconds(int.Parse(offsetTag.Groups[1].Value, CultureInfo.InvariantCulture));
                continue;
            }

            // Time tags must lead the line; metadata tags ([ar:], [ti:]…) never match.
            int textStart = 0;
            var times = new List<TimeSpan>();
            for (var tag = TimeTag().Match(line); tag.Success && tag.Index == textStart; tag = tag.NextMatch())
            {
                double seconds = double.Parse(tag.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                times.Add(TimeSpan.FromMinutes(int.Parse(tag.Groups[1].Value, CultureInfo.InvariantCulture)) + TimeSpan.FromSeconds(seconds));
                textStart = tag.Index + tag.Length;
            }

            string text = WordTag().Replace(line[textStart..], "").Trim();
            foreach (var time in times)
            {
                var shifted = time - offset;
                lines.Add(new LyricLine(shifted < TimeSpan.Zero ? TimeSpan.Zero : shifted, text));
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        return new SyncedLyrics(lines.OrderBy(line => line.Time).ToList());
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex WordTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();
}
