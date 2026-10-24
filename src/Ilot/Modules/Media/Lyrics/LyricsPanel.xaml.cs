using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Ilot.Composition;
using Windows.Foundation;
using Ilot.Localization;

namespace Ilot.Modules.Media.Lyrics;

/// <summary>
/// Apple Music style lyrics: the line being sung large and bright, its neighbours smaller and dimmed around it.
/// </summary>
/// <remarks>
/// Each of the four recycled lines has a spring-driven "slot" (−2 leaving above, −1 previous, 0 sung, 1 next,
/// 2 waiting below) from which expressions derive its position, scale and opacity: moving to the next line just
/// shifts every slot by one. A line too long for the panel scrolls instead of being cut: it holds briefly, glides
/// to its end over the time the line is sung, and stops. Everything runs on the compositor.
/// </remarks>
public sealed partial class LyricsPanel : UserControl
{
    private const string Break = "• • •";
    private const string LineScale = "(1 - 0.2 * Clamp(Abs(s.Slot), 0, 1))";

    // The sung line at p.Sung (1; dimmed under its karaoke copy), neighbours at p.Level (42 % by default), or invisible
    // when the panel only shows the sung line.
    private const string LineOpacity =
        "Clamp(s.Slot >= 0"
        + " ? (s.Slot <= 1 ? p.Sung - (p.Sung - p.Level * p.Next) * s.Slot : p.Level * p.Next * (2 - s.Slot))"
        + " : (s.Slot >= -1 ? p.Sung + (p.Sung - p.Level * p.Prev) * s.Slot : p.Level * p.Prev * (2 + s.Slot)), 0, 1)";

    // Karaoke: the sung line's unsung part, under its bright copy; the copy shows on the sung line only.
    private const float KaraokeSung = 0.5f;
    private const string FillOpacity = "Clamp(1 - Abs(s.Slot), 0, 1) * p.Karaoke";
    private const double FillEndFraction = 0.9; // Filled a little before the next line starts.

    private static readonly TimeSpan ScrollReturn = TimeSpan.FromMilliseconds(450);

    // Scrolling: hold the start of the line, then glide so the end shows a little before the line is over.
    private const double ScrollHoldSeconds = 0.7;
    private const double ScrollEndFraction = 0.88;

    private readonly TextBlock[] _lines;
    private readonly TextBlock[] _fills;
    private readonly CompositionPropertySet[] _states;
    private readonly SpringChannel[] _slots;
    private readonly double[] _overflow;
    private readonly int[] _roles = [-1, 0, 1, 2];
    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _style;
    private readonly CompositionEasingFunction _linear;
    private readonly CompositionEasingFunction _glide;
    private SyncedLyrics? _lyrics;
    private int _index = int.MinValue;
    private bool _showsNextLine = true;
    private bool _showsPreviousLine;
    private double _lineSpacing = 22;
    private bool _flushRight;
    private bool _karaoke;

