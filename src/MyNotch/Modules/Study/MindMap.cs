using System.Text;
using System.Text.Json.Serialization;

namespace MyNotch.Modules.Study;

/// <summary>
/// A notion of the course as a mind map, taken in at a glance: the notion at its centre with its definition (as the course
/// gives it, if it does), its formulas (if the course has some), a few branches around it (an aspect each: its structure,
/// its function…), each with a few details in short. As Notch writes it, and as the course keeps it (maps drawn before
/// the definitions and formulas have none).
/// </summary>
public sealed record MindMap(
    [property: JsonPropertyName("chapter")] string Chapter,
    [property: JsonPropertyName("notion")] string Notion,
    [property: JsonPropertyName("definition")] string Definition,
    [property: JsonPropertyName("formulas")] List<string> Formulas,
    [property: JsonPropertyName("branches")] List<MindBranch> Branches)
{
    /// <summary>Formulas on a map, at most.</summary>
    public const int MostFormulas = 4;

    /// <summary>Branches around a notion, at most: more would not be taken in at a glance.</summary>
    public const int MostBranches = 6;

    /// <summary>Details under a branch, at most.</summary>
    public const int MostDetails = 3;

    /// <summary>
    /// The whole course as one map: its title at the centre, its parts around it (in the course's order), the notions of
    /// each part under it.
    /// </summary>
    public static MindMap Overview(string title, IReadOnlyList<StudyChapter> chapters, IReadOnlyList<MindMap> maps)
    {
        var parts = chapters.Select(chapter => chapter.Title)
            .Concat(maps.Select(map => map.Chapter))
            .Where(part => part.Length > 0)
            .DistinctBy(Key)
            .ToList();
        var branches = parts
            .Select(part => new MindBranch(part, maps.Where(map => Key(map.Chapter) == Key(part)).Select(map => map.Notion).ToList()))
            .ToList();
        if (maps.Where(map => map.Chapter.Length == 0).Select(map => map.Notion).ToList() is { Count: > 0 } loose)
        {
            branches.Add(new MindBranch("…", loose)); // Notions out of any part (a course without one).
        }

        return new MindMap("", title, "", [], branches);
    }

    /// <summary>
    /// The maps as Notch wrote them, made clean: trimmed, no empty branch nor detail, no branch twice, at most
    /// <see cref="MostBranches"/> branches of <see cref="MostDetails"/> details and <see cref="MostFormulas"/> formulas (no
    /// LaTeX dollar); a map needs a notion and two branches, and a notion has one map only.
    /// </summary>
    public static List<MindMap> Tidy(IEnumerable<MindMap>? maps)
    {
        var seen = new HashSet<string>();
        return (maps ?? [])
            .Where(map => map is not null)
            .Select(map => new MindMap(
                (map.Chapter ?? "").Trim(),
                (map.Notion ?? "").Trim(),
                (map.Definition ?? "").Trim(),
                (map.Formulas ?? [])
                    .Select(formula => (formula ?? "").Trim().Trim('$').Trim())
                    .Where(formula => formula.Length > 0)
                    .Distinct()
                    .Take(MostFormulas)
                    .ToList(),
                (map.Branches ?? [])
                    .Where(branch => branch is not null)
                    .Select(branch => new MindBranch(
                        (branch.Label ?? "").Trim(),
                        (branch.Details ?? [])
                            .Select(detail => (detail ?? "").Trim().TrimEnd('.', ';', ','))
                            .Where(detail => detail.Length > 0)
                            .DistinctBy(Key)
                            .Take(MostDetails)
                            .ToList()))
                    .Where(branch => branch.Label.Length > 0)
                    .DistinctBy(branch => Key(branch.Label))
                    .Take(MostBranches)
                    .ToList()))
            .Where(map => map.Notion.Length > 0 && map.Branches.Count >= 2 && seen.Add(Key(map.Notion)))
            .ToList();
    }

    /// <summary>A text as compared with another: letters and digits only, no case, no accents.</summary>
    internal static string Key(string text)
    {
        var key = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                key.Append(char.ToLowerInvariant(c));
            }
        }

        return key.ToString();
    }
}

/// <summary>A branch of a mind map: an aspect of the notion, in a few words, and its details.</summary>
public sealed record MindBranch(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("details")] List<string> Details);

/// <summary>A box placed on a mind map: its top left corner and its size.</summary>
public readonly record struct MindBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Middle => Y + Height / 2;
}

/// <summary>
/// Where a mind map's parts go: its centre in the middle; its branches on both sides, the first ones on the right (top to
/// bottom), the others on the left (bottom to top), so it reads clockwise from the top right; as many on the right as
/// makes the map the least tall (a tall card, the formulas, leaves more on the left; the halves when it is the same);
/// each side stacked and centred on the centre, the left one flush towards it.
/// </summary>
public static class MindMapLayout
{
    /// <summary>From the centre to the branches (room for the curves).</summary>
    public const double Across = 48;

    /// <summary>Between two branches of a side.</summary>
    public const double Between = 14;

    /// <summary>The centre's box, each branch's box (in the branches' order), which side each is on, and the whole size.</summary>
    public sealed record Placed(MindBox Centre, IReadOnlyList<MindBox> Branches, IReadOnlyList<bool> OnRight, double Width, double Height);

    public static Placed Arrange((double Width, double Height) centre, IReadOnlyList<(double Width, double Height)> branches)
    {
        int count = branches.Count;
        int onRight = (count + 1) / 2;
        double least = Taller(onRight);
        for (int split = 1; split <= count; split++)
        {
            if (Taller(split) < least - 0.5)
            {
                least = Taller(split);
                onRight = split;
            }
        }

        var right = Enumerable.Range(0, onRight).ToList();
        var left = Enumerable.Range(onRight, count - onRight).Reverse().ToList();

        double leftWidth = left.Count == 0 ? 0 : left.Max(index => branches[index].Width);
        double rightWidth = right.Count == 0 ? 0 : right.Max(index => branches[index].Width);
        double height = Math.Max(centre.Height, Math.Max(Tall(right), Tall(left)));
        double centreX = left.Count == 0 ? 0 : leftWidth + Across;

        var boxes = new MindBox[count];
        var sides = new bool[count];
        Stack(right, index => centreX + centre.Width + Across, onTheRight: true);
        Stack(left, index => leftWidth - branches[index].Width, onTheRight: false);

        double width = centreX + centre.Width + (right.Count == 0 ? 0 : Across + rightWidth);
        return new Placed(new MindBox(centreX, (height - centre.Height) / 2, centre.Width, centre.Height), boxes, sides, width, height);

        double Tall(List<int> side) => side.Count == 0 ? 0 : side.Sum(index => branches[index].Height) + Between * (side.Count - 1);

        double Taller(int split) => Math.Max(
            Tall(Enumerable.Range(0, split).ToList()),
            Tall(Enumerable.Range(split, count - split).ToList()));

        void Stack(List<int> side, Func<int, double> x, bool onTheRight)
        {
            double y = (height - Tall(side)) / 2;
            foreach (int index in side)
            {
                boxes[index] = new MindBox(x(index), y, branches[index].Width, branches[index].Height);
                sides[index] = onTheRight;
                y += branches[index].Height + Between;
            }
        }
    }
}
