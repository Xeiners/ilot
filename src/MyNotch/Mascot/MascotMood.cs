namespace MyNotch.Mascot;

/// <summary>How the mascot looks, held until another mood replaces it (see <see cref="MascotPose"/>).</summary>
public enum MascotMood
{
    /// <summary>Awake, at rest: round eyes, a calm pebble.</summary>
    Neutral,

    /// <summary>Pleased (a copy, a boop): squinting eyes pushed up by its cheeks, a wider, lower body.</summary>
    Happy,

    /// <summary>Something came (a card, a question): big eyes, a body standing tall.</summary>
    Surprised,

    /// <summary>Asleep: eyes shut to lines, the body slumped.</summary>
    Sleepy,

    /// <summary>Music plays: a sound wave with eyes. The body is its middle bar, a narrow capsule beating to the bass.</summary>
    Groove,

    /// <summary>Thinking (the assistant, waiting for its answer): eyes narrowed and raised, the body a little taller.</summary>
    Thinking,

    /// <summary>Something went wrong: eyes lowered, their outer ends drooping, the body slumped.</summary>
    Sad,

    /// <summary>The assistant's orb (see MascotRig.Orb): a round body, the eyes a little closer, looking out of it.</summary>
    Orb,

    /// <summary>Reading (the Pomodoro's focus, see MascotRig.Book): eyes lowered to its book, a little narrowed, absorbed.</summary>
    Reading,

    /// <summary>Playing (the Pomodoro's break, see MascotRig.Pad): eyes wide on its gamepad, cheeks a little pink, having fun.</summary>
    Playing,
}

/// <summary>
/// A mood's numbers, in the mascot's own 100 × 100 box: the body (a rounded rectangle sitting on the box's floor, so
/// squashing it keeps it grounded) and the eyes (capsules, placed from the body's centre).
/// </summary>
/// <param name="BodyWidth">Body width.</param>
/// <param name="BodyHeight">Body height.</param>
/// <param name="BodyRadius">Corner radius, capped at half the smaller side (then it is a capsule).</param>
/// <param name="EyeWidth">Eye width.</param>
/// <param name="EyeHeight">Eye height.</param>
/// <param name="EyeSpacing">From the body's centre line to each eye's centre.</param>
/// <param name="EyeRise">From the body's centre to the eyes' (negative is up).</param>
/// <param name="EyeTilt">Each eye leans this many degrees, mirrored (the tops apart when positive).</param>
/// <param name="Blush">The cheeks, 0 (none) to 1.</param>
public readonly record struct MascotPose(
    double BodyWidth,
    double BodyHeight,
    double BodyRadius,
    double EyeWidth,
    double EyeHeight,
    double EyeSpacing,
    double EyeRise,
    double EyeTilt,
    double Blush)
{
    public static MascotPose For(MascotMood mood) => mood switch
    {
        MascotMood.Happy => new(90, 70, 34, 14, 7.5, 16, -7, 0, 1),
        MascotMood.Surprised => new(80, 82, 38, 14, 24, 16, -7, 0, 0),
        MascotMood.Sleepy => new(92, 66, 33, 13, 2.6, 15, 3, 0, 0.35),
        MascotMood.Groove => new(40, 60, 20, 10, 17, 9.5, -8, 0, 0),
        MascotMood.Thinking => new(84, 80, 36, 10, 15, 14, -9, -4, 0),
        MascotMood.Sad => new(90, 70, 34, 11, 11, 15, 2, -16, 0),
        MascotMood.Orb => new(82, 82, 41, 9, 16, 12, -2, 0, 0),
        MascotMood.Reading => new(86, 76, 36, 10, 13, 14, -3, 0, 0.25),
        MascotMood.Playing => new(88, 74, 36, 11, 15, 15, -5, 0, 0.35),
        _ => new(86, 76, 36, 10.5, 21, 15, -5, 6, 0),
    };
}
