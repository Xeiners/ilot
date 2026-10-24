using Microsoft.UI.Composition;
using Ilot.Composition;

namespace Ilot.Notch;

/// <summary>
/// Animated state of the notch, shared by every visual that moves with it.
/// </summary>
/// <remarks>
/// Everything lives in one <see cref="CompositionPropertySet"/>, referenced as <c>n</c> in expressions:
/// <list type="bullet">
/// <item><c>n.Width</c>, <c>n.Height</c>, <c>n.Radius</c>: the morphing shape, in logical pixels.</item>
/// <item><c>n.Expansion</c>: content reveal, 0 = compact and 1 = expanded (it may overshoot);
/// <c>n.Progress</c> is the same value clamped to [0, 1].</item>
/// <item><c>n.Pin</c>: 1 while the notch is pinned open.</item>
/// <item><c>n.Toast</c>: 1 while the closed notch announces something (see <see cref="NotchContext.Announce"/>); compact
/// content at the right end of the pill steps aside for it.</item>
/// <item><c>n.Card</c>: 1 while the closed notch presents a card (see <see cref="NotchContext.Present"/>).</item>
/// <item><c>n.Bubble</c>: 1 while a bubble hangs under the open notch, detached from it like a drop (see
/// <see cref="NotchContext.ShowBubble"/>), 0 when it is back inside; <c>n.BubbleWidth</c>, <c>n.BubbleHeight</c>: its size,
/// <c>n.Gap</c>: the space between them; <c>n.BubbleRest</c>: where its top rests now, in the view.</item>
/// <item><c>n.Mini</c>: 1 while the closed notch gives way under the pointer: it is only its drag handle
/// (see <see cref="NotchLayout.Handle"/>), its compact content, shoulders and announcements hidden.</item>
/// <item><c>n.Extra</c>: extension of the shape to the right, beyond <c>n.Width</c>, where the announcement shows. The
/// left edge, and everything placed from <c>n.Width</c>, stays put.</item>
/// <item><c>n.Gauge</c>: 1 while the closed pill shows a level that changed (the volume), <c>n.GaugeLevel</c> that
/// level (0 to 1); modules' compact content and announcements step aside meanwhile.</item>
/// <item><c>n.Glow</c>: the assistant's glow inside the notch's edges and the drop's (0 to 1); <c>n.GlowTurn</c> and
/// <c>n.GlowPulse</c> turn its colours and make it breathe, only while it shows.</item>
/// <item><c>n.Nest</c>: 1 while the closed notch is the mascot's nest at rest: modules' compact content hidden.</item>
/// <item><c>n.Mascot</c>: 1 when the mascot is on (it takes the pill's right end, where a visualizer would ride);
/// <c>n.Away</c>: 1 while the module shown hosts it itself (the assistant, large in its drop): its pod steps aside;
/// <c>n.Hero</c>: 1 while it is a card's hero, at <c>n.HeroX</c>, <c>n.HeroY</c> (its top left) and <c>n.HeroScale</c>.</item>
/// <item><c>n.HostWidth</c>, <c>n.Inset</c>, <c>n.Flush</c>, <c>n.Shoulder</c>: layout constants.</item>
/// </list>
/// Visuals bind to these values with expressions, so the body, its clip and every piece of content move in
/// lockstep on the compositor thread. Modules use <see cref="Bind"/> to choreograph their own content.
/// </remarks>
public sealed class NotchMotion
{
    public NotchMotion(Compositor compositor, NotchLayout layout)
    {
        Compositor = compositor;
        Layout = layout;
        Properties = compositor.CreatePropertySet();
        Properties.InsertScalar("HostWidth", (float)layout.CanvasWidth);
        Properties.InsertScalar("Inset", (float)layout.TopInset);
        Properties.InsertScalar("Flush", layout.IsFlush ? 1f : 0f);
        Properties.InsertScalar("Shoulder", (float)Math.Max(layout.ShoulderRadius, 1));

        var hidden = layout.Hidden;
        Width = new SpringChannel(Properties, "Width", hidden.Width);
        Height = new SpringChannel(Properties, "Height", hidden.Height);
        Radius = new SpringChannel(Properties, "Radius", hidden.Radius);
        Expansion = new SpringChannel(Properties, "Expansion", 0);
        Pin = new SpringChannel(Properties, "Pin", 0);
        Toast = new SpringChannel(Properties, "Toast", 0);
        Extra = new SpringChannel(Properties, "Extra", 0);
        Card = new SpringChannel(Properties, "Card", 0);
        Bubble = new SpringChannel(Properties, "Bubble", 0);
        Mini = new SpringChannel(Properties, "Mini", 0);
        Nest = new SpringChannel(Properties, "Nest", 0);
        Gauge = new SpringChannel(Properties, "Gauge", 0);
        Glow = new SpringChannel(Properties, "Glow", 0);
        Properties.InsertScalar("GlowTurn", 0);
        Properties.InsertScalar("GlowPulse", 1);
        GaugeLevel = new SpringChannel(Properties, "GaugeLevel", 0);
        Properties.InsertScalar("Mascot", 0);
        Away = new SpringChannel(Properties, "Away", 0);
        Hero = new SpringChannel(Properties, "Hero", 0);
        Properties.InsertScalar("HeroX", 0);
        Properties.InsertScalar("HeroY", 0);
        Properties.InsertScalar("HeroScale", 1);
        Properties.InsertScalar("BubbleRest", 0);
        BubbleWidth = new SpringChannel(Properties, "BubbleWidth", 0);
        BubbleHeight = new SpringChannel(Properties, "BubbleHeight", 0);
        Properties.InsertScalar("Gap", (float)layout.BubbleGap);

        Properties.InsertScalar("Progress", 0);
        Bind("Clamp(n.Expansion, 0, 1)").Start(Properties, "Progress");
    }

