namespace MyNotch.Modules.Pomodoro;

/// <summary>A phase of the Pomodoro: work, or a break.</summary>
public enum PomodoroPhase
{
    Focus,
    ShortBreak,
    LongBreak,
}

/// <summary>Where the timer is at: not started (a phase ready), running, or paused.</summary>
public enum PomodoroState
{
    Ready,
    Running,
    Paused,
}

/// <summary>How long each phase lasts, and how many focuses before a long break; what follows on its own.</summary>
public sealed record PomodoroPlan(
    TimeSpan Focus, TimeSpan ShortBreak, TimeSpan LongBreak, int SessionsBeforeLongBreak, bool AutoStartBreaks, bool AutoStartFocus)
{
    public static PomodoroPlan Default { get; } = new(
        TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), 4, AutoStartBreaks: true, AutoStartFocus: false);

    public TimeSpan Length(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.Focus => Focus,
        PomodoroPhase.ShortBreak => ShortBreak,
        _ => LongBreak,
    };
}

/// <summary>
/// The Pomodoro's clock, without any screen: a focus, then a short break, again, and a long break after so many focuses.
/// It keeps when the phase ends (not a countdown: it never drifts, whatever the timers do), so it is read rather than
/// ticked; <see cref="Tick"/> is called by whoever shows it (once a second, only while it runs) to notice the end.
/// </summary>
public sealed class PomodoroTimer(Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.Now);
    private DateTimeOffset _endsAt;
    private TimeSpan _left = PomodoroPlan.Default.Focus;

    public PomodoroPlan Plan { get; private set; } = PomodoroPlan.Default;

    public PomodoroPhase Phase { get; private set; } = PomodoroPhase.Focus;

    public PomodoroState State { get; private set; } = PomodoroState.Ready;

    /// <summary>Focuses done since the last long break (0 to the plan's count): the dots.</summary>
    public int Done { get; private set; }

    /// <summary>The phase's whole length.</summary>
    public TimeSpan Length => Plan.Length(Phase);

    /// <summary>What is left of the phase now.</summary>
    public TimeSpan Left => State == PomodoroState.Running ? Max(_endsAt - _now(), TimeSpan.Zero) : _left;

    /// <summary>What is left, as a share of the phase (1 at its start, 0 at its end).</summary>
    public double LeftShare => Length > TimeSpan.Zero ? Math.Clamp(Left / Length, 0, 1) : 0;

    /// <summary>The phase or the state changed (started, paused, a phase ended…).</summary>
    public event EventHandler? Changed;

    /// <summary>A phase ran to its end: which one (the next is ready, or started if the plan says so).</summary>
    public event EventHandler<PomodoroPhase>? Finished;

    /// <summary>Another plan (the settings changed): a phase not started takes it at once; one going keeps its length.</summary>
    public void Use(PomodoroPlan plan)
    {
        Plan = plan;
        Done = Math.Min(Done, plan.SessionsBeforeLongBreak);
        if (State == PomodoroState.Ready)
        {
            _left = Length;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Start()
    {
        if (State == PomodoroState.Running)
        {
            return;
        }

        if (State == PomodoroState.Ready)
        {
            _left = Length;
        }

        _endsAt = _now() + _left;
        State = PomodoroState.Running;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (State != PomodoroState.Running)
        {
            return;
        }

        _left = Left;
        State = PomodoroState.Paused;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Toggle()
    {
        if (State == PomodoroState.Running)
        {
            Pause();
        }
        else
        {
            Start();
        }
    }

    /// <summary>The phase from its start again, not running.</summary>
    public void Restart()
    {
        _left = Length;
        State = PomodoroState.Ready;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>All over: a focus, none done, not running.</summary>
    public void Reset()
    {
        Phase = PomodoroPhase.Focus;
        Done = 0;
        Restart();
    }

    /// <summary>On to the next phase now (a focus skipped does not count as done), not running.</summary>
    public void Skip() => Advance(completed: false, autoStart: false);

    /// <summary>Notices the end of the phase (call it once a second while it runs): then the next one comes.</summary>
    public void Tick()
    {
        if (State == PomodoroState.Running && _endsAt <= _now())
        {
            var finished = Phase;
            Advance(completed: true, autoStart: finished == PomodoroPhase.Focus ? Plan.AutoStartBreaks : Plan.AutoStartFocus);
            Finished?.Invoke(this, finished);
        }
    }

    /// <summary>The phase after this one: a break after a focus (the long one every so many), a focus after a break.</summary>
    private void Advance(bool completed, bool autoStart)
    {
        if (Phase == PomodoroPhase.Focus)
        {
            if (completed)
            {
                Done++;
            }

            Phase = Done >= Plan.SessionsBeforeLongBreak ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak;
        }
        else
        {
            if (Phase == PomodoroPhase.LongBreak)
            {
                Done = 0;
            }

            Phase = PomodoroPhase.Focus;
        }

        _left = Length;
        State = PomodoroState.Ready;
        if (autoStart)
        {
            Start();
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
