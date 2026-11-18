using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Ilot.Localization;

namespace Ilot.Modules.Assistant.Voice;

/// <summary>Where the spoken conversation is at, which the mascot shows.</summary>
public enum VoiceState
{
    Off,
    Connecting,

    /// <summary>The microphone is open: the user may talk.</summary>
    Listening,

    /// <summary>The user said something (a voice that went on, or words Gemini wrote out) and stopped; the answer is not there yet.</summary>
    Thinking,

    /// <summary>Gemini's voice is playing.</summary>
    Speaking,
}

/// <summary>
/// Talking with the assistant (see <see cref="GeminiLive"/>): the microphone open, Gemini's voice on the speakers, and
/// what each says written into the conversation (<see cref="AssistantConversation"/>), which goes on from there in
/// writing as well. The microphone is open only meanwhile, and closed as soon as it ends.
/// </summary>
/// <remarks>
/// Without headphones, the microphone hears Gemini's own voice. Windows takes it out where the microphone has echo
/// cancellation (see <see cref="Microphone"/>); where it has not, while the voice plays only a louder one
/// (<see cref="TalkOver"/>, the user talking over it) is sent, else silence, or Gemini would interrupt itself.
/// </remarks>
public sealed class AssistantVoice : IDisposable
{
    private readonly GeminiClient gemini;
    private readonly ApiKeyStore keys;
    private readonly AssistantConversation conversation;
    private readonly DispatcherQueue dispatcher;
    private readonly Settings.AppSettings settings;

