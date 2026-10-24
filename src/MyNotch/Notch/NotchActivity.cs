using Windows.UI;

namespace MyNotch.Notch;

/// <summary>
/// A live activity on the closed notch (an upload…): it stays at the right end of the pill until it is replaced or
/// ended, unlike an announcement which only shows for a moment.
/// </summary>
/// <param name="Glyph">Segoe Fluent Icons glyph.</param>
/// <param name="Text">A short text: "45 %", "En pause"…</param>
/// <param name="Progress">0 to 1 to show a progress ring around the glyph; <c>null</c> for none.</param>
/// <param name="Tint">Colour of the glyph and the ring; the primary text colour when <c>null</c>.</param>
public sealed record NotchActivity(string Glyph, string Text, double? Progress = null, Color? Tint = null);
