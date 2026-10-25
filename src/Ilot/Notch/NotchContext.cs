using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Ilot.Composition;
using Windows.UI;

namespace Ilot.Notch;

/// <summary>
/// What a module gets from the notch: the shared motion state, its tab selection, whether the notch is open, and
/// ways to keep it open, reshape it or make it react.
/// </summary>
public sealed class NotchContext
{
    /// <summary>
    /// Opacity of the module's open content for its tab selection (<c>t</c>): the outgoing tab fades out before the
    /// incoming one fades in, so both never blur into each other.
    /// </summary>
    public const string TabOpacity = "Clamp(t.Selected * 1.6 - 0.4, 0, 1)";

    /// <summary>
    /// Set (to anything) in the data of drags that start inside the notch: dropping them back onto the notch means
    /// nothing, so no module takes them.
    /// </summary>
    public const string InternalDragProperty = "Ilot.InternalDrag";

    /// <summary>Horizontal slide of the module's open content for its tab selection: away to its side, home at 0.</summary>
    public const string TabOffset = "(1 - t.Selected) * t.Direction * 40";

    private readonly NotchController _controller;
    private readonly Action<double> _setCompactWidth;
    private readonly Action<CompositionPropertySet?> _shareBeat;
    private readonly Action<string, string, double, bool> _showLevel;
    private readonly Action<MascotAct> _actMascot;
    private readonly Action<float, float> _voiceMascot;
    private readonly Action<bool> _mascotReads;
    private readonly Action<bool> _mascotPlays;
    private readonly Action<NotchShape?> _setExpandedShape;
    private readonly Action _reshape;
    private readonly Action<NotchActivity> _announce;
    private readonly Func<Action, IDisposable> _takeKeyboard;
    private readonly Action<NotchActivity?> _setActivity;
    private readonly Action<NotchPresence> _setPresence;
    private readonly Action<INotchDropTarget> _acceptDrops;
    private readonly Action _show;
    private readonly Func<FrameworkElement, Windows.Foundation.Size, TimeSpan, bool, bool> _present;
    private readonly Func<FrameworkElement, Windows.Foundation.Size, IDisposable> _ask;
    private readonly Func<FrameworkElement, Windows.Foundation.Size, Windows.Foundation.Size?, Windows.Foundation.Size?, NotchBubble> _showBubble;
    private readonly Action<bool> _hideTabs;
    private readonly SpringChannel _selected;