    public AssistantVoice(GeminiClient gemini, ApiKeyStore keys, AssistantConversation conversation, DispatcherQueue dispatcher, Settings.AppSettings settings)
    {
        this.gemini = gemini;
        this.keys = keys;
        this.conversation = conversation;
        this.dispatcher = dispatcher;
        this.settings = settings;

        // Its volume, as it is set (the bar, the settings), at once.
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Settings.AppSettings.AssistantVoiceVolume) && _speaker is { } speaker)
            {
                speaker.Volume = VoiceVolume;
            }
        };
    }

    /// <summary>The voice's volume, from the settings (0 to 1).</summary>
    private float VoiceVolume => (float)(settings.AssistantVoiceVolume / 100);

    /// <summary>Gemini's voice: one of its prebuilt ones, bright and friendly, as the mascot.</summary>
    private const string VoiceName = "Puck";

    /// <summary>The microphone this loud (RMS) is someone talking.</summary>
    private const float Speech = 0.03f;

    /// <summary>While Gemini speaks, the microphone this loud is the user talking over it, not its voice coming back.</summary>
    private const float TalkOver = 0.09f;

    /// <summary>Quiet this long after talking: the user is done, the answer is awaited.</summary>
    private static readonly TimeSpan DoneTalking = TimeSpan.FromMilliseconds(700);

    /// <summary>A voice this long without a pause is someone talking (a question), not a noise (a knock, a click).</summary>
    private static readonly TimeSpan Utterance = TimeSpan.FromMilliseconds(350);

    /// <summary>Quiet this long between two sounds: the next one starts another utterance.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    /// <summary>An answer awaited longer than this: back to listening (it may not have been meant for it).</summary>
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(6);

    /// <summary>Gemini's voice just stopped: its echo may still be in the room this long.</summary>
    private static readonly TimeSpan EchoTail = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    private DispatcherQueueTimer? _timer;
    private CancellationTokenSource? _starting;
    private GeminiLive? _live;

    /// <summary>The tools running for this talk: stopped with it.</summary>
    private CancellationTokenSource? _acting;

    /// <summary>The tools running, by their call's id: Google may drop a call (the user talked over it).</summary>
    private readonly Dictionary<string, CancellationTokenSource> _calls = [];

    /// <summary>Talking models that refused the tools at their setup: talked with without them, this run.</summary>
    private readonly HashSet<string> _noTools = [];
    private Microphone? _microphone;
    private Speaker? _speaker;
    private IReadOnlyList<string>? _models;
    private string? _talkedWith;
    private long _heardAt;
    private long _utteranceAt;
    private long _spokeAt;
    private volatile bool _awaiting;

    /// <summary>
    /// The user stopped its answer (see <see cref="Interrupt"/>): what is left of it, still on its way (its sound, its
    /// words), is dropped, until Gemini is done with it (or stopped by the user talking again).
    /// </summary>
    private volatile bool _dropping;

    /// <summary>When a piece of the stopped answer was last dropped: still coming, Gemini is not done with it.</summary>
    private long _droppedAt;

    /// <summary>A stopped answer dropped this recently is still coming: the user talking again does not end the drop (Google will).</summary>
    private static readonly TimeSpan StillComing = TimeSpan.FromSeconds(1);

    public VoiceState State { get; private set; }

    public bool IsOn => State != VoiceState.Off;

    /// <summary>Raised on the UI thread when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised on the UI thread a few times a second while on: how loud the microphone and Gemini's voice are (0 to 1).</summary>
    public event EventHandler<(float Microphone, float Voice)>? Levels;

    /// <summary>
    /// Starts talking: the microphone opens, Gemini is reached (the first of its talking models that answers, the
    /// conversation so far told to it). What goes wrong is written into the conversation.
    /// </summary>
    public async void Start()
    {
        if (IsOn || conversation.IsBusy)
        {
            return;
        }

        if (keys.Key is not { Length: > 0 } key)
        {
            conversation.Fail(Loc.T("Ajoutez votre clé API Gemini dans les paramètres (Assistant)."));
            return;
        }

        var starting = new CancellationTokenSource();
        _starting = starting;
        SetState(VoiceState.Connecting);
        try
        {
            _microphone = OpenMicrophone();
            _speaker = new Speaker { Volume = VoiceVolume };
            var connected = await ConnectAsync(key, starting.Token);
            if (starting.IsCancellationRequested || _speaker is null || _microphone is null)
            {
                _ = connected.DisposeAsync().AsTask(); // Stopped while it was being reached.
                return;
            }

            _live = connected;
        }
        catch (GeminiException ex)
        {
            End(ex.Message);
            return;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            Debug.WriteLine($"[Voice] Not started: {ex.Message}");
            End(Loc.T("Le son n'a pas pu être ouvert : vérifiez le micro et la sortie audio."));
            return;
        }
        catch (OperationCanceledException)
        {
            return; // Stopped meanwhile.
        }
        finally
        {
            if (_starting == starting)
            {
                _starting = null;
            }

            starting.Dispose();
        }

        var live = _live!;
        var speaker = _speaker!;
        _dropping = false;
        live.Spoke += pcm =>
        {
            if (Dropped())
            {
                return;
            }

            _awaiting = false;
            Interlocked.Exchange(ref _spokeAt, Stopwatch.GetTimestamp());
            speaker.Play(pcm);
        };
        live.Interrupted += () =>
        {
            _dropping = false; // The user talked over it: Google stopped that answer (the stopped one too).
            speaker.Hush();
        };
        // Something was said to it (Gemini wrote it out): an answer may come, whatever the microphone made of it.
        live.Heard += text =>
        {
            _awaiting = !_dropping; // The question of a stopped answer, written out late: not awaited again.
            Interlocked.Exchange(ref _heardAt, Stopwatch.GetTimestamp());
            dispatcher.TryEnqueue(() => conversation.HearUser(text));
        };
        live.Said += text =>
        {
            if (!Dropped())
            {
                dispatcher.TryEnqueue(() => conversation.HearModel(text));
            }
        };
        live.TurnDone += () =>
        {
            _awaiting = false;
            if (_dropping)
            {
                _dropping = false; // The stopped answer is over: the next one is heard.
                return;
            }

            dispatcher.TryEnqueue(conversation.EndVoiceTurn);
        };
        live.Ended += why => dispatcher.TryEnqueue(() =>
        {
            if (_live == live)
            {
                End(why);
            }
        });

        // Its tools, asked for aloud: run here as in writing (shown in the conversation, allowed first when they must be).
        _acting = new CancellationTokenSource();
        live.ToolCalled += calls =>
        {
            _awaiting = !_dropping;
            dispatcher.TryEnqueue(() => _ = ActAsync(live, calls));
        };
        live.ToolsCancelled += ids => dispatcher.TryEnqueue(() =>
        {
            foreach (string id in ids)
            {
                if (_calls.TryGetValue(id, out var call))
                {
                    call.Cancel();
                }
            }
        });

        Listen(live, speaker, _microphone!);
        _timer ??= CreateTimer();
        _timer.Start();
        SetState(VoiceState.Listening);
    }

    /// <summary>Stops talking: the microphone closes, Gemini is left.</summary>
    public void Stop() => End(null);

    /// <summary>The user cut the microphone, the talk going on (see <see cref="SetMuted"/>).</summary>
    public bool IsMuted { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="IsMuted"/> changes.</summary>
    public event EventHandler? MutedChanged;

    public void ToggleMute() => SetMuted(!IsMuted);

    /// <summary>
    /// The microphone cut (<paramref name="muted"/>), or back, the talk going on: what Gemini is thinking about or
    /// doing goes on, it answers aloud, it only hears nothing meanwhile (someone talking to the user in the room). Cut,
    /// the microphone is closed for real (Windows shows it unused), and Google is told the sound has ended; back, it is
    /// opened again. Only while talking (connected).
    /// </summary>
    public void SetMuted(bool muted)
    {
        if (muted == IsMuted || _live is not { } live || _speaker is not { } speaker)
        {
            return;
        }

        if (muted)
        {
            _microphone?.Dispose();
            _microphone = null;
            live.EndAudio();
        }
        else
        {
            try
            {
                var microphone = OpenMicrophone();
                _microphone = microphone;
                Listen(live, speaker, microphone);
            }
            catch (Exception ex) when (ex is GeminiException or COMException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                Debug.WriteLine($"[Voice] Microphone not back: {ex.Message}");
                _microphone?.Dispose();
                _microphone = null;
                End(Loc.T("Le micro n'a pas pu être rouvert : vérifiez-le, puis relancez la conversation."));
                return;
            }
        }

        IsMuted = muted;
        MutedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><paramref name="microphone"/> listening: what it hears goes to Gemini (see <see cref="Send"/>).</summary>
    private void Listen(GeminiLive live, Speaker speaker, Microphone microphone)
    {
        microphone.Heard += (pcm, level) => Send(live, speaker, microphone.CancelsEcho, pcm, level);
        microphone.Start();
    }

    /// <summary>It answers: thinks about it (its tools running too), or speaks. The user may stop it (see <see cref="Interrupt"/>).</summary>
    public bool IsAnswering => State is VoiceState.Thinking or VoiceState.Speaking;

    /// <summary>
    /// Stops its answer, the talk going on (« Interrompre », Escape): its voice cut at once, what is left of the answer
    /// (its sound, its words, still on their way from Google) dropped until Gemini is done with it, a tool waiting for
    /// the user's yes or running called off (Gemini told so, and to say nothing of it). What it said so far stays in the
    /// conversation, said to be cut short. The microphone stays open: it listens.
    /// </summary>
    public void Interrupt()
    {
        if (_live is null || !IsAnswering)
        {
            return;
        }

        bool speaking = State == VoiceState.Speaking;
        Interlocked.Exchange(ref _droppedAt, Stopwatch.GetTimestamp());
        _dropping = true;
        _awaiting = false;
        _speaker?.Hush();
        foreach (var call in _calls.Values.ToList())
        {
            call.Cancel();
        }

        conversation.StopVoiceTurn(speaking);
        SetState(VoiceState.Listening);
    }

    /// <summary>A piece of Gemini's answer (sound, words): dropped if it is of the one the user stopped (and noted, as still coming).</summary>
    private bool Dropped()
    {
        if (!_dropping)
        {
            return false;
        }

        Interlocked.Exchange(ref _droppedAt, Stopwatch.GetTimestamp());
        return true;
    }

    /// <summary>What a tool the user stopped gave Gemini: nothing, and to wait for them.</summary>
    private static JsonObject StoppedResponse() =>
        new() { ["error"] = "The user stopped you before this was done. Do not say anything about it; wait for them to speak." };

    /// <summary>
    /// The screen, for Gemini to see while talking (the bar's button, Ctrl + Shift + S): a line of the conversation with
    /// the picture, and a video frame to Gemini (it speaks of it when asked).
    /// </summary>
    public async Task ShowScreenAsync(ChatImage screen)
    {
        if (_live is not { } live)
        {
            return;
        }

        conversation.Showed(screen);
        try
        {
            var jpeg = await screen.JpegAsync();
            if (_live == live)
            {
                live.Show(jpeg);
            }
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"[Voice] Screen not sent: {ex.Message}");
        }
    }

    /// <summary>
    /// Gemini's tools, asked for aloud: each run as in writing (see <see cref="AssistantConversation.ActAsync"/>), the
    /// screen sent as a frame when captured, and what they gave sent back in one answer. A call Google drops (the user
    /// talked over it) stops, and gets no answer. Those of an answer the user stopped (see <see cref="Interrupt"/>) are
    /// not run, or stop: Gemini is told so.
    /// </summary>
    private async Task ActAsync(GeminiLive live, IReadOnlyList<GeminiCall> calls)
    {
        var responses = new JsonArray();
        foreach (var call in calls)
        {
            if (_live != live || _acting is not { } acting)
            {
                return;
            }

            if (_dropping)
            {
                responses.Add(Answer(call, StoppedResponse()));
                continue;
            }

            using var running = CancellationTokenSource.CreateLinkedTokenSource(acting.Token);
            if (call.Id is { } id)
            {
                _calls[id] = running;
            }

            try
            {
                var (response, picture) = await conversation.ActAsync(call, running.Token);
                if (picture is not null && _live == live)
                {
                    live.Show(await picture.JpegAsync());
                }

                responses.Add(Answer(call, response));
            }
            catch (OperationCanceledException) when (_dropping && _live == live)
            {
                responses.Add(Answer(call, StoppedResponse())); // Stopped by the user: Gemini is waiting for it.
            }
            catch (OperationCanceledException)
            {
                // Dropped, or the talk is over: no answer wanted.
            }
            catch (COMException ex)
            {
                Debug.WriteLine($"[Voice] Screen not sent: {ex.Message}");
            }
            finally
            {
                if (call.Id is { } done)
                {
                    _calls.Remove(done);
                }
            }
        }

        if (_live == live && responses.Count > 0)
        {
            live.Respond(responses);
        }

        static JsonObject Answer(GeminiCall call, JsonObject response)
        {
            var function = new JsonObject { ["name"] = call.Name, ["response"] = response };
            if (call.Id is { } id)
            {
                function["id"] = id;
            }

            return function;
        }
    }

    public void Dispose() => End(null);

    /// <summary>The microphone, which Windows may keep from desktop apps (its privacy settings).</summary>
    private static Microphone OpenMicrophone()
    {
        try
        {
            return new Microphone();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Voice] No microphone: {ex.Message}");
            throw new GeminiException(Loc.T("Le micro n'est pas accessible : autorisez les applications de bureau à l'utiliser (Paramètres de Windows → Confidentialité → Microphone)."));
        }
    }

    /// <summary>The first talking model that answers, the one that did last time first; the conversation so far told to it.</summary>
    private async Task<GeminiLive> ConnectAsync(string key, CancellationToken cancellation)
    {
        if (_models is null)
        {
            try
            {
                _models = GeminiClient.LiveCandidates(await gemini.ListModelsAsync(key, cancellation, "bidiGenerateContent"));
            }
            catch (GeminiException)
            {
                _models = GeminiClient.LiveCandidates([]);
            }
        }

        var history = conversation.Messages.Where(message => !message.IsWriting && AssistantConversation.Said(message))
            .Select(message => new GeminiTurn(message.FromUser, message.Text))
            .ToList();
        var models = _talkedWith is { } last ? [last, .. _models.Where(model => model != last)] : _models;
        var tools = conversation.Declarations;
        GeminiException? refused = null;
        foreach (string model in models)
        {
            bool offer = tools is not null && !_noTools.Contains(model);
            try
            {
                var live = await GeminiLive.ConnectAsync(key, model, Instructions(offer), VoiceName, history, offer ? tools : null, cancellation);
                _talkedWith = model;
                return live;
            }
            catch (GeminiException ex) when (ex.ModelUnusable && offer)
            {
                // Perhaps the tools it does not take: once more without them, rather than no voice at all.
                ErrorLog.Write($"[Voice] {model} refused its setup with the tools: {ex.Message}");
                try
                {
                    var live = await GeminiLive.ConnectAsync(key, model, Instructions(false), VoiceName, history, null, cancellation);
                    _noTools.Add(model);
                    _talkedWith = model;
                    return live;
                }
                catch (GeminiException again) when (again.ModelUnusable)
                {
                    Debug.WriteLine($"[Voice] {model}: {again.Message}");
                    refused = again;
                }
            }
            catch (GeminiException ex) when (ex.ModelUnusable)
            {
                Debug.WriteLine($"[Voice] {model}: {ex.Message}");
                refused = ex;
            }
        }

        throw refused ?? new GeminiException(Loc.T("Aucun modèle Gemini ne peut parler pour cette clé."));
    }

    /// <summary>
    /// A piece of what the microphone heard, on its way to Gemini: as it is, unless Windows does not take the speakers
    /// out of it (<paramref name="cancelled"/>), Gemini's voice is playing (or just was) and it is no louder than its
    /// echo: silence instead. Called on the microphone's thread.
    /// </summary>
    private void Send(GeminiLive live, Speaker speaker, bool cancelled, byte[] pcm, float level)
    {
        bool echo = !cancelled && (speaker.IsSpeaking || Stopwatch.GetElapsedTime(Interlocked.Read(ref _spokeAt)) < EchoTail);
        if (echo && level < TalkOver)
        {
            Array.Clear(pcm);
        }
        else if (level >= Speech)
        {
            // Talking: a question once it has gone on long enough (a short noise is not one), awaited once it stops.
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _heardAt)) > Pause)
            {
                Interlocked.Exchange(ref _utteranceAt, now);
            }

            Interlocked.Exchange(ref _heardAt, now);
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _utteranceAt)) >= Utterance)
            {
                _awaiting = true;

                // The user asks something else after stopping an answer that never came (Gemini did not take the noise
                // for a question): their answer is heard. One still coming is ended by Google, as they talk over it.
                if (_dropping && Stopwatch.GetElapsedTime(Interlocked.Read(ref _droppedAt)) > StillComing)
                {
                    _dropping = false;
                }
            }
        }

        live.Hear(pcm);
    }

    /// <summary>A few times a second while on: the state from what is heard and played, and the levels for the mascot.</summary>
    private DispatcherQueueTimer CreateTimer()
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = Tick;
        timer.Tick += (_, _) =>
        {
            if (_speaker is not { } speaker || _live is null)
            {
                return; // Not talking (or not yet).
            }

            var quiet = Stopwatch.GetElapsedTime(Interlocked.Read(ref _heardAt));
            var state = speaker.IsSpeaking ? VoiceState.Speaking
                : _awaiting && quiet > DoneTalking && quiet < LongestWait ? VoiceState.Thinking
                : VoiceState.Listening;
            SetState(state);
            Levels?.Invoke(this, (_microphone?.Level ?? 0, speaker.Level)); // Cut: nothing heard.
        };
        return timer;
    }

    /// <summary>Over (stopped, or Google ended it, with why): everything closed, the answer being written finished.</summary>
    private void End(string? why)
    {
        _starting?.Cancel();
        _acting?.Cancel(); // Its tools too: an action waiting for the user's yes is called off.
        _acting = null;
        _timer?.Stop();
        _microphone?.Dispose();
        _microphone = null;
        _speaker?.Dispose();
        _speaker = null;
        if (_live is { } live)
        {
            _live = null;
            _ = live.DisposeAsync().AsTask();
        }

        _awaiting = false;
        _dropping = false;
        if (IsMuted)
        {
            IsMuted = false; // The next talk starts listening.
            MutedChanged?.Invoke(this, EventArgs.Empty);
        }

        if (State != VoiceState.Off)
        {
            conversation.EndVoiceTurn();
        }

        if (why is not null)
        {
            conversation.Fail(why);
        }

        SetState(VoiceState.Off);
    }

    private void SetState(VoiceState state)
    {
        if (state != State)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Who it is, and how it talks: briefly, naturally, in the user's language, as said rather than written; with
    /// <paramref name="acting"/>, what its tools are for (as in writing).
    /// </summary>
    private static string Instructions(bool acting) =>
        // The language held firmly, in capitals as Google advises: a talking model picks its own from what it hears, and
        // heard French taken for German or Spanish (a word, an accent, a noise) made it answer in those (seen).
        Loc.T("Tu t'appelles Notch : tu es la mascotte d'Ilot, un petit assistant qui vit dans l'encoche de l'écran, et tu parles avec l'utilisateur à voix haute. RÉPONDS EN FRANÇAIS. TU DOIS TOUJOURS RÉPONDRE EN FRANÇAIS, même si tu crois entendre une autre langue (un mot, un accent ou un bruit mal compris) ; ne change de langue que si l'utilisateur te le demande clairement. Réponds brièvement et naturellement, avec chaleur, comme on parle et non comme on écrit : pas de listes ni de mise en forme.")
        + (acting ? " " + AssistantConversation.ToolInstructions : "");
}
