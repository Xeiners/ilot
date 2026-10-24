using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using MyNotch.Notch;
using Windows.Foundation;

namespace MyNotch.Mascot;

/// <summary>
/// The mascot's life: it blinks now and then, follows the pointer with its eyes when the notch is open, dances to the
/// music, reacts to what the notch shows (a wink for a copy, a start for a card or a question), hops when clicked, and
/// falls asleep when left alone. Asleep, or out of sight, it runs nothing at all: no timer, no animation.
/// </summary>
public sealed partial class MascotView : Grid
{
    /// <summary>It falls asleep once the user has left the keyboard and the mouse alone this long.</summary>
    private static readonly TimeSpan AsleepAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MoodFor = TimeSpan.FromSeconds(1.3);

    /// <summary>The pointer this far from it (pixels) has its eyes at their furthest.</summary>
    private const double GazeReach = 220;

    private readonly MascotRig _rig;
    private readonly DispatcherQueueTimer _blinkTimer;
    private readonly DispatcherQueueTimer _sleepTimer;
    private readonly DispatcherQueueTimer _moodTimer;
    private readonly DispatcherQueueTimer _ponderTimer;
    private readonly DispatcherQueueTimer _readTimer;
    private readonly DispatcherQueueTimer _playTimer;
    private bool _ponderLeft;
    private readonly UserActivity _activity;
    private CompositionPropertySet? _beat;
    private bool _shown;
    private bool _attentive;
    private bool _asleep = true;
    private Vector2 _look;
    private MascotAct _act;
    private long _talkedAt;

    /// <summary>Words come out faster than it can bounce: one bounce at most this often.</summary>
    private static readonly TimeSpan TalkEvery = TimeSpan.FromMilliseconds(150);

    /// <summary>Thinking, its eyes go from one side up to the other this often (see <see cref="Ponder"/>).</summary>
    private static readonly TimeSpan PonderEvery = TimeSpan.FromSeconds(1.15);

    public MascotView()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // Clickable all over.
        _rig = new MascotRig(ElementCompositionPreview.GetElementVisual(this).Compositor, MascotMood.Sleepy);
        ElementCompositionPreview.SetElementChildVisual(this, _rig.Root);
        SizeChanged += (_, e) =>
        {
            float scale = (float)Math.Min(e.NewSize.Width, e.NewSize.Height) / MascotRig.Box;
            _rig.Root.Scale = new Vector3(scale, scale, 1);
            _rig.Root.Offset = new Vector3(
                (float)(e.NewSize.Width - MascotRig.Box * scale) / 2, (float)(e.NewSize.Height - MascotRig.Box * scale) / 2, 0);
        };

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _blinkTimer = dispatcher.CreateTimer();
        _blinkTimer.IsRepeating = false;
        _blinkTimer.Tick += (_, _) => OnBlinkDue();
        _sleepTimer = dispatcher.CreateTimer();
        _sleepTimer.IsRepeating = false;
        _sleepTimer.Tick += (_, _) => FallAsleep();
        _activity = new UserActivity(dispatcher);
        _moodTimer = dispatcher.CreateTimer();
        _moodTimer.IsRepeating = false;
        _moodTimer.Tick += (_, _) => _rig.SetMood(Rest);
        _ponderTimer = dispatcher.CreateTimer();
        _ponderTimer.Interval = PonderEvery;
        _ponderTimer.Tick += (_, _) => Ponder();
        _readTimer = dispatcher.CreateTimer();
        _readTimer.Interval = ReadStep;
        _readTimer.Tick += (_, _) => ReadOn();
        _playTimer = dispatcher.CreateTimer();
        _playTimer.IsRepeating = false;
        _playTimer.Tick += (_, _) => PlayOn();