    internal NotchContext(
        NotchMotion motion,
        NotchController controller,
        double contentTop,
        bool selected,
        Action<double> setCompactWidth,
        Action<CompositionPropertySet?> shareBeat,
        Action<string, string, double, bool> showLevel,
        Action<MascotAct> actMascot,
        Action<float, float> voiceMascot,
        Action<bool> mascotReads,
        Action<bool> mascotPlays,
        Action<NotchShape?> setExpandedShape,
        Action reshape,
        Action<NotchActivity> announce,
        Func<Action, IDisposable> takeKeyboard,
        Action<NotchActivity?> setActivity,
        Action<NotchPresence> setPresence,
        Action<INotchDropTarget> acceptDrops,
        Action show,
        Func<FrameworkElement, Windows.Foundation.Size, TimeSpan, bool, bool> present,
        Func<FrameworkElement, Windows.Foundation.Size, IDisposable> ask,
        Func<FrameworkElement, Windows.Foundation.Size, Windows.Foundation.Size?, Windows.Foundation.Size?, NotchBubble> showBubble,
        Action<bool> hideTabs)
    {
        Motion = motion;
        ContentTop = contentTop;
        IsSelected = selected;
        _controller = controller;
        _setCompactWidth = setCompactWidth;
        _shareBeat = shareBeat;
        _showLevel = showLevel;
        _actMascot = actMascot;
        _voiceMascot = voiceMascot;
        _mascotReads = mascotReads;
        _mascotPlays = mascotPlays;
        _setExpandedShape = setExpandedShape;
        _reshape = reshape;
        _announce = announce;
        _takeKeyboard = takeKeyboard;
        _setActivity = setActivity;
        _setPresence = setPresence;
        _acceptDrops = acceptDrops;
        _show = show;
        _present = present;
        _ask = ask;
        _showBubble = showBubble;
        _hideTabs = hideTabs;

        Selection = motion.Compositor.CreatePropertySet();
        Selection.InsertScalar("Direction", 1);
        _selected = new SpringChannel(Selection, "Selected", selected ? 1 : 0);

        _controller.PhaseChanged += (_, _) =>
        {
            bool expanded = _controller.Phase == NotchPhase.Expanded;
            if (expanded != IsExpanded)
            {
                IsExpanded = expanded;
                ExpandedChanged?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public NotchMotion Motion { get; }

    /// <summary>Top of the module's open content, in the view: the tab bar sits above it.</summary>
    public double ContentTop { get; }

    /// <summary>
    /// Tab selection, animated on the compositor and referenced as <c>t</c> by <see cref="Bind"/>: <c>Selected</c>
    /// springs to 1 when the module's tab is chosen and back to 0 when another one is; <c>Direction</c> (±1) is the
    /// side its content leaves to (and comes back from). See <see cref="TabOpacity"/> and <see cref="TabOffset"/>.
    /// </summary>
    public CompositionPropertySet Selection { get; }

    /// <summary>Whether the module's tab is the one shown when the notch is open.</summary>
    public bool IsSelected { get; private set; }

    public bool IsExpanded { get; private set; }

    /// <summary>The module's content is on screen: notch open on its tab.</summary>
    public bool IsVisible => IsExpanded && IsSelected;

    /// <summary>Raised when the module's tab is selected or deselected.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when the notch opens or closes; a module can start or stop its live updates here.</summary>
    public event EventHandler? ExpandedChanged;

    /// <summary>
    /// The mouse wheel turned over the notch while it shows this module (or over the closed pill, for the first
    /// module, which owns it): the delta in wheel units, 120 a notch, positive away from the user. Wheels already used
    /// by the module's own content (a row scrolling) do not come here.
    /// </summary>
    public event EventHandler<int>? Wheel;

    /// <summary>Creates an expression where <c>n</c> is the notch state and <c>t</c> the module's tab selection.</summary>
    public CompositionExpression Bind(string expression) => Motion.Bind(expression).With("t", Selection);

    /// <summary>
    /// A moment's card, the way the Dynamic Island grows for an event (a device connected…): the closed pill widens if
    /// needed and grows down to hold <paramref name="card"/> (laid out at <paramref name="size"/>) under its own top
    /// row, then shrinks back after <paramref name="duration"/>. The card binds its motion to <c>n.Card</c> (0 → 1).
    /// Hovering opens the notch as usual, and the card gives way. Not shown, and <c>false</c>, while the notch is open.
    /// </summary>
    /// <param name="withMascot">
    /// The mascot comes out of its place into the card, large, at its left (leave it <see cref="HeroRoom"/> there), and
    /// goes back after: something it has to say itself (a Pomodoro's end…).
    /// </param>
    public bool Present(FrameworkElement card, Windows.Foundation.Size size, TimeSpan duration, bool withMascot = false) =>
        _present(card, size, duration, withMascot);

    /// <summary>The room the mascot takes at the left of a card it comes into (see <see cref="Present"/>).</summary>
    public const double HeroRoom = 84;

    /// <summary>
    /// A question on the closed notch, the way the Dynamic Island shows an incoming call: a card like
    /// <see cref="Present"/>'s, but it takes clicks (its buttons answer) and stays until the returned token is disposed.
    /// Hovering it does not open the notch, a click on it does. While the notch is open, or another card shows, it
    /// waits, and comes back after. One question at a time: asking again replaces it.
    /// </summary>
    public IDisposable Ask(FrameworkElement card, Windows.Foundation.Size size) => _ask(card, size);

    /// <summary>
    /// A bubble under the open notch, the way the Dynamic Island splits in two: it falls from the notch like a drop,
    /// stretching and letting go, and rests a little below it (<see cref="NotchLayout.BubbleGap"/>), centred, with
    /// <paramref name="content"/> (laid out at <paramref name="size"/>) inside. Disposing the returned token draws it
    /// back in; so does the notch closing. It takes the mouse like the notch (crossing the gap is not leaving). With a
    /// <paramref name="minimum"/> size, the user may pull its bottom corners to resize it, up to <paramref name="maximum"/>
    /// (<see cref="NotchBubble.Resized"/> tells the new size). It fits in <see cref="NotchLayout.ExpandedMax"/>, under the
    /// open notch. One at a time.
    /// </summary>
    public NotchBubble ShowBubble(
        FrameworkElement content, Windows.Foundation.Size size, Windows.Foundation.Size? minimum = null, Windows.Foundation.Size? maximum = null) =>
        _showBubble(content, size, minimum, maximum);

    /// <summary>
    /// The module needs the open notch's top row (its search field, opened over the tabs): the tabs step aside while it
    /// shows, and come back with <c>false</c>.
    /// </summary>
    public void HideTabs(bool hidden) => _hideTabs(hidden);

    /// <summary>Opens the notch on this module's tab (a shortcut…).</summary>
    public void Show() => _show();

    /// <summary>
    /// Closes the notch now, without the grace period hovering gets (the module is done: a search given up…), unless it
    /// is pinned or something holds it open. Hovering does not reopen it until the pointer has left.
    /// </summary>
    public void Close() => _controller.Dismiss();

    /// <summary>Keeps the notch as it is (typically open) until the returned token is disposed.</summary>
    public IDisposable HoldOpen() => _controller.Hold();

    /// <summary>
    /// Lets the module type (a search field…): the notch never takes the keyboard on its own, so the app in use keeps
    /// it. Until the returned token is disposed, the notch has the keyboard and stays open; disposing it hands the
    /// keyboard back to the app that had it, ready for Ctrl+V. If the user clicks elsewhere in the meantime,
    /// <paramref name="lost"/> is called and the token is void.
    /// </summary>
    public IDisposable TakeKeyboard(Action lost) => _takeKeyboard(lost);

    /// <summary>
    /// Ends a lease from <see cref="TakeKeyboard"/> because something was just opened (an app, a file…): the keyboard is
    /// left to it, rather than handed back to the app that had it (which would come over what just opened).
    /// </summary>
    public static void HandOffKeyboard(IDisposable lease)
    {
        if (lease is IKeyboardLease keyboard)
        {
            keyboard.HandOff();
        }
        else
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Sizes the compact pill to <paramref name="width"/>: wider (the sung line), or narrower (just what it shows), or
    /// back to its own with <c>null</c>. The widest request of the modules wins; the morph to it is animated.
    /// </summary>
    public void RequestCompactWidth(double? width) => _setCompactWidth(width ?? 0);

    /// <summary>
    /// Lends the mascot a beat to dance to while music plays: a property set holding the scalars <c>Bass</c> and
    /// <c>L0</c> to <c>L3</c> (0 to 1: the bass, and a few levels of the spectrum, kept up to date by the module);
    /// <c>null</c> when the music stops.
    /// </summary>
    public void ShareBeat(CompositionPropertySet? beat) => _shareBeat(beat);

    /// <summary>
    /// A level that just changed (the system volume…), on the closed pill for a moment: <paramref name="glyph"/> on the
    /// left, a track, and the mascot as its cursor at <paramref name="level"/> (0 to 1); without the mascot, an
    /// announcement of <paramref name="text"/> ringed by the level. Ignored while open (the module shows it itself).
    /// </summary>
    public void ShowLevel(string glyph, string text, double level, bool muted) => _showLevel(glyph, text, level, muted);

    /// <summary>Has the mascot act something out (the assistant's conversation: listening, thinking…); nothing without it.</summary>
    public void ActMascot(MascotAct act) => _actMascot(act);

    /// <summary>
    /// Talking aloud with the assistant: how loud the user (<paramref name="microphone"/>) and its voice
    /// (<paramref name="voice"/>) are now (0 to 1): the mascot nods to the one and talks with the other, wherever it is.
    /// </summary>
    public void VoiceMascot(float microphone, float voice) => _voiceMascot(microphone, voice);

    /// <summary>
    /// The mascot reads (a focus going on, the Pomodoro): its book in its hands, wherever it is, until told to stop.
    /// Nothing without the mascot.
    /// </summary>
    public void MascotReads(bool reading) => _mascotReads(reading);

    /// <summary>
    /// The mascot plays (a break going on, the Pomodoro): its gamepad in its hands, wherever it is, until told to stop.
    /// Nothing without the mascot.
    /// </summary>
    public void MascotPlays(bool playing) => _mascotPlays(playing);

    /// <summary>
    /// Enlarges the open notch to <paramref name="shape"/> (within <see cref="NotchLayout.ExpandedMax"/>), or
    /// restores it with <c>null</c>. The module's view is resized to match; the shape springs to it.
    /// </summary>
    public void RequestExpandedShape(NotchShape? shape) => _setExpandedShape(shape);

    /// <summary>
    /// The module's own open shape (<see cref="INotchModule.OpenShape"/>) changed: the open notch springs to it if it
    /// shows the module, and a bubble under it follows.
    /// </summary>
    public void Reshape() => _reshape();

    /// <summary>
    /// A brief live activity on the closed notch, to acknowledge something (a copy…): the pill grows to the right
    /// and shows <paramref name="glyph"/> (Segoe Fluent Icons, in <paramref name="tint"/> if given) and a short
    /// <paramref name="message"/> for a moment, at its right end (compact content there steps aside with
    /// <c>n.Toast</c>). Ignored while open. With <paramref name="progress"/> (0 to 1), the glyph is ringed by it (a
    /// volume level…); such an announcement updates in place, at each call.
    /// </summary>
    public void Announce(string glyph, string message, Color? tint = null, double? progress = null) =>
        _announce(new NotchActivity(glyph, message, progress, tint));

    /// <summary>
    /// Shows a live activity on the closed notch (an upload…), updates it, or ends it with <c>null</c>. Announcements
    /// still show over it for their moment.
    /// </summary>
    public void SetActivity(NotchActivity? activity) => _setActivity(activity);

    /// <summary>
    /// What the module has for the closed notch: something going on (music playing: the pill stays), something shown but
    /// idle (a track paused: the notch shrinks to its handle after a while, see the settings), or nothing (the handle at
    /// once). Live activities count as going on. Announcements, cards and questions still bring the pill out.
    /// </summary>
    public void SetPresence(NotchPresence presence) => _setPresence(presence);

    /// <summary>Takes what is dragged onto the notch, when <paramref name="target"/> accepts it.</summary>
    public void AcceptDrops(INotchDropTarget target) => _acceptDrops(target);

    /// <param name="selected">Whether this module's tab becomes the shown one.</param>
    /// <param name="side">Where the shown tab is not: the side this module's content leaves to, when deselected.</param>
    /// <summary>Hands a wheel turn to the module; <c>false</c> if it does not listen to the wheel.</summary>
    internal bool RaiseWheel(int delta)
    {
        if (Wheel is null)
        {
            return false;
        }

        Wheel(this, delta);
        return true;
    }

    internal void Select(bool selected, int side, bool animate)
    {
        if (!selected)
        {
            // Changing sides only while the content is home or away: never mid-flight on screen.
            Selection.InsertScalar("Direction", side);
        }

        if (animate)
        {
            _selected.AnimateTo(selected ? 1 : 0, Composition.Motion.Tab);
        }
        else
        {
            _selected.Snap(selected ? 1 : 0);
        }

        if (selected != IsSelected)
        {
            IsSelected = selected;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
