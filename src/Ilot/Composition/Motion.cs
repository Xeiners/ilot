namespace Ilot.Composition;

/// <summary>
/// Motion design tokens. The whole feel of the notch is tuned from this file.
/// </summary>
public static class Motion
{
    // Shape morphs. Width leads height slightly so the notch "pours" open instead of scaling.
    public static readonly MorphSprings Expand = new(
        Width: new(0.50, 0.70), Height: new(0.56, 0.76), Radius: new(0.50, 0.90), Reveal: new(0.62, 0.92));

    public static readonly MorphSprings Collapse = new(
        Width: new(0.46, 0.86), Height: new(0.40, 0.92), Radius: new(0.42, 1.00), Reveal: new(0.24, 1.00));

    // Hover anticipation: a quick, slightly bouncy breath that says "I'm interactive".
    public static readonly MorphSprings Prime = new(
        Width: new(0.30, 0.56), Height: new(0.30, 0.60), Radius: new(0.30, 0.70), Reveal: new(0.30, 1.00));

    public static readonly MorphSprings Settle = new(
        Width: new(0.34, 0.66), Height: new(0.34, 0.72), Radius: new(0.34, 0.80), Reveal: new(0.30, 1.00));

    // First appearance: the notch drops out of the screen edge.
    public static readonly MorphSprings Arrive = new(
        Width: new(0.72, 0.62), Height: new(0.60, 0.70), Radius: new(0.60, 0.90), Reveal: new(0.40, 1.00));

    // Feedback.
    public static readonly Spring Bump = new(0.38, 0.42);
    public static readonly Spring Toggle = new(0.40, 0.64);
    public static readonly Spring Fade = new(0.42, 1.00);
    public static readonly Spring Swap = new(0.50, 0.86);
    public static readonly Spring Lyric = new(0.55, 0.88);
    public static readonly Spring Hover = new(0.28, 0.62);
    public static readonly Spring Press = new(0.16, 0.85);
    public static readonly Spring Release = new(0.36, 0.48);

    // Tabs: the content slides across, and the selection pill stretches toward the new tab (its leading edge
    // runs ahead of the trailing one) before it settles.
    public static readonly Spring Tab = new(0.46, 0.88);
    public static readonly Spring TabLead = new(0.30, 0.80);
    public static readonly Spring TabTrail = new(0.46, 0.84);

    // Lists: items glide to their new place when one is added, moved or removed; a new one pops in.
    public static readonly Spring Reflow = new(0.44, 0.86);
    public static readonly Spring Pop = new(0.42, 0.66);

    // A bubble under the notch: it falls like a drop (stretching, then bouncing once at rest), and is drawn back in.
    public static readonly Spring Drip = new(0.62, 0.58);
    public static readonly Spring Absorb = new(0.40, 0.92);

    // …and it stretches or gathers to what it holds (the search's results), with a soft give at the end.
    public static readonly Spring Resize = new(0.42, 0.80);

    // Search: the selection glides from row to row; a new result slides in.
    public static readonly Spring Select = new(0.28, 0.82);
    public static readonly Spring Arrival = new(0.36, 0.78);
}

/// <summary>The springs used by each channel of a shape morph.</summary>
public readonly record struct MorphSprings(Spring Width, Spring Height, Spring Radius, Spring Reveal);