    public Compositor Compositor { get; }

    public NotchLayout Layout { get; }

    public CompositionPropertySet Properties { get; }

    public SpringChannel Width { get; }

    public SpringChannel Height { get; }

    public SpringChannel Radius { get; }

    public SpringChannel Expansion { get; }

    public SpringChannel Pin { get; }

    public SpringChannel Toast { get; }

    public SpringChannel Extra { get; }

    public SpringChannel Card { get; }

    public SpringChannel Bubble { get; }

    /// <summary>The bubble's size: set at once when it comes, sprung when its module resizes it.</summary>
    public SpringChannel BubbleWidth { get; }

    /// <inheritdoc cref="BubbleWidth"/>
    public SpringChannel BubbleHeight { get; }

    public SpringChannel Mini { get; }

    public SpringChannel Nest { get; }

    public SpringChannel Gauge { get; }

    public SpringChannel GaugeLevel { get; }

    /// <summary>The assistant's glow inside the notch's edges (see NotchSurface.Shimmer), 0 to 1.</summary>
    public SpringChannel Glow { get; }

    /// <summary>The mascot is away from its pod: the module shown hosts it (see INotchModule.HostsMascot), 0 to 1.</summary>
    public SpringChannel Away { get; }

    /// <summary>The mascot is a card's hero (see NotchContext.Present): out of its place, large, in the card, 0 to 1.</summary>
    public SpringChannel Hero { get; }

    /// <summary>Creates an expression where <c>n</c> refers to the notch state.</summary>
    public CompositionExpression Bind(string expression) =>
        new CompositionExpression(Compositor, expression).With("n", Properties);

    /// <summary>Morphs the notch to <paramref name="shape"/>; returns the time the whole motion takes to settle.</summary>
    public TimeSpan MorphTo(NotchShape shape, double expansion, MorphSprings springs)
    {
        TimeSpan longest = Width.AnimateTo(shape.Width, springs.Width);
        longest = Max(longest, Height.AnimateTo(shape.Height, springs.Height));
        longest = Max(longest, Radius.AnimateTo(shape.Radius, springs.Radius));
        return Max(longest, Expansion.AnimateTo(expansion, springs.Reveal));
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
