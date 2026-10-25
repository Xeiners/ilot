using System.Runtime.InteropServices;
using System.Text.Json;
using Ilot.Interop;
using Ilot.Settings;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Ilot.Quick;

/// <summary>
/// Apps louder than Windows lets them be (up to 200 %), nothing to install: an app set past 100 % in the mixer has its
/// sessions put down to <see cref="BoostStream.Floor"/> (its sound 60 dB down, losslessly), and Ilot plays its sound again
/// as loud as asked (<see cref="BoostStream"/>: Windows' capture of a process, then a gain and a limiter). The others are
/// left to Windows: nothing runs for them, nor at all when no app is boosted.
/// <para>
/// What it puts down, it gives back: when its switch goes off (each app at 100 %), when an app comes back under 100 %, when
/// Ilot quits; and, after a crash, at the next launch, from what it noted before (<c>boost-held.json</c>: Windows remembers
/// an app's volume, so a boosted app would come back silent). An app's volume moved elsewhere (Windows' mixer) ends its
/// boost there. Its work happens on a thread of its own, in passes that look at all the outputs' sessions; the mixer only
/// says what it wants.
/// </para>
/// </summary>
public sealed class SoundBooster : IDisposable
{
    /// <summary>The loudest an app can be: 200 %.</summary>
    public const double Most = 2;

    /// <summary>The capture of a process's sound: Windows 11 (and Windows Server 2022, the same build).</summary>
    public static bool IsSupported { get; } = Environment.OSVersion.Version.Build >= 20348;

    private static string HeldPath => Path.Combine(AppData.Folder, "boost-held.json");

    private readonly AppSettings _settings;
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _levels;            // An app's program → its level, past 100 %.
    private readonly Dictionary<string, double> _restore = [];      // → its level to give back (100 % or less), next pass.
    private readonly Dictionary<string, float> _held;               // → its volume before it went down (on disk).
    private readonly HashSet<string> _failed = [];                  // → not captured (it plays as Windows has it).
    private readonly Dictionary<int, Boosted> _streams = [];        // A process played for → its stream.
    private readonly List<Watched> _watched = [];                   // The sessions down, followed.
    private Task _work = Task.CompletedTask;
    private bool _queued;
    private bool _rebuild;
    private bool _rewatch;
    private bool _closed;
    private MMDeviceEnumerator? _devices;
    private MMDeviceNotificationClient? _notifications;
    private readonly List<MMDevice> _endpoints = [];

    public SoundBooster(AppSettings settings)
    {
        _settings = settings;
        _levels = new Dictionary<string, double>(settings.BoostLevels.Where(level => level.Value > 1));
        _held = ReadHeld();
    }

    /// <summary>The booster's switch, in the mixer: off, every app at 100 % at most (their levels kept for when it is on).</summary>
    public bool IsOn
    {
        get => _settings.BoostOn;
        set
        {
            _settings.BoostOn = value;
            Queue();
        }
    }

    /// <summary>At launch: what a crash left down is given back (or goes on, boosted), and the boosts start again.</summary>
    public void Start()
    {
        bool any;
        lock (_gate)
        {
            any = _held.Count > 0 || (_settings.BoostOn && _levels.Count > 0);
        }

        if (any && IsSupported)
        {
            Queue();
        }
    }

    /// <summary>
    /// The level the booster has for an app (by its program, see <see cref="MixerApp.Key"/>): past 100 %, or the one it is
    /// about to give back; <c>null</c> when it is Windows' own.
    /// </summary>
    public double? Level(string key)
    {
        lock (_gate)
        {
            if (_settings.BoostOn && _levels.TryGetValue(key, out double level) && !_failed.Contains(key))
            {
                return level;
            }

            if (_restore.TryGetValue(key, out double back))
            {
                return back;
            }

            return _held.TryGetValue(key, out float before) ? (_levels.ContainsKey(key) ? 1 : before) : null;
        }
    }

    /// <summary>An app's volume is the booster's to set (boosted, or given back soon), not Windows' mixer's.</summary>
    public bool Holds(string key) => Level(key) is not null;

    /// <summary>
    /// An app's level, from 0 to <see cref="Most"/>: past 100 % (the switch on), it is boosted; at 100 % or less, it is
    /// given back that volume.
    /// </summary>
    public void Set(string key, double level)
    {
        level = Math.Clamp(level, 0, Most);
        bool pass = true;
        lock (_gate)
        {
            if (level > 1 && _settings.BoostOn)
            {
                _levels[key] = level;
                _restore.Remove(key);
                foreach (var boosted in _streams.Values.Where(boosted => boosted.Key == key))
                {
                    boosted.Stream.Level = level; // Playing already: louder or softer at once.
                    pass = false;
                }
            }
            else
            {
                _levels.Remove(key);
                _restore[key] = Math.Min(level, 1);
            }

            _failed.Remove(key);
            _settings.BoostLevels = new Dictionary<string, double>(_levels);
        }

        if (pass)
        {
            Queue();
        }
    }

