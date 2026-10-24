using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ilot.Localization;

namespace Ilot.Modules.Assistant.Voice;

/// <summary>
/// A spoken conversation with Gemini (the Live API, over a WebSocket, nothing but .NET): the microphone's sound goes up
/// as it is heard (16 kHz PCM), Gemini's voice comes down as it speaks (24 kHz PCM), with what each says written out.
/// Gemini hears when the user starts and stops talking by itself, and stops speaking when they talk over it. The key
/// goes in a header, never in the address.
/// </summary>
/// <remarks>Events are raised on the socket's thread: the caller takes them to its own.</remarks>
internal sealed class GeminiLive : IAsyncDisposable
{
    private const string Address = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    /// <summary>Setting up longer than this: Google does not answer.</summary>
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(12);

    private readonly ClientWebSocket _socket = new();
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _life = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _sending;
    private Task? _receiving;
    private int _ended;

    private GeminiLive()
    {
    }

    /// <summary>A piece of Gemini's voice: 24 kHz, 16-bit, mono PCM.</summary>
    public event Action<byte[]>? Spoke;

    /// <summary>The user talked over Gemini: what it was saying is not wanted any more (the voice not yet heard is dropped).</summary>
    public event Action? Interrupted;

    /// <summary>Gemini is done with its answer (its voice may still be playing).</summary>
    public event Action? TurnDone;

    /// <summary>More of what the user said, written out.</summary>
    public event Action<string>? Heard;

    /// <summary>More of what Gemini said, written out.</summary>
    public event Action<string>? Said;

    /// <summary>The conversation is over: Google ended it (<c>null</c> if it was asked to), with why, for the user.</summary>
    public event Action<string?>? Ended;

    /// <summary>
    /// Opens a conversation with <paramref name="model"/>: set up (its voice, its instructions, what each says written
    /// out), and told <paramref name="history"/> (the conversation so far, written) without being asked to answer.
    /// Throws <see cref="GeminiException"/> if Google says no (<see cref="GeminiException.ModelUnusable"/>: another model
    /// may say yes).
    /// </summary>
    public static async Task<GeminiLive> ConnectAsync(
        string key, string model, string instructions, string voice, IReadOnlyList<GeminiTurn> history, CancellationToken cancellation)
    {
        var live = new GeminiLive();
        try
        {
            live._socket.Options.SetRequestHeader("x-goog-api-key", key);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(SetupTimeout);
            try
            {
                await live._socket.ConnectAsync(new Uri(Address), timeout.Token);
            }
            catch (WebSocketException)
            {
                throw new GeminiException(Loc.T("Pas de connexion à Google : vérifiez Internet."));
            }

            live._receiving = Task.Run(live.ReceiveAsync);
            live._sending = Task.Run(live.SendAsync);
            live.Send(Setup(model, instructions, voice));
            using (timeout.Token.Register(() => live._ready.TrySetCanceled()))
            {
                await live._ready.Task;
            }

            // The conversation so far, so it goes on from there (images left out: the voice has none).
            var turns = new JsonArray();
            foreach (var turn in history.Where(turn => turn.Text.Length > 0).TakeLast(MostRemembered))
            {
                turns.Add(new JsonObject
                {
                    ["role"] = turn.FromUser ? "user" : "model",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = turn.Text }),
                });
            }

            if (turns.Count > 0)
            {
                live.Send(new JsonObject { ["clientContent"] = new JsonObject { ["turns"] = turns, ["turnComplete"] = false } });
            }

