using Microsoft.UI.Dispatching;

namespace MyNotch.Notch;

public enum NotchPhase
{
    /// <summary>The compact pill.</summary>
    Collapsed,

    /// <summary>The pointer rests on the pill: it swells slightly while hover intent is confirmed.</summary>
    Primed,

    /// <summary>Fully open, showing the module content.</summary>
    Expanded,
}

/// <summary>
/// Interaction state machine of the notch: hover intent, delayed collapse and the double-click pin.
/// It knows nothing about rendering; the view reacts to <see cref="PhaseChanged"/> and <see cref="PinnedChanged"/>.
/// </summary>
public sealed class NotchController
{
    /// <summary>Hover time before opening, so sweeping the mouse across the top of the screen does nothing.</summary>
    public static readonly TimeSpan HoverIntentDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>Grace period before closing, so briefly overshooting the edge does not slam the notch shut.</summary>
    public static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(320);

    private readonly DispatcherQueueTimer _intentTimer;
    private readonly DispatcherQueueTimer _collapseTimer;
    private bool _pointerInside;
    private int _holds;
    private bool _engaged;

    // Set when the user explicitly closes the notch while hovering it: hover must not reopen it
    // until the pointer has left.
    private bool _hoverSuppressed;

    public NotchController(DispatcherQueue dispatcher)
    {
        _intentTimer = CreateTimer(dispatcher, HoverIntentDelay, OnHoverIntentConfirmed);
        _collapseTimer = CreateTimer(dispatcher, CollapseDelay, OnCollapseDelayElapsed);
    }

    public NotchPhase Phase { get; private set; } = NotchPhase.Collapsed;

    public bool IsPinned { get; private set; }

    /// <summary>The user acted in the open notch (see <see cref="Engage"/>): it stays open until a click outside.</summary>
    public bool IsEngaged => _engaged;

    public event EventHandler? PhaseChanged;

    public event EventHandler? PinnedChanged;

    public event EventHandler? EngagedChanged;

    public void OnPointerEntered()
    {
        _pointerInside = true;
        _collapseTimer.Stop();
        if (_hoverSuppressed || Phase != NotchPhase.Collapsed)
        {
            return;
        }

        SetPhase(NotchPhase.Primed);
        Restart(_intentTimer);
    }

    public void OnPointerExited()
    {
        _pointerInside = false;
        _hoverSuppressed = false;
        _intentTimer.Stop();
        if (_holds == 0)
        {
            CollapseUnlessPinned();
        }
    }

    /// <summary>
    /// Freezes the notch as it is until the returned token is disposed: while its context menu is open, while the
    /// user drags a slider beyond its edge… Holds stack up.
    /// </summary>
    public IDisposable Hold()
    {
        if (_holds++ == 0)
        {
            _intentTimer.Stop();
            _collapseTimer.Stop();
        }

        return new HoldToken(this);
    }

    /// <summary>A click opens the notch right away (touch, or users who don't wait for hover).</summary>
    public void OnTapped()
    {
        _hoverSuppressed = false;
        if (Phase != NotchPhase.Expanded)
        {
            Expand();
        }
    }

    /// <summary>Opens at once, whatever the pointer does (something is dragged onto the notch…).</summary>
    public void Open()
    {
        _hoverSuppressed = false;
        _intentTimer.Stop();
        _collapseTimer.Stop();
        if (Phase != NotchPhase.Expanded)
        {
            Expand();
        }
    }

    /// <summary>
    /// Closes at once, without the grace period hovering gets (a search given up, something opened from it…): unless
    /// pinned, or held by something else. Hovering does not open it again until the pointer has left.
    /// </summary>
    public void Dismiss()
    {
        if (IsPinned || _holds > 0 || Phase == NotchPhase.Collapsed)
        {
            return;
        }

        _hoverSuppressed = _pointerInside;
        Collapse();
    }

    /// <summary>
    /// The user acted in the open notch (a click, a tap): it no longer closes as the pointer leaves, only at a click outside
    /// (see <see cref="Disengage"/>), when a module closes it, or when it is put away. Not while pinned: it stays anyway.
    /// </summary>
    public void Engage()
    {
        if (!_engaged && !IsPinned && Phase == NotchPhase.Expanded)
        {
            _collapseTimer.Stop();
            SetEngaged(true);
        }
    }

    /// <summary>A click outside: the notch closes now (unless pinned; something holding it closes it when it lets go).</summary>
    public void Disengage()
    {
        if (!_engaged)
        {
            return;
        }

        SetEngaged(false);
        if (_holds == 0 && !IsPinned)
        {
            _hoverSuppressed = _pointerInside;
            Collapse();
        }
    }

    /// <summary>Double-click pins the notch open; another double-click closes it.</summary>
    public void OnDoubleTapped() => SetPinned(!IsPinned);

    /// <summary>
    /// Back to rest without any event (the notch is being put away, e.g. while an app is full screen):
    /// collapsed, not pinned, no pending timer. Only an engagement that ends is told (its watching stops).
    /// </summary>
    public void Reset()
    {
        _intentTimer.Stop();
        _collapseTimer.Stop();
        _pointerInside = false;
        _hoverSuppressed = false;
        Phase = NotchPhase.Collapsed;
        IsPinned = false;
        SetEngaged(false);
    }

    public void SetPinned(bool pinned)
    {
        if (pinned == IsPinned)
        {
            return;
        }

        IsPinned = pinned;
        if (pinned)
        {
            SetEngaged(false); // Pinned, it stays anyway.
            Expand();
        }
        else
        {
            _hoverSuppressed = _pointerInside;
            Collapse();
        }

        PinnedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnHoverIntentConfirmed()
    {
        if (_pointerInside && Phase == NotchPhase.Primed)
        {
            Expand();
        }
    }

    private void OnCollapseDelayElapsed()
    {
        if (!_pointerInside && _holds == 0 && !IsPinned && !_engaged)
        {
            Collapse();
        }
    }

    private void ReleaseHold()
    {
        if (--_holds == 0 && !_pointerInside)
        {
            CollapseUnlessPinned();
        }
    }

    private void CollapseUnlessPinned()
    {
        if (Phase == NotchPhase.Primed)
        {
            SetPhase(NotchPhase.Collapsed);
        }
        else if (Phase == NotchPhase.Expanded && !IsPinned && !_engaged)
        {
            Restart(_collapseTimer);
        }
    }

    private void Expand()
    {
        _intentTimer.Stop();
        _collapseTimer.Stop();
        SetPhase(NotchPhase.Expanded);
    }

    private void Collapse()
    {
        _intentTimer.Stop();
        _collapseTimer.Stop();
        SetPhase(NotchPhase.Collapsed);
    }

    private void SetPhase(NotchPhase phase)
    {
        if (phase == Phase)
        {
            return;
        }

        Phase = phase;
        bool disengaged = _engaged && phase != NotchPhase.Expanded;
        _engaged &= !disengaged;
        PhaseChanged?.Invoke(this, EventArgs.Empty);
        if (disengaged)
        {
            EngagedChanged?.Invoke(this, EventArgs.Empty); // Closed, whatever closed it.
        }
    }

    private void SetEngaged(bool engaged)
    {
        if (engaged != _engaged)
        {
            _engaged = engaged;
            EngagedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void Restart(DispatcherQueueTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private static DispatcherQueueTimer CreateTimer(DispatcherQueue dispatcher, TimeSpan interval, Action tick)
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private sealed class HoldToken(NotchController owner) : IDisposable
    {
        private NotchController? _owner = owner;

        public void Dispose()
        {
            _owner?.ReleaseHold();
            _owner = null;
        }
    }
}
