using System.Text.Json.Serialization;

namespace MyNotch.Modules.Study;

/// <summary>
/// What the user drew and wrote on a mind map (see MindMapCanvas): strokes of the pen or the highlighter, and notes typed
/// where they clicked; in the map's own space (from its top left corner), so they stay where they were put whatever the
/// zoom. Kept with the course, one per map (see <see cref="StudyDeck.MapInks"/>).
/// </summary>
public sealed class MapInk
{
    [JsonPropertyName("strokes")]
    public List<InkStroke> Strokes { get; init; } = [];

    [JsonPropertyName("notes")]
    public List<InkNote> Notes { get; init; } = [];

    [JsonIgnore]
    public bool IsEmpty => Strokes.Count == 0 && Notes.Count == 0;

    /// <summary>The key a map's ink is kept under: its notion's, or the whole course's.</summary>
    public static string KeyOf(MindMap map, bool overview) => overview ? "overview" : "notion:" + MindMap.Key(map.Notion);
}

/// <summary>A stroke: its colour (« #FFFBD53F »), whether it is the highlighter's (wide, see-through), its points (x, y, x, y…).</summary>
public sealed record InkStroke(
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("marker")] bool Marker,
    [property: JsonPropertyName("points")] List<double> Points);

/// <summary>A note typed on the map: where (its top left corner), what, in which colour.</summary>
public sealed class InkNote
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#FFFFFFFF";
}