            return live;
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            string? said = live._socket.CloseStatusDescription;
            await live.DisposeAsync();
            throw said is not null ? Refusal(said) : new GeminiException(Loc.T("Gemini ne répond pas pour la voix : réessayez dans un moment."), modelUnusable: true);
        }
        catch
        {
            await live.DisposeAsync();
            throw;
        }
    }

    /// <summary>The written turns of the conversation told to the voice, at most (the latest).</summary>
    private const int MostRemembered = 20;

    /// <summary>A piece of what the microphone heard: 16 kHz, 16-bit, mono PCM.</summary>
    public void Hear(ReadOnlySpan<byte> pcm) =>
        Send(new JsonObject
        {
            ["realtimeInput"] = new JsonObject
            {
                ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(pcm), ["mimeType"] = "audio/pcm;rate=16000" },
            },
        });

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _ended, 1);
        _outgoing.Writer.TryComplete();
        _life.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, bye.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }

        foreach (var task in new[] { _sending, _receiving })
        {
            if (task is not null)
            {
                await task.ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }

        _socket.Dispose();
        _life.Dispose();
    }

    private static JsonObject Setup(string model, string instructions, string voice) => new()
    {
        ["setup"] = new JsonObject
        {
            ["model"] = $"models/{model}",
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("AUDIO"),
                ["speechConfig"] = new JsonObject
                {
                    ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = voice } },
                },
            },
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = instructions }) },
            ["inputAudioTranscription"] = new JsonObject(),
            ["outputAudioTranscription"] = new JsonObject(),
        },
    };

    private void Send(JsonObject message) => _outgoing.Writer.TryWrite(message.ToJsonString());

    /// <summary>One message at a time: a WebSocket takes no two sends at once.</summary>
    private async Task SendAsync()
    {
        try
        {
            await foreach (string message in _outgoing.Reader.ReadAllAsync(_life.Token))
            {
                await _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, _life.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Ended: the receiving side says why.
        }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        string? why = null;
        try
        {
            while (!_life.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, _life.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    why = _socket.CloseStatusDescription is { Length: > 0 } said
                        ? Refusal(said).Message
                        : null;
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    Read(message.GetBuffer().AsSpan(0, (int)message.Length));
                    message.SetLength(0);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            why = _life.IsCancellationRequested ? null : Loc.T("La connexion avec Gemini a été coupée.");
        }

        _ready.TrySetCanceled();
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            Ended?.Invoke(why);
        }
    }

    /// <summary>One message from Google (JSON, in text or binary frames alike).</summary>
    private void Read(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("setupComplete", out _))
            {
                _ready.TrySetResult();
            }

            if (root.TryGetProperty("serverContent", out var content))
            {
                if (content.TryGetProperty("inputTranscription", out var heard) && heard.TryGetProperty("text", out var heardText) && heardText.GetString() is { Length: > 0 } user)
                {
                    Heard?.Invoke(user);
                }

                if (content.TryGetProperty("modelTurn", out var turn) && turn.TryGetProperty("parts", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("inlineData", out var data) && data.TryGetProperty("data", out var audio) && audio.GetString() is { Length: > 0 } base64)
                        {
                            Spoke?.Invoke(Convert.FromBase64String(base64));
                        }
                    }
                }

                if (content.TryGetProperty("outputTranscription", out var said) && said.TryGetProperty("text", out var saidText) && saidText.GetString() is { Length: > 0 } model)
                {
                    Said?.Invoke(model);
                }

                if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.ValueKind == JsonValueKind.True)
                {
                    Interrupted?.Invoke();
                }

                if (content.TryGetProperty("turnComplete", out var complete) && complete.ValueKind == JsonValueKind.True)
                {
                    TurnDone?.Invoke();
                }
            }

            if (root.TryGetProperty("goAway", out _) && Interlocked.Exchange(ref _ended, 1) == 0)
            {
                Ended?.Invoke(Loc.T("La conversation vocale a atteint sa durée maximale : relancez-la pour continuer."));
            }
        }
    }

    /// <summary>Why Google closed the conversation (its own words), for the user; and whether another model may do.</summary>
    internal static GeminiException Refusal(string said)
    {
        if (said.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            return new GeminiException(Loc.T("Clé API refusée par Google : vérifiez-la dans les paramètres."));
        }

        bool quota = said.Contains("quota", StringComparison.OrdinalIgnoreCase) || said.Contains("exhausted", StringComparison.OrdinalIgnoreCase)
            || said.Contains("exceeded", StringComparison.OrdinalIgnoreCase);
        bool absent = said.Contains("not found", StringComparison.OrdinalIgnoreCase) || said.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || said.Contains("not available", StringComparison.OrdinalIgnoreCase) || said.Contains("is not", StringComparison.OrdinalIgnoreCase);
        return quota
            ? new GeminiException(Loc.T("Quota Gemini atteint pour la voix : réessayez dans un moment."), modelUnusable: true)
            : absent
                ? new GeminiException(Loc.F("Ce modèle ne parle pas : {0}", said), modelUnusable: true, lasting: true)
                : new GeminiException(Loc.F("Gemini a refusé la conversation vocale : {0}", said), modelUnusable: true);
    }
}