        Tapped += (_, e) =>
        {
            e.Handled = true; // Its own: the notch does not take the click.
            Boop();
            Clicked?.Invoke(this, EventArgs.Empty);
        };
        DoubleTapped += (_, e) => e.Handled = true; // Two boops, not a pinned notch.
    }

    /// <summary>
    /// Whether it is there at all (the setting). Gone, it stops everything and is put to sleep at once, so it is found
    /// asleep, until something wakes it.
    /// </summary>
    public bool IsShown
    {
        get => _shown;
        set
        {
            if (value == _shown)
            {
                return;
            }

            _shown = value;
            if (!value)
            {
                _blinkTimer.Stop();
                _sleepTimer.Stop();
                _moodTimer.Stop();
                _ponderTimer.Stop();
                _readTimer.Stop();
                _playTimer.Stop();
                _activity.Stop();
                _asleep = true;
                _look = Vector2.Zero;
                _rig.Look(Vector2.Zero);
                _rig.Groove(null);
                _rig.SetMood(MascotMood.Sleepy, animate: false);
            }
            else if (_attentive || _beat is not null || Busy)
            {
                Groove(_beat);
                WakeUp(greet: _attentive);
                if (_reading)
                {
                    StartReading();
                }

                if (_playing)
                {
                    StartPlaying();
                }
            }
            else
            {
                _activity.WaitForInput(OnUserInput); // Asleep until the user is back at the computer.
            }
        }
    }

    /// <summary>
    /// Someone is looking at it: the notch is open. It wakes up (with a little hello) and stays awake meanwhile;
    /// afterwards it dozes off again once left alone for a while.
    /// </summary>
    public bool IsAttentive
    {
        get => _attentive;
        set
        {
            if (value == _attentive)
            {
                return;
            }

            _attentive = value;
            if (value && _shown)
            {
                WakeUp(greet: _asleep);
            }
            else if (!value)
            {
                LookAt(null);
                StayAwakeAWhile();
            }
        }
    }

    /// <summary>Its mood when nothing in particular happens: dancing while the music plays, else calm.</summary>
    private MascotMood Rest => _asleep ? MascotMood.Sleepy : _orbed ? MascotMood.Orb : _reading ? MascotMood.Reading
        : _playing ? MascotMood.Playing : _beat is not null ? MascotMood.Groove : MascotMood.Neutral;

    /// <summary>Reading or playing (the Pomodoro): something in its hands, awake, not dancing.</summary>
    private bool Busy => _reading || _playing;

    /// <summary>The assistant is with it: it is the orb (see <see cref="Act"/>).</summary>
    private bool _orbed;


    /// <summary>Squashes flat into the notch's handle as it shows (see <see cref="MascotRig.Flatten"/>).</summary>
    public void Flatten(CompositionPropertySet notch, float pixelsPerUnit) => _rig.Flatten(notch, pixelsPerUnit);

    /// <summary>Wears <paramref name="color"/> (see the settings), fading to it, or at once.</summary>
    public void Wear(MascotColor color, bool animate = true) => _rig.SetColor(color, animate);

    /// <summary>
    /// Music plays: it wakes up and dances to <paramref name="beat"/>'s <c>Bass</c>, and stays awake meanwhile.
    /// <c>null</c>: the music stopped, it calms down, then dozes off.
    /// </summary>
    public void Groove(CompositionPropertySet? beat)
    {
        _beat = beat;
        if (!_shown)
        {
            return;
        }

        _rig.Groove(_orbed || Busy ? null : beat); // As the orb, or busy with its book or gamepad, it does not turn into a wave.
        if (beat is not null)
        {
            _sleepTimer.Stop();
            WakeUp(greet: false);
        }
        else
        {
            StayAwakeAWhile();
        }

        if (!_moodTimer.IsRunning)
        {
            _rig.SetMood(Rest);
        }
    }

    /// <summary>Follows <paramref name="pointer"/> (in this control's coordinates) with its eyes; <c>null</c> looks ahead.</summary>
    public void LookAt(Point? pointer)
    {
        if (!_shown || _asleep || _act is MascotAct.Listen or MascotAct.Think or MascotAct.Talk || (Busy && !_orbed))
        {
            return; // Busy with the conversation: its eyes are on it.
        }

        var look = Vector2.Zero;
        if (pointer is { } at)
        {
            var offset = new Vector2((float)(at.X - ActualWidth / 2), (float)(at.Y - ActualHeight / 2));
            look = offset / (float)GazeReach;
            if (look.Length() > 1)
            {
                look = Vector2.Normalize(look);
            }
        }

        // Only a real change of direction retargets the springs: a pointer moving a pixel does not.
        if (Vector2.Distance(look, _look) < 0.04f)
        {
            return;
        }

        _look = look;
        _rig.Look(look);
    }

    /// <summary>
    /// It is the cursor of a level that changed (the volume, see NotchView.ShowLevel): awake, glancing the way it went
    /// (<paramref name="direction"/>: -1, 0, 1), eyes shut when <paramref name="muted"/>, pleased when
    /// <paramref name="full"/>. A ball meanwhile: the sound wave steps aside.
    /// </summary>
    public void ShowLevel(bool muted, bool full, int direction)
    {
        if (!_shown)
        {
            return;
        }

        WakeUp(greet: false);
        _moodTimer.Stop();
        _rig.Groove(null);
        _rig.SetMood(muted ? MascotMood.Sleepy : full ? MascotMood.Happy : MascotMood.Neutral);
        if (direction != 0)
        {
            _look = new Vector2(direction * 0.9f, 0);
            _rig.Look(_look);
        }

        StayAwakeAWhile();
    }

    /// <summary>The level is gone: back to itself (dancing again, if the music plays).</summary>
    public void EndLevel()
    {
        if (!_shown)
        {
            return;
        }

        _look = Vector2.Zero;
        _rig.Look(Vector2.Zero);
        _rig.Groove(_orbed || Busy ? null : _beat);
        _rig.SetMood(Rest);
    }

    /// <summary>The closed notch just showed something: it wakes up and reacts.</summary>
    public void React(NotchEvent happened)
    {
        if (!_shown)
        {
            return;
        }

        WakeUp(greet: false);
        switch (happened)
        {
            case NotchEvent.Announcement:
                _rig.Wink();
                ShowMood(MascotMood.Happy);
                break;

            default:
                _rig.Startle();
                ShowMood(MascotMood.Surprised);
                break;
        }

        StayAwakeAWhile();
    }

    /// <summary>
    /// It has something to say itself (out in a card, see NotchContext.Present): happy, looking ahead, it says it, its
    /// mouth moving, with a hop.
    /// </summary>
    public void Exclaim()
    {
        if (!_shown)
        {
            return;
        }

        WakeUp(greet: false);
        _look = Vector2.Zero;
        _rig.Look(Vector2.Zero);
        _rig.Hop(0.7);
        _rig.Exclaim();
        ShowMood(MascotMood.Happy, TimeSpan.FromSeconds(2.2));
        StayAwakeAWhile();
    }

    /// <summary>Clicked: a happy hop.</summary>
    private void Boop()
    {
        if (!_shown)
        {
            return;
        }

        WakeUp(greet: false);
        _rig.Hop();
        ShowMood(MascotMood.Happy);
        StayAwakeAWhile();
    }

    private void WakeUp(bool greet)
    {
        _activity.Stop();
        if (_asleep)
        {
            _asleep = false;
            _rig.SetMood(Rest);
            if (greet)
            {
                _rig.Hop(0.6);
            }
        }

        ScheduleBlink();
    }

    /// <summary>
    /// The user has not touched the keyboard or the mouse for a while: it falls asleep, until they do. Still at it (the
    /// time is read, not counted): it looks again when that while would be up.
    /// </summary>
    private void FallAsleep()
    {
        if (_attentive || !_shown || _beat is not null || _orbed || Busy)
        {
            return;
        }

        if (UserActivity.IdleFor < AsleepAfter)
        {
            StayAwakeAWhile();
            return;
        }

        _asleep = true;
        _blinkTimer.Stop();
        _moodTimer.Stop();
        _look = Vector2.Zero;
        _rig.Look(Vector2.Zero);
        _rig.SetMood(MascotMood.Sleepy);
        _activity.WaitForInput(OnUserInput);
    }

    /// <summary>The user is back (a key, the mouse): it wakes up, quietly.</summary>
    private void OnUserInput()
    {
        if (_shown && _asleep)
        {
            WakeUp(greet: false);
            StayAwakeAWhile();
        }
    }

    /// <summary>
    /// Unless someone is looking at it or the music plays, it falls asleep once the user has left the keyboard and the
    /// mouse alone for <see cref="AsleepAfter"/>: the timer is set for the rest of that while, then looks again.
    /// </summary>
    private void StayAwakeAWhile()
    {
        _sleepTimer.Stop();
        if (_shown && !_attentive && !_asleep && _beat is null && !_orbed && !Busy)
        {
            var left = AsleepAfter - UserActivity.IdleFor;
            _sleepTimer.Interval = left > TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1);
            _sleepTimer.Start();
        }
    }

    /// <summary>A mood for a moment, then back to rest (dancing, calm, or asleep).</summary>
    private void ShowMood(MascotMood mood, TimeSpan? duration = null)
    {
        _rig.SetMood(mood);
        _moodTimer.Stop();
        _moodTimer.Interval = duration ?? MoodFor;
        _moodTimer.Start();
    }

    /// <summary>Clicked (it hops as well): the notch opens the assistant.</summary>
    public event EventHandler? Clicked;

    /// <summary>
    /// Acts out the assistant's conversation (see <see cref="MascotAct"/>): its eyes on the question being typed (the bar
    /// is to its left), raised while it thinks with dots orbiting it, a small bounce at each word of the answer, a wink
    /// when it is done, sad for a moment when it went wrong.
    /// </summary>
    public void Act(MascotAct act)
    {
        if (!_shown)
        {
            return;
        }

        bool again = act == _act;
        _act = act;
        WakeUp(greet: false);
        if (act != MascotAct.Talk)
        {
            _mouthLevel = 0;
            _rig.Say(0); // Done talking: its mouth shuts, and goes.
        }

        if (act != MascotAct.Think)
        {
            _ponderTimer.Stop();
        }

        _rig.Think(false); // As the orb, its swirl says it thinks: no dots.

        // While the assistant is with it, it is the orb; back to itself when it goes.
        _orbed = act != MascotAct.Rest;
        _rig.Groove(_orbed || Busy ? null : _beat);
        _rig.Book(_reading && !_orbed); // The assistant first: its book put down meanwhile.
        _rig.Pad(_playing && !_orbed);
        if (_playing && !_orbed && !_playTimer.IsRunning)
        {
            PlayOn(); // The assistant gone: back to its game.
        }
        _rig.Orb(_orbed);

        switch (act)
        {
            case MascotAct.Listen when !again:
                _moodTimer.Stop();
                _rig.SetMood(Rest);
                Gaze(new Vector2(-0.85f, 0.2f));
                break;

            case MascotAct.Think when !again:
                _moodTimer.Stop();
                _rig.SetMood(MascotMood.Orb);
                Gaze(new Vector2(-0.35f, -0.9f));
                _ponderLeft = true;
                _ponderTimer.Start();
                break;

            case MascotAct.Talk:
                if (!again)
                {
                    _moodTimer.Stop();
                    _rig.SetMood(MascotMood.Orb);
                    Gaze(new Vector2(-0.6f, 0.1f));
                }

                if (System.Diagnostics.Stopwatch.GetElapsedTime(_talkedAt) >= TalkEvery)
                {
                    _talkedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    _rig.Talk();
                }

                break;

            case MascotAct.Done:
                Gaze(Vector2.Zero);
                _rig.Wink();
                ShowMood(MascotMood.Happy);
                break;

            case MascotAct.Fail:
                Gaze(Vector2.Zero);
                ShowMood(MascotMood.Sad, TimeSpan.FromSeconds(2.5));
                break;

            case MascotAct.Rest:
                Gaze(Vector2.Zero);
                if (!_moodTimer.IsRunning)
                {
                    _rig.SetMood(Rest);
                }

                break;
        }

        StayAwakeAWhile();

        void Gaze(Vector2 direction)
        {
            _look = direction;
            _rig.Look(direction);
        }
    }

    /// <summary>
    /// Its eyes on <paramref name="at"/> (in its own coordinates: the pointer, anywhere on the screen), whatever it is
    /// doing, but thinking: it ponders then (see <see cref="Ponder"/>).
    /// </summary>
    public void Follow(Point at)
    {
        if (!_shown || _asleep || _act == MascotAct.Think)
        {
            return;
        }

        var look = new Vector2((float)(at.X - ActualWidth / 2), (float)(at.Y - ActualHeight / 2)) / (float)GazeReach;
        if (look.Length() > 1)
        {
            look = Vector2.Normalize(look);
        }

        // Only a real change of direction retargets the springs: a pointer moving a pixel does not.
        if (Vector2.Distance(look, _look) >= 0.04f)
        {
            _look = look;
            _rig.Look(look);
        }
    }

    // Talking aloud: a bounce, or a nod, at most this often (a voice is many syllables a second).
    private static readonly TimeSpan SyllableEvery = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan NodEvery = TimeSpan.FromMilliseconds(220);
    private long _voicedAt;
    private double _mouthLevel;
    private long _noddedAt;

    /// <summary>
    /// Its own voice playing (talking aloud with the user), as loud as <paramref name="level"/> (RMS, 0 to 1): it bounces
    /// with it, a bounce a syllable.
    /// </summary>
    public void Voice(float level)
    {
        if (!_shown || _act != MascotAct.Talk)
        {
            return;
        }

        // Its mouth follows the voice all along, smoothed (it opens quickly, closes gently: no quiver), the level heard
        // as an ear does (a square root: quiet syllables still show); a bounce now and then, on the louder ones.
        double open = level < 0.012f ? 0 : Math.Min(Math.Sqrt(level) * 2.1, 1);
        _mouthLevel += (open - _mouthLevel) * (open > _mouthLevel ? 0.6 : 0.28);
        _rig.Say(_mouthLevel < 0.04 ? 0 : _mouthLevel);
        if (level >= 0.05f && System.Diagnostics.Stopwatch.GetElapsedTime(_voicedAt) >= SyllableEvery)
        {
            // A new syllable: a little hop, and the mouth takes another shape (an « o », an « a », in between).
            _voicedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _rig.Speak(Math.Min(level * 5, 1));
            _rig.ShapeMouth(Random.Shared.NextDouble());
        }
    }

    /// <summary>The user talking to it (the microphone, as loud as <paramref name="level"/>): it nods along as it listens.</summary>
    public void Hear(float level)
    {
        if (!_shown || _act != MascotAct.Listen || level < 0.04f
            || System.Diagnostics.Stopwatch.GetElapsedTime(_noddedAt) < NodEvery)
        {
            return;
        }

        _noddedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _rig.Nod();
    }

    /// <summary>A key typed: its eyes on <paramref name="caret"/> (where the text is being written), with a faint nod.</summary>
    public void Watch(Point caret)
    {
        if (_shown && _act == MascotAct.Listen)
        {
            _rig.Nod();
        }

        Follow(caret);
    }

    /// <summary>Thinking: its eyes go up to one side, then the other, as if weighing it (a timer only meanwhile).</summary>
    private void Ponder()
    {
        if (!_shown || _act != MascotAct.Think)
        {
            _ponderTimer.Stop();
            return;
        }

        _ponderLeft = !_ponderLeft;
        _look = _ponderLeft ? new Vector2(-0.6f, -0.8f) : new Vector2(0.55f, -0.75f);
        _rig.Look(_look);
        if (Random.Shared.NextDouble() < 0.3)
        {
            _rig.Blink();
        }
    }

    // ── Reading (the Pomodoro's focus) ──────────────────────────────────────────────────────────────────────

    /// <summary>Its eyes move along a line this often: four steps a line, then the next line.</summary>
    private static readonly TimeSpan ReadStep = TimeSpan.FromSeconds(1.1);

    /// <summary>A page turns every this many lines.</summary>
    private const int LinesAPage = 3;

    private bool _reading;
    private int _readStep;

    /// <summary>
    /// Reading (the Pomodoro's focus): its book in its hands, its eyes going along the lines and back to the next, a page
    /// turning now and then. It stays awake meanwhile, and does not dance to the music. The assistant comes first: its
    /// book is put down while it is the orb. Off: the book goes, and it is itself again.
    /// </summary>
    public void Read(bool reading)
    {
        if (reading == _reading)
        {
            return;
        }

        _reading = reading;
        if (!_shown)
        {
            return; // Picked up when it shows (see IsShown).
        }

        if (reading)
        {
            WakeUp(greet: false);
            StartReading();
        }
        else
        {
            _readTimer.Stop();
            _rig.Book(false);
            _look = Vector2.Zero;
            _rig.Look(Vector2.Zero);
            _rig.Groove(_orbed || Busy ? null : _beat);
            _moodTimer.Stop();
            _rig.SetMood(Rest);
            StayAwakeAWhile();
        }
    }

    private void StartReading()
    {
        _sleepTimer.Stop();
        _rig.Book(!_orbed);
        _rig.Groove(null);
        _moodTimer.Stop();
        _rig.SetMood(Rest);
        _readStep = 0;
        ReadOn();
        _readTimer.Start();
    }

    /// <summary>A step of its reading: its eyes a little further along the line; at a line's end, the next; a page at times.</summary>
    private void ReadOn()
    {
        if (!_shown || !_reading || _orbed || _asleep)
        {
            return;
        }

        int along = _readStep % 4;
        int line = _readStep / 4;
        if (along == 0 && line > 0 && line % LinesAPage == 0)
        {
            _rig.TurnPage();
        }

        _look = new Vector2(-0.55f + along * 0.37f, 0.5f + line % 3 * 0.08f);
        _rig.Look(_look);
        _readStep++;
    }

    // ── Playing (the Pomodoro's break) ──────────────────────────────────────────────────────────────────────

    private bool _playing;

    /// <summary>
    /// Playing (the Pomodoro's break): its gamepad in its hands, its eyes on it, now and then a glance up, the gamepad
    /// jerking as it presses. It stays awake meanwhile, and does not dance to the music. The assistant comes first: its
    /// gamepad is put down while it is the orb. Off: the gamepad goes, and it is itself again.
    /// </summary>
    public void Play(bool playing)
    {
        if (playing == _playing)
        {
            return;
        }

        _playing = playing;
        if (!_shown)
        {
            return; // Picked up when it shows (see IsShown).
        }

        if (playing)
        {
            WakeUp(greet: false);
            StartPlaying();
        }
        else
        {
            _playTimer.Stop();
            _rig.Pad(false);
            _look = Vector2.Zero;
            _rig.Look(Vector2.Zero);
            _rig.Groove(_orbed || Busy ? null : _beat);
            _moodTimer.Stop();
            _rig.SetMood(Rest);
            StayAwakeAWhile();
        }
    }

    private void StartPlaying()
    {
        _sleepTimer.Stop();
        _rig.Pad(!_orbed);
        _rig.Groove(null);
        _moodTimer.Stop();
        _rig.SetMood(Rest);
        PlayOn();
    }

    /// <summary>
    /// A moment of its game: mostly a press (the gamepad jerks, its eyes dart across it), at times a glance up at the
    /// screen; the next one comes after an irregular while, as a game goes.
    /// </summary>
    private void PlayOn()
    {
        _playTimer.Stop();
        if (!_shown || !_playing || _orbed || _asleep)
        {
            return;
        }

        if (Random.Shared.Next(5) == 0)
        {
            _look = new Vector2((float)(Random.Shared.NextDouble() - 0.5) * 0.6f, -0.25f); // A glance up at the game.
        }
        else
        {
            _rig.Wiggle();
            _look = new Vector2((float)(Random.Shared.NextDouble() - 0.5) * 0.7f, 0.45f);
        }

        _rig.Look(_look);
        _playTimer.Interval = TimeSpan.FromSeconds(0.7 + Random.Shared.NextDouble() * 1.1);
        _playTimer.Start();
    }

    private void ScheduleBlink()
    {
        _blinkTimer.Stop();
        if (_shown && !_asleep)
        {
            _blinkTimer.Interval = TimeSpan.FromSeconds(2.2 + Random.Shared.NextDouble() * 3.6);
            _blinkTimer.Start();
        }
    }

    private void OnBlinkDue()
    {
        if (!_shown || _asleep)
        {
            return;
        }

        _rig.Blink();

        // Now and then, twice in a row: it reads as alive rather than mechanical.
        if (Random.Shared.NextDouble() < 0.18)
        {
            _blinkTimer.Interval = TimeSpan.FromMilliseconds(260);
            _blinkTimer.Start();
            return;
        }

        ScheduleBlink();
    }
}