    /// <summary>Everything given back (Ilot quits): its streams stop, the sessions down go back up.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;
        }

        Queue();
        try
        {
            _work.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException ex)
        {
            ErrorLog.Write($"[Booster] Not all given back: {ex.InnerException}");
        }
    }

    private void Queue()
    {
        lock (_gate)
        {
            if (_queued)
            {
                return;
            }

            _queued = true;
            _work = _work.ContinueWith(_ => Run(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void Run()
    {
        try
        {
            Pass();
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidOperationException or ArgumentException)
        {
            ErrorLog.Write($"[Booster] {ex}");
        }
    }

    /// <summary>
    /// One pass: what is wanted, made so. The streams no longer wanted stop first (silent before anything goes back up),
    /// those wanted start (silent until their sessions are down), the sessions in a boosted tree go down (their volume
    /// noted on disk before), the others noted go back up; the sessions down are followed, and new ones watched for.
    /// </summary>
    private void Pass()
    {
        Dictionary<string, double> boosts;
        Dictionary<string, double> restore;
        bool rebuild;
        bool rewatch;
        bool closed;
        bool idle;
        lock (_gate)
        {
            _queued = false;
            closed = _closed;
            boosts = !closed && _settings.BoostOn && IsSupported
                ? _levels.Where(level => !_failed.Contains(level.Key)).ToDictionary()
                : [];
            restore = new(_restore);
            _restore.Clear();
            rebuild = _rebuild || closed;
            rewatch = _rewatch;
            _rebuild = _rewatch = false;
            idle = boosts.Count == 0 && restore.Count == 0 && _held.Count == 0 && _streams.Count == 0;
        }

        if (idle)
        {
            Unwatch(); // Nothing to do, nor to wait for.
            return;
        }

        foreach (var watched in _watched)
        {
            watched.Dispose(); // This pass sets volumes: its own changes are not someone else's.
        }

        _watched.Clear();
        using var devices = new MMDeviceEnumerator();
        var endpoints = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var sessions = new List<Session>();
        var kept = new HashSet<Session>();
        try
        {
            int self = Environment.ProcessId;
            foreach (var endpoint in endpoints)
            {
                var manager = endpoint.AudioSessionManager;
                manager.RefreshSessions();
                var all = manager.Sessions;
                for (int i = 0; i < all.Count; i++)
                {
                    var control = all[i];
                    int id = (int)control.GetProcessID;
                    if (control.State == AudioSessionState.AudioSessionStateExpired || control.IsSystemSoundsSession || id == self || id == 0)
                    {
                        control.Dispose();
                        continue;
                    }

                    sessions.Add(new Session(control, id, NativeMethods.ProcessPath(id)?.ToLowerInvariant()));
                }
            }

            // The processes played for: those of the boosted apps' sessions, but not one inside another's tree (twice).
            var parents = boosts.Count > 0 ? NativeMethods.ParentProcesses() : [];
            var boosted = sessions.Where(session => session.Key is { } key && boosts.ContainsKey(key)).ToList();
            var ids = boosted.Select(session => session.Id).ToHashSet();
            var roots = new Dictionary<int, string>();
            foreach (var session in boosted)
            {
                if (!Lineage(session.Id, parents).Skip(1).Any(ids.Contains))
                {
                    roots[session.Id] = session.Key!;
                }
            }

            // 1. The streams no longer wanted (or on an output no longer the default), silent first.
            List<Boosted> stopping;
            lock (_gate)
            {
                stopping = [.. _streams.Values.Where(stream => rebuild || !roots.TryGetValue(stream.Stream.ProcessId, out string? key) || key != stream.Key)];
                foreach (var stream in stopping)
                {
                    _streams.Remove(stream.Stream.ProcessId);
                }
            }

            foreach (var stream in stopping)
            {
                stream.Stream.Tripped -= OnTripped;
                stream.Stream.Dispose();
            }

            // 2. Those wanted, started: silent until settled.
            var started = new HashSet<int>();
            foreach (var (id, key) in roots)
            {
                bool playing;
                lock (_gate)
                {
                    playing = _streams.ContainsKey(id);
                }

                if (playing)
                {
                    continue;
                }

                if (BoostStream.StartAsync(id, boosts[key]).GetAwaiter().GetResult() is { } stream)
                {
                    stream.Tripped += OnTripped;
                    started.Add(id);
                    lock (_gate)
                    {
                        _streams[id] = new Boosted(stream, key);
                    }
                }
                else
                {
                    lock (_gate)
                    {
                        _failed.Add(key);
                    }
                }
            }

            // 3. Down: every session in a tree played for, on every output (its volume noted first, on disk).
            Dictionary<int, Boosted> playingFor;
            lock (_gate)
            {
                playingFor = new(_streams);
            }

            var down = sessions.Where(session => Lineage(session.Id, parents).Any(playingFor.ContainsKey)).ToList();
            bool noted = false;
            lock (_gate)
            {
                foreach (var session in down)
                {
                    if (!_held.ContainsKey(session.Held))
                    {
                        float volume = Read(() => session.Control.SimpleAudioVolume.Volume, 1);
                        _held[session.Held] = volume > BoostStream.Floor * 1.5f ? volume : 1;
                        noted = true;
                    }
                }
            }

            if (noted)
            {
                WriteHeld();
            }

            var lowered = new HashSet<int>();
            foreach (var session in down)
            {
                if (Math.Abs(Read(() => session.Control.SimpleAudioVolume.Volume, BoostStream.Floor) - BoostStream.Floor) > 1e-6f)
                {
                    Write(session, () => session.Control.SimpleAudioVolume.Volume = BoostStream.Floor);
                    lowered.Add(session.Id);
                }
            }

            foreach (var (id, stream) in playingFor)
            {
                if (started.Contains(id) || down.Any(session => lowered.Contains(session.Id) && Lineage(session.Id, parents).Contains(id)))
                {
                    stream.Stream.Settle();
                }
            }

            // 4. Back up: the sessions noted that are not down (the level asked, else 100 % for a boosted app, else theirs).
            var downSet = down.ToHashSet();
            var given = new HashSet<string>();
            foreach (var session in sessions.Where(session => !downSet.Contains(session)))
            {
                double? target;
                lock (_gate)
                {
                    target = session.Key is { } key && restore.TryGetValue(key, out double asked) ? asked
                        : _held.TryGetValue(session.Held, out float before) ? (session.Key is { } boostedKey && _levels.ContainsKey(boostedKey) ? 1 : before)
                        : null;
                }

                if (target is { } volume)
                {
                    Write(session, () => session.Control.SimpleAudioVolume.Volume = (float)volume);
                    given.Add(session.Held);
                }
            }

            lock (_gate)
            {
                bool forgotten = false;
                foreach (string key in given.Where(key => !down.Any(session => session.Held == key)))
                {
                    forgotten |= _held.Remove(key);
                }

                // Asked for an app not playing now: it is given back when it comes.
                foreach (var (key, level) in restore.Where(asked => !sessions.Any(session => session.Key == asked.Key)))
                {
                    _restore.TryAdd(key, level);
                }

                if (forgotten)
                {
                    WriteHeld();
                }
            }

            // 5. The sessions down, followed: moved elsewhere, their boost ends; gone, their stream stops.
            foreach (var session in down)
            {
                var watched = new Watched(this, session);
                try
                {
                    session.Control.RegisterEventClient(watched);
                    _watched.Add(watched);
                    kept.Add(session);
                }
                catch (Exception ex) when (ex is COMException or InvalidComObjectException)
                {
                    // Gone meanwhile: the next session to come brings a pass.
                }
            }

            // 6. New sessions and outputs watched for, while anything is boosted or noted.
            bool waiting;
            lock (_gate)
            {
                waiting = !closed && (_streams.Count > 0 || _held.Count > 0 || _restore.Count > 0 || boosts.Count > 0);
            }

            if (!waiting)
            {
                Unwatch();
            }
            else if (rewatch || _notifications is null)
            {
                Watch();
            }
        }
        finally
        {
            foreach (var session in sessions.Where(session => !kept.Contains(session)))
            {
                session.Control.Dispose();
            }

            foreach (var endpoint in endpoints)
            {
                endpoint.Dispose();
            }
        }
    }

    /// <summary>A process, its parent, and so on up (a few dozen at most: an id reused could loop).</summary>
    private static IEnumerable<int> Lineage(int id, Dictionary<int, int> parents)
    {
        for (int depth = 0; depth < 64 && id > 0; depth++)
        {
            yield return id;
            if (!parents.TryGetValue(id, out int parent) || parent == id)
            {
                yield break;
            }

            id = parent;
        }
    }

    private void OnTripped(object? sender, EventArgs e) => Queue();

    /// <summary>A session down was moved by someone else: its app's boost ends there, at that volume.</summary>
    private void OnMoved(Session session, float volume)
    {
        if (Math.Abs(volume - BoostStream.Floor) <= 1e-6f)
        {
            return;
        }

        lock (_gate)
        {
            if (session.Key is { } key && _levels.Remove(key))
            {
                _restore[key] = volume;
                _settings.BoostLevels = new Dictionary<string, double>(_levels);
            }
        }

        Queue();
    }

    private void Watch()
    {
        Unwatch();
        try
        {
            _devices = new MMDeviceEnumerator();
            _notifications = _devices.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia)
                {
                    Changed(rebuild: true); // The streams play on the default output: they move to the new one.
                }
            };
            _notifications.DeviceAdded += (_, _) => Changed(rebuild: false);
            _notifications.DeviceRemoved += (_, _) => Changed(rebuild: false);
            _notifications.DeviceStateChanged += (_, _) => Changed(rebuild: false);
            foreach (var endpoint in _devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var manager = endpoint.AudioSessionManager;
                manager.OnSessionCreated += (_, _) => Queue(); // An app plays: boosted, or noted (Windows gave it its volume down again).
                manager.RefreshSessions();
                _endpoints.Add(endpoint);
            }
        }
        catch (COMException ex)
        {
            ErrorLog.Write($"[Booster] Sessions not watched: {ex.Message}");
        }
    }

    private void Changed(bool rebuild)
    {
        lock (_gate)
        {
            _rewatch = true;
            _rebuild |= rebuild;
        }

        Queue();
    }

    private void Unwatch()
    {
        _notifications?.Dispose();
        _notifications = null;
        foreach (var endpoint in _endpoints)
        {
            endpoint.Dispose();
        }

        _endpoints.Clear();
        _devices?.Dispose();
        _devices = null;
    }

    private static Dictionary<string, float> ReadHeld()
    {
        try
        {
            if (File.Exists(HeldPath) && JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText(HeldPath)) is { } held)
            {
                // A process's (its program unread) is long gone: its id means nothing now.
                return held.Where(entry => !entry.Key.StartsWith("process:", StringComparison.Ordinal)).ToDictionary();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ErrorLog.Write($"[Booster] {HeldPath} not read: {ex.Message}");
        }

        return [];
    }

    /// <summary>What is down, on disk (called under the lock): nothing noted, no file.</summary>
    private void WriteHeld()
    {
        try
        {
            if (_held.Count == 0)
            {
                File.Delete(HeldPath);
                return;
            }

            Directory.CreateDirectory(AppData.Folder);
            File.WriteAllText(HeldPath, JsonSerializer.Serialize(_held));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write($"[Booster] {HeldPath} not written: {ex.Message}");
        }
    }

    /// <summary>A session gone meanwhile answers with an error: it is left alone (the next pass sees it gone).</summary>
    private static float Read(Func<float> read, float otherwise)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            return otherwise;
        }
    }

    private static void Write(Session session, Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            System.Diagnostics.Debug.WriteLine($"[Booster] Session of {session.Id} not set: {ex.Message}");
        }
    }

    /// <summary>A sound session: its process, and its program (what an app's level is kept by).</summary>
    private sealed record Session(AudioSessionControl Control, int Id, string? Key)
    {
        /// <summary>What its volume is noted by: its program, else its process (not kept past this run).</summary>
        public string Held => Key ?? $"process:{Id}";
    }

    private sealed record Boosted(BoostStream Stream, string Key);

    /// <summary>A session down, followed (its volume moved; it ends).</summary>
    private sealed class Watched(SoundBooster owner, Session session) : IAudioSessionEventsHandler, IDisposable
    {
        public void OnVolumeChanged(float volume, bool isMuted) => owner.OnMoved(session, volume);

        public void OnStateChanged(AudioSessionState state)
        {
            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                owner.Queue();
            }
        }

        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason) => owner.Queue();

        public void OnDisplayNameChanged(string displayName)
        {
        }

        public void OnIconPathChanged(string iconPath)
        {
        }

        public void OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex)
        {
        }

        public void OnGroupingParamChanged(ref Guid groupingId)
        {
        }

        public void Dispose()
        {
            try
            {
                session.Control.UnRegisterEventClient(this);
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                // Gone already.
            }

            session.Control.Dispose();
        }
    }
}
