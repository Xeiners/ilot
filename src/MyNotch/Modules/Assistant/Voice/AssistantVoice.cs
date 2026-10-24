using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using MyNotch.Localization;

namespace MyNotch.Modules.Assistant.Voice;

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
    private Microphone? _microphone;
    private Speaker? _speaker;
    private IReadOnlyList<string>? _models;
    private string? _talkedWith;
    private long _heardAt;
    private long _utteranceAt;
    private long _spokeAt;
    private volatile bool _awaiting;

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
        live.Spoke += pcm =>
        {
            _awaiting = false;
            Interlocked.Exchange(ref _spokeAt, Stopwatch.GetTimestamp());
            speaker.Play(pcm);
        };
        live.Interrupted += speaker.Hush;
        // Something was said to it (Gemini wrote it out): an answer may come, whatever the microphone made of it.
        live.Heard += text =>
        {
            _awaiting = true;
            Interlocked.Exchange(ref _heardAt, Stopwatch.GetTimestamp());
            dispatcher.TryEnqueue(() => conversation.HearUser(text));
        };
        live.Said += text => dispatcher.TryEnqueue(() => conversation.HearModel(text));
        live.TurnDone += () =>
        {
            _awaiting = false;
            dispatcher.TryEnqueue(conversation.EndVoiceTurn);
        };
        live.Ended += why => dispatcher.TryEnqueue(() =>
        {
            if (_live == live)
            {
                End(why);
            }
        });

        var microphone = _microphone!;
        microphone.Heard += (pcm, level) => Send(live, speaker, microphone.CancelsEcho, pcm, level);
        microphone.Start();
        _timer ??= CreateTimer();
        _timer.Start();
        SetState(VoiceState.Listening);
    }

    /// <summary>Stops talking: the microphone closes, Gemini is left.</summary>
    public void Stop() => End(null);

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

        var history = conversation.Messages.Where(message => !message.IsError && !message.IsWriting)
            .Select(message => new GeminiTurn(message.FromUser, message.Text))
            .ToList();
        var models = _talkedWith is { } last ? [last, .. _models.Where(model => model != last)] : _models;
        GeminiException? refused = null;
        foreach (string model in models)
        {
            try
            {
                var live = await GeminiLive.ConnectAsync(key, model, Instructions(), VoiceName, history, cancellation);
                _talkedWith = model;
                return live;
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
            if (_speaker is not { } speaker || _microphone is not { } microphone)
            {
                return;
            }

            var quiet = Stopwatch.GetElapsedTime(Interlocked.Read(ref _heardAt));
            var state = speaker.IsSpeaking ? VoiceState.Speaking
                : _awaiting && quiet > DoneTalking && quiet < LongestWait ? VoiceState.Thinking
                : VoiceState.Listening;
            SetState(state);
            Levels?.Invoke(this, (microphone.Level, speaker.Level));
        };
        return timer;
    }

    /// <summary>Over (stopped, or Google ended it, with why): everything closed, the answer being written finished.</summary>
    private void End(string? why)
    {
        _starting?.Cancel();
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

    /// <summary>Who it is, and how it talks: briefly, naturally, in the user's language, as said rather than written.</summary>
    private static string Instructions() => Loc.T("Tu t'appelles Notch : tu es la mascotte de MyNotch, un petit assistant qui vit dans l'encoche de l'écran, et tu parles avec l'utilisateur à voix haute. Réponds brièvement et naturellement, avec chaleur, dans la langue de l'utilisateur, comme on parle et non comme on écrit : pas de listes ni de mise en forme.");
}