    public LyricsPanel()
    {
        InitializeComponent();
        Loc.Apply(this); // English, if so: its texts translated in place.
        _lines = [LineA, LineB, LineC, LineD];
        _fills = [FillA, FillB, FillC, FillD];
        _states = new CompositionPropertySet[_lines.Length];
        _slots = new SpringChannel[_lines.Length];
        _overflow = new double[_lines.Length];
        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _linear = _compositor.CreateLinearEasingFunction();
        _glide = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.55f, 1));
        _style = _compositor.CreatePropertySet();
        _style.InsertScalar("Next", 1);
        _style.InsertScalar("Prev", 0);
        _style.InsertScalar("Spacing", (float)_lineSpacing);
        _style.InsertScalar("Sung", 1);
        _style.InsertScalar("Level", 0.42f);
        _style.InsertScalar("Karaoke", 0);

        for (int i = 0; i < _lines.Length; i++)
        {
            var line = _lines[i];
            var state = _states[i] = _compositor.CreatePropertySet();
            state.InsertScalar("Left", 0);
            state.InsertScalar("Scroll", 0);
            state.InsertScalar("Fill", 0);
            state.InsertScalar("Width", 0);
            _slots[i] = new SpringChannel(state, "Slot", _roles[i]);

            ElementCompositionPreview.SetIsTranslationEnabled(line, true);
            var visual = ElementCompositionPreview.GetElementVisual(line);
            new CompositionExpression(_compositor, "Vector3(s.Left + s.Scroll, s.Slot * p.Spacing, 0)")
                .With("s", state).With("p", _style)
                .Start(visual, "Translation");
            new CompositionExpression(_compositor, $"Vector3({LineScale}, {LineScale}, 1)").With("s", state).Start(visual, "Scale");
            new CompositionExpression(_compositor, LineOpacity).With("s", state).With("p", _style).Start(visual, "Opacity");

            // Its karaoke copy: exactly over it, cut on the right where it is not sung yet.
            var fill = _fills[i];
            ElementCompositionPreview.SetIsTranslationEnabled(fill, true);
            var fillVisual = ElementCompositionPreview.GetElementVisual(fill);
            new CompositionExpression(_compositor, "Vector3(s.Left + s.Scroll, s.Slot * p.Spacing, 0)")
                .With("s", state).With("p", _style)
                .Start(fillVisual, "Translation");
            new CompositionExpression(_compositor, $"Vector3({LineScale}, {LineScale}, 1)").With("s", state).Start(fillVisual, "Scale");
            new CompositionExpression(_compositor, FillOpacity).With("s", state).With("p", _style).Start(fillVisual, "Opacity");
            var cut = _compositor.CreateInsetClip();
            new CompositionExpression(_compositor, "Max(s.Width * (1 - Clamp(s.Fill, 0, 1)), 0)").With("s", state).Start(cut, "RightInset");
            fillVisual.Clip = cut;

            int index = i;
            line.SizeChanged += (_, _) =>
            {
                state.InsertScalar("Width", (float)line.ActualWidth);
                Pivot(index);
            };
        }

        // Scrolling text must not run over the artwork: clip sideways, leave room above and below for the slots.
        SizeChanged += (_, _) =>
        {
            Stage.Clip = new RectangleGeometry { Rect = new Rect(0, -ActualHeight * 2, ActualWidth, ActualHeight * 5) };
            for (int i = 0; i < _lines.Length; i++)
            {
                Fit(i);
            }
        };
    }

    /// <summary>Show the next line, smaller and dimmed, under the sung one (default), or only the sung line.</summary>
    public bool ShowsNextLine
    {
        get => _showsNextLine;
        set
        {
            _showsNextLine = value;
            _style.InsertScalar("Next", value ? 1 : 0);
        }
    }

    /// <summary>Keep the previous line, smaller and dimmed, above the sung one (off by default).</summary>
    public bool ShowsPreviousLine
    {
        get => _showsPreviousLine;
        set
        {
            _showsPreviousLine = value;
            _style.InsertScalar("Prev", value ? 1 : 0);
        }
    }

    /// <summary>The next (and previous) line's opacity, from 0 to 1 (0.42 by default).</summary>
    public double NeighbourOpacity
    {
        set => _style.InsertScalar("Level", (float)Math.Clamp(value, 0, 1));
    }

    /// <summary>
    /// Karaoke (the taskbar's lines): the sung line dimmed, brightening from the left over the time it is sung (lyrics
    /// timed by the line: evenly along it). Off by default.
    /// </summary>
    public bool Karaoke
    {
        get => _karaoke;
        set
        {
            _karaoke = value;
            _style.InsertScalar("Karaoke", value ? 1 : 0);
            _style.InsertScalar("Sung", value ? KaraokeSung : 1);
            foreach (var fill in _fills)
            {
                fill.Visibility = value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            }
        }
    }

    /// <summary>Vertical distance between two lines, in pixels.</summary>
    public double LineSpacing
    {
        get => _lineSpacing;
        set
        {
            _lineSpacing = value;
            _style.InsertScalar("Spacing", (float)value);
        }
    }

    /// <summary>
    /// Lines that fit set flush right (against the taskbar's icons) rather than centred; the dimmed ones shrink towards
    /// that edge, so all stay aligned on it.
    /// </summary>
    public bool FlushRight
    {
        get => _flushRight;
        set
        {
            if (value == _flushRight)
            {
                return;
            }

            _flushRight = value;
            for (int i = 0; i < _lines.Length; i++)
            {
                Fit(i);
                Pivot(i);
            }
        }
    }

    /// <summary>The lines' colour: white by default (on the notch), dark on a light taskbar.</summary>
    public Brush LineForeground
    {
        get => LineA.Foreground;
        set
        {
            foreach (var line in _lines.Concat(_fills))
            {
                line.Foreground = value;
            }
        }
    }

    public double LineFontSize
    {
        get => LineA.FontSize;
        set
        {
            for (int i = 0; i < _lines.Length; i++)
            {
                _lines[i].FontSize = value;
                _fills[i].FontSize = value;
                Fit(i);
            }
        }
    }

    /// <summary>Shows a new set of lyrics (or none); the next <see cref="Sync"/> brings the first line in.</summary>
    public void SetLyrics(SyncedLyrics? lyrics)
    {
        _lyrics = lyrics;
        _index = int.MinValue;
        for (int i = 0; i < _lines.Length; i++)
        {
            SetText(i, "");
        }
    }

    /// <summary>Moves to the sung line and (re)starts its scrolling from where playback stands.</summary>
    public void Sync(LyricsPosition position)
    {
        if (_lyrics is null)
        {
            return;
        }

        if (position.Index != _index)
        {
            MoveTo(position.Index);
        }

        int sung = Array.IndexOf(_roles, 0);
        Scroll(sung, position);
        if (_karaoke)
        {
            Sing(sung, position);
        }
    }

    /// <summary>
    /// The sung line leaves upward, the next one rises into its place, the spare one brings the line after.
    /// On a jump (seek) the rising line simply takes the new text first.
    /// </summary>
    private void MoveTo(int index)
    {
        bool sequential = index == _index + 1;
        _index = index;
        for (int i = 0; i < _lines.Length; i++)
        {
            switch (_roles[i])
            {
                case -1:
                    Animate(i, -2);
                    break;

                case 0:
                    // Becomes the previous line: back to its start, so it reads from the beginning again.
                    if (!sequential)
                    {
                        SetText(i, TextOf(index - 1));
                    }

                    ReturnScroll(i);
                    Sang(i);
                    Animate(i, -1);
                    break;

                case 1:
                    if (!sequential)
                    {
                        SetText(i, TextOf(index));
                    }

                    Animate(i, 0);
                    break;

                default:
                    SetText(i, TextOf(index + 1));
                    _slots[i].Snap(2);
                    Animate(i, 1);
                    break;
            }
        }
    }

    private void Scroll(int line, LyricsPosition position)
    {
        var state = _states[line];
        state.StopAnimation("Scroll");
        double overflow = _overflow[line];
        double length = position.LineLength.TotalSeconds;
        if (overflow <= 0 || length <= 0)
        {
            state.InsertScalar("Scroll", 0);
            return;
        }

        // Hold, glide to the end, hold: a short line glides faster, never past the moment the next one starts.
        double end = length * ScrollEndFraction;
        double start = Math.Min(ScrollHoldSeconds, end * 0.25);
        double now = Math.Clamp(position.IntoLine.TotalSeconds, 0, length);
        double progress = now <= start ? 0 : now >= end ? 1 : (now - start) / (end - start);
        state.InsertScalar("Scroll", (float)(-overflow * progress));
        if (!position.IsPlaying || now >= end)
        {
            return;
        }

        var glide = _compositor.CreateScalarKeyFrameAnimation();
        double remaining = end - now;
        if (now < start)
        {
            glide.InsertKeyFrame((float)((start - now) / remaining), 0, _linear);
            glide.InsertKeyFrame(1, (float)-overflow, _glide);
        }
        else
        {
            glide.InsertKeyFrame(1, (float)-overflow, _linear);
        }

        glide.Duration = TimeSpan.FromSeconds(remaining);
        state.StartAnimation("Scroll", glide);
    }

    /// <summary>
    /// Karaoke: the sung line brightens from the left, evenly over the time it is sung (filled a little before the next
    /// line starts), on the compositor; paused, it stays where it is.
    /// </summary>
    private void Sing(int line, LyricsPosition position)
    {
        var state = _states[line];
        state.StopAnimation("Fill");
        double end = position.LineLength.TotalSeconds * FillEndFraction;
        if (end <= 0)
        {
            state.InsertScalar("Fill", 1); // Its length unknown (the last line): bright at once.
            return;
        }

        double now = Math.Clamp(position.IntoLine.TotalSeconds, 0, end);
        state.InsertScalar("Fill", (float)(now / end));
        if (!position.IsPlaying || now >= end)
        {
            return;
        }

        var fill = _compositor.CreateScalarKeyFrameAnimation();
        fill.InsertKeyFrame(1, 1, _linear);
        fill.Duration = TimeSpan.FromSeconds(end - now);
        state.StartAnimation("Fill", fill);
    }

    /// <summary>The line just sung, all of it bright as it goes.</summary>
    private void Sang(int line)
    {
        _states[line].StopAnimation("Fill");
        _states[line].InsertScalar("Fill", 1);
    }

    private void ReturnScroll(int line)
    {
        var state = _states[line];
        state.StopAnimation("Scroll");
        var back = _compositor.CreateScalarKeyFrameAnimation();
        back.InsertKeyFrame(1, 0, _glide);
        back.Duration = ScrollReturn;
        state.StartAnimation("Scroll", back);
    }

    private void SetText(int line, string text)
    {
        _lines[line].Text = text;
        _fills[line].Text = text;
        _states[line].StopAnimation("Scroll");
        _states[line].InsertScalar("Scroll", 0);
        _states[line].StopAnimation("Fill");
        _states[line].InsertScalar("Fill", 0);
        Fit(line);
    }

    /// <summary>
    /// Centres a line that fits (or sets it flush right, see <see cref="FlushRight"/>); a longer one starts flush left and
    /// gets an amount to scroll.
    /// </summary>
    private void Fit(int line)
    {
        var text = _lines[line];
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = text.DesiredSize.Width;
        double room = ActualWidth;
        _overflow[line] = Math.Max(width - room, 0);
        _states[line].InsertScalar("Left", (float)Math.Max(_flushRight ? room - width : (room - width) / 2, 0));
    }

    /// <summary>Where a line shrinks towards when dimmed: its middle, or its right end when the lines are flush right.</summary>
    private void Pivot(int line)
    {
        var text = _lines[line];
        var center = new Vector3((float)(_flushRight ? text.ActualWidth : text.ActualWidth / 2), (float)text.ActualHeight / 2, 0);
        ElementCompositionPreview.GetElementVisual(text).CenterPoint = center;
        ElementCompositionPreview.GetElementVisual(_fills[line]).CenterPoint = center;
    }

    private void Animate(int line, int slot)
    {
        _roles[line] = slot;
        _slots[line].AnimateTo(slot, Motion.Lyric);
    }

    private string TextOf(int index)
    {
        // −1 is the intro before the first line (shown as a break); nothing exists before it or after the end.
        if (_lyrics is null || index < -1 || index >= _lyrics.Lines.Count)
        {
            return "";
        }

        string text = index < 0 ? "" : _lyrics.Lines[index].Text;
        return string.IsNullOrWhiteSpace(text) ? Break : text;
    }
}
