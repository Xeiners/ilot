using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ilot.Localization;

namespace Ilot.Modules.Assistant;

/// <summary>A Gemini model the key may use: its id ("gemini-…"), and its name as Google gives it.</summary>
public sealed record GeminiModel(string Id, string Name);

/// <summary>A turn of the conversation, as sent: from the user (with the images they joined, if any), or the model's answer.</summary>
public sealed record GeminiTurn(bool FromUser, string Text, IReadOnlyList<GeminiImage>? Images = null);

/// <summary>An image sent with a question: its type ("image/png"…) and its bytes.</summary>
public sealed record GeminiImage(string MimeType, byte[] Data);

/// <summary>
/// Google said no, or could not be reached: the message says why, for the user. <see cref="ModelUnusable"/>: it is the
/// model that cannot be used (no quota for it, unknown): another one may answer; <see cref="Lasting"/>: and it will not
/// be usable later either (no free quota at all on this key, no such model). <see cref="Malformed"/>: the request was
/// refused as such (a setting this model does not take).
/// </summary>
public sealed class GeminiException(string message, bool modelUnusable = false, bool lasting = false, bool malformed = false) : Exception(message)
{
    public bool ModelUnusable { get; } = modelUnusable;

    public bool Lasting { get; } = lasting;

    public bool Malformed { get; } = malformed;

    /// <summary>A quota spent for now: when Google says it may be asked again (its « retryDelay »), if it says.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>The quota spent is the day's (Google's « …PerDay… » one): the model will not answer again for hours.</summary>
    public bool Daily { get; init; }
}

/// <summary>
/// The Gemini API (generativelanguage.googleapis.com), with nothing but HttpClient: the answer streamed as it is written
/// (server-sent events), and the models the key may use. The key goes in a header, never in the address.
/// </summary>
public sealed class GeminiClient
{
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/"),
        Timeout = Timeout.InfiniteTimeSpan, // A long answer streams for a while: cancelled by the caller instead.
    };

    /// <summary>
    /// The models <paramref name="key"/> may chat with (those that generate content), as Google lists them; with
    /// <paramref name="method"/> "bidiGenerateContent", those that talk (the Live API, see Voice/GeminiLive).
    /// </summary>
    public async Task<IReadOnlyList<GeminiModel>> ListModelsAsync(string key, CancellationToken cancellation = default, string method = "generateContent")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "models?pageSize=200");
        request.Headers.Add("x-goog-api-key", key);
        using var response = await Send(request, HttpCompletionOption.ResponseContentRead, cancellation);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var models = new List<GeminiModel>();
        if (document.RootElement.TryGetProperty("models", out var list))
        {
            foreach (var model in list.EnumerateArray())
            {
                bool chats = model.TryGetProperty("supportedGenerationMethods", out var methods)
                    && methods.EnumerateArray().Any(listed => listed.GetString() == method);
                string? name = model.TryGetProperty("name", out var id) ? id.GetString() : null;
                if (chats && name is not null && name.StartsWith("models/gemini", StringComparison.Ordinal))
                {
                    string display = model.TryGetProperty("displayName", out var shown) ? shown.GetString() ?? name : name;
                    models.Add(new GeminiModel(name["models/".Length..], display));
                }
            }
        }

        return models;
    }

    /// <summary>
    /// The models of <paramref name="models"/> to try for « Automatique », best first: the everyday « Flash » ones (not a
    /// lite, preview, alias or specialised variant), the newest version first; then the lite ones; then the rest. A key
    /// does not have a free quota for every model (the newest often not): the first that answers is the one.
    /// </summary>
    public static IReadOnlyList<string> Candidates(IReadOnlyList<GeminiModel> models)
    {
        string[] specialised = ["image", "tts", "audio", "live", "embedding", "learnlm", "robotics", "computer-use"];
        string[] unsettled = ["preview", "exp", "latest", "thinking", "8b"];
        return models
            .Where(model => !specialised.Any(word => Has(model.Id, word)))
            .OrderBy(model => Has(model.Id, "flash") && !Has(model.Id, "lite") && !unsettled.Any(word => Has(model.Id, word)) ? 0
                : Has(model.Id, "flash") && !unsettled.Any(word => Has(model.Id, word)) ? 1
                : Has(model.Id, "flash") ? 2
                : 3)
            .ThenByDescending(model => Version(model.Id))
            .Select(model => model.Id)
            .ToList();
    }

    /// <summary>The Live models to try when the key lists none (Google's current ones, newest first).</summary>
    private static readonly string[] KnownLive = ["gemini-3.8-live", "gemini-3.1-flash-live-preview", "gemini-2.5-flash-native-audio-preview-12-2025"];

    /// <summary>
    /// The models to talk with (the Live API), best first: those the key lists for it (<paramref name="listed"/>), the
    /// stable ones before the previews, the newest first, the specialised ones (translation, transcription) left out and
    /// the slow thinkers last; then the known ones it did not list, in case its list says nothing of them.
    /// </summary>
    public static IReadOnlyList<string> LiveCandidates(IReadOnlyList<GeminiModel> listed)
    {
        string[] specialised = ["translate", "transcribe", "image", "video"];
        var ordered = listed
            .Select(model => model.Id)
            .Where(id => !specialised.Any(word => Has(id, word)))
            .OrderBy(id => Has(id, "thinking") ? 2 : Has(id, "preview") ? 1 : 0)
            .ThenByDescending(Version)
            .ToList();
        return [.. ordered, .. KnownLive.Where(id => !ordered.Contains(id))];
    }

    private static bool Has(string id, string word) => id.Contains(word, StringComparison.OrdinalIgnoreCase);

    // "gemini-2.5-flash": 2.5; an alias without a number comes after the numbered ones.
    private static double Version(string id) =>
        id.Split('-').Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : -1)
            .FirstOrDefault(v => v >= 0, 0);

    /// <summary>
    /// How little <paramref name="model"/> may think before it answers (a chat answer, not a proof): by default the
    /// thinking models think several seconds before the first word. Null: a model that does not think, or one not known.
    /// </summary>
    public static JsonObject? QuickThinking(string model) => Version(model) switch
    {
        >= 3 => new JsonObject { ["thinkingLevel"] = Has(model, "flash") ? "minimal" : "low" },
        >= 2.5 => new JsonObject { ["thinkingBudget"] = Has(model, "pro") ? 128 : 0 }, // Pro cannot not think.
        _ => null,
    };

    /// <summary>
    /// A little thinking (a long document to sum up precisely, not a proof): left to itself, a model may think for
    /// minutes over a whole course before writing a word. Null: a model that does not think, or one not known.
    /// </summary>
    public static JsonObject? LightThinking(string model) => Version(model) switch
    {
        >= 3 => new JsonObject { ["thinkingLevel"] = "low" },
        >= 2.5 => new JsonObject { ["thinkingBudget"] = 1024 },
        _ => null,
    };

    /// <summary>
    /// The answer to the conversation <paramref name="turns"/> (the last one the user's), written piece by piece as
    /// Gemini streams it, the model thinking as little as it may (<paramref name="quick"/>, see <see cref="QuickThinking"/>).
    /// Throws <see cref="GeminiException"/> with a message for the user.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        string key, string model, IReadOnlyList<GeminiTurn> turns, string instructions, bool quick = true,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        var contents = new JsonArray();
        foreach (var turn in turns)
        {
            contents.Add(new JsonObject
            {
                ["role"] = turn.FromUser ? "user" : "model",
                ["parts"] = Parts(turn),
            });
        }

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = instructions }) },
            ["contents"] = contents,
        };
        if (quick && QuickThinking(model) is { } thinking)
        {
            body["generationConfig"] = new JsonObject { ["thinkingConfig"] = thinking };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-goog-api-key", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await Send(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // One event per line: "data: {…}", the next piece of the answer.
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string piece = Piece(line[5..].Trim());
            if (piece.Length > 0)
            {
                yield return piece;
            }
        }
    }

    /// <summary>
    /// An answer to <paramref name="parts"/> (a document, then what is asked), written as JSON that follows
    /// <paramref name="schema"/> (Gemini's structured output: it cannot answer otherwise), streamed piece by piece (a long
    /// summary, many cards: what is done so far can be followed). <paramref name="temperature"/>: low to keep to the text,
    /// higher for variety (Gemini 2 only, see <see cref="TakesTemperature"/>); <paramref name="thinking"/>: how much the model thinks first (see <see cref="QuickThinking"/>,
    /// <see cref="LightThinking"/>; <c>null</c>, as it likes). Throws <see cref="GeminiException"/> with a message for the
    /// user, also when the answer was cut short.
    /// </summary>
    public async IAsyncEnumerable<string> StreamJsonAsync(
        string key, string model, string instructions, JsonArray parts, JsonObject schema, double temperature, JsonObject? thinking,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        var body = JsonRequest(model, instructions, parts, schema, temperature, thinking);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-goog-api-key", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await Send(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string json = line[5..].Trim();
            string piece = Piece(json);
            if (piece.Length > 0)
            {
                yield return piece;
            }

            if (json.Contains("\"MAX_TOKENS\"", StringComparison.Ordinal))
            {
                throw new GeminiException(Loc.T("La réponse était trop longue et a été coupée : essayez sur une partie du cours."));
            }
        }
    }

    /// <summary>
    /// The body of <see cref="StreamJsonAsync"/>. The schema and the thinking go as copies: a node has one parent, and the
    /// same ones go with every try (a quota waited for, a connection dropped, the next model).
    /// </summary>
    internal static JsonObject JsonRequest(
        string model, string instructions, JsonArray parts, JsonObject schema, double temperature, JsonObject? thinking)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = instructions }) },
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = schema.DeepClone(),
            },
        };
        if (TakesTemperature(model))
        {
            body["generationConfig"]!["temperature"] = temperature;
        }

        if (thinking is not null)
        {
            body["generationConfig"]!["thinkingConfig"] = thinking.DeepClone();
        }

        return body;
    }

    /// <summary>
    /// Sends a document too large to go with a question (Gemini's File API, resumable upload): Google keeps it 48 hours.
    /// Returns its address, to give as <c>fileData</c>, once Google has it ready.
    /// </summary>
    public async Task<string> UploadAsync(string key, byte[] data, string mimeType, string name, CancellationToken cancellation = default)
    {
        using var start = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/upload/v1beta/files")
        {
            Content = new StringContent(new JsonObject { ["file"] = new JsonObject { ["display_name"] = name } }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        start.Headers.Add("x-goog-api-key", key);
        start.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        start.Headers.Add("X-Goog-Upload-Command", "start");
        start.Headers.Add("X-Goog-Upload-Header-Content-Length", data.Length.ToString(CultureInfo.InvariantCulture));
        start.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);
        string address;
        using (var started = await Send(start, HttpCompletionOption.ResponseContentRead, cancellation))
        {
            address = started.Headers.TryGetValues("X-Goog-Upload-URL", out var values) ? values.First()
                : throw new GeminiException(Loc.T("Google n'a pas accepté le document."));
        }

        using var upload = new HttpRequestMessage(HttpMethod.Post, address) { Content = new ByteArrayContent(data) };
        upload.Headers.Add("X-Goog-Upload-Offset", "0");
        upload.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        using var uploaded = await Send(upload, HttpCompletionOption.ResponseContentRead, cancellation);
        using var file = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync(cancellation));
        var info = file.RootElement.GetProperty("file");
        string uri = info.GetProperty("uri").GetString() ?? throw new GeminiException(Loc.T("Google n'a pas accepté le document."));
        string id = info.GetProperty("name").GetString() ?? "";

        // Google prepares it (a PDF is read page by page): asked again until it is ready, a minute at most.
        for (int i = 0; i < 30 && info.TryGetProperty("state", out var state) && state.GetString() == "PROCESSING"; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
            using var ask = new HttpRequestMessage(HttpMethod.Get, id);
            ask.Headers.Add("x-goog-api-key", key);
            using var asked = await Send(ask, HttpCompletionOption.ResponseContentRead, cancellation);
            using var now = JsonDocument.Parse(await asked.Content.ReadAsStringAsync(cancellation));
            if (now.RootElement.TryGetProperty("state", out var current) && current.GetString() == "FAILED")
            {
                throw new GeminiException(Loc.T("Google n'a pas pu lire le document."));
            }

            if (now.RootElement.TryGetProperty("state", out current) && current.GetString() != "PROCESSING")
            {
                break;
            }
        }

        return uri;
    }

    /// <summary>A turn's parts: its images (inline, base64), then its text, if any.</summary>
    private static JsonArray Parts(GeminiTurn turn)
    {
        var parts = new JsonArray();
        foreach (var image in turn.Images ?? [])
        {
            parts.Add(new JsonObject
            {
                ["inlineData"] = new JsonObject { ["mimeType"] = image.MimeType, ["data"] = Convert.ToBase64String(image.Data) },
            });
        }

        if (turn.Text.Length > 0 || parts.Count == 0)
        {
            parts.Add(new JsonObject { ["text"] = turn.Text });
        }

        return parts;
    }

    /// <summary>The text of one streamed event; why it stopped, if it was refused.</summary>
    private static string Piece(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw Refusal(error.TryGetProperty("code", out var code) && code.TryGetInt32(out int status) ? status : 0, Describe(error));
        }

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            return root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out _)
                ? throw new GeminiException(Loc.T("Gemini a refusé de répondre à cette question."))
                : "";
        }

        var candidate = candidates[0];
        var text = new StringBuilder();
        if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var piece) && !(part.TryGetProperty("thought", out var thought) && thought.GetBoolean()))
                {
                    text.Append(piece.GetString());
                }
            }
        }

        if (text.Length == 0 && candidate.TryGetProperty("finishReason", out var reason) && reason.GetString() is "SAFETY" or "PROHIBITED_CONTENT")
        {
            throw new GeminiException(Loc.T("Gemini a refusé de répondre à cette question."));
        }

        return text.ToString();
    }

    /// <summary>Sends <paramref name="request"/>; a failure becomes a message for the user.</summary>
    private static async Task<HttpResponseMessage> Send(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellation)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, completion, cancellation);
        }
        catch (HttpRequestException)
        {
            throw new GeminiException(Loc.T("Pas de connexion à Google : vérifiez Internet."));
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        string body = await response.Content.ReadAsStringAsync(cancellation);
        response.Dispose();
        string? said = null;
        TimeSpan? retry = null;
        bool daily = false;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                said = Describe(error);
                retry = RetryDelay(error);
                daily = PerDay(error);
            }
        }
        catch (JsonException)
        {
        }

        bool keyRefused = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || (response.StatusCode == HttpStatusCode.BadRequest && said?.Contains("API key", StringComparison.OrdinalIgnoreCase) == true);
        if (keyRefused)
        {
            throw new GeminiException(Loc.T("Clé API refusée par Google : vérifiez-la dans les paramètres."));
        }

        throw Refusal((int)response.StatusCode, said, retry, daily);
    }

    /// <summary>
    /// Why Google said no (<paramref name="code"/>, its HTTP status; <paramref name="said"/>, its message), for the user.
    /// No quota for this model (a fresh key has none for some: Google says "limit: 0"), no such model, or the model
    /// overloaded for now (503, "high demand", sometimes for long): another one may answer.
    /// </summary>
    private static GeminiException Refusal(int code, string? said, TimeSpan? retry = null, bool daily = false) => code switch
    {
        429 => new GeminiException(Loc.T("Quota Gemini atteint : réessayez dans un moment."), modelUnusable: true,
            lasting: said?.Contains("limit: 0", StringComparison.Ordinal) == true)
        {
            RetryAfter = retry,
            Daily = daily,
        },
        404 => new GeminiException(Loc.T("Ce modèle n'est pas disponible : choisissez-en un autre dans les paramètres."), modelUnusable: true, lasting: true),
        500 or 503 or 504 => new GeminiException(Loc.T("Gemini est surchargé en ce moment : réessayez dans un moment."), modelUnusable: true),
        400 => new GeminiException(said ?? Loc.F("Gemini ne répond pas ({0}).", 400), malformed: true),
        _ => new GeminiException(said ?? Loc.F("Gemini ne répond pas ({0}).", code)),
    };

    /// <summary>
    /// Whether the quota spent is a day's: a detail's violation whose « quotaId » counts per day
    /// (« GenerateRequestsPerDayPerProjectPerModel-FreeTier »).
    /// </summary>
    private static bool PerDay(JsonElement error) =>
        error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array
        && details.EnumerateArray().Any(detail =>
            detail.ValueKind == JsonValueKind.Object
            && detail.TryGetProperty("violations", out var violations) && violations.ValueKind == JsonValueKind.Array
            && violations.EnumerateArray().Any(violation =>
                violation.ValueKind == JsonValueKind.Object
                && violation.TryGetProperty("quotaId", out var id) && id.GetString() is { } quota
                && quota.Contains("PerDay", StringComparison.OrdinalIgnoreCase)));

    /// <summary>When Google says a spent quota may be asked again: a detail's « retryDelay » (« 37s », « 12.5s »).</summary>
    private static TimeSpan? RetryDelay(JsonElement error)
    {
        if (!error.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var detail in details.EnumerateArray())
        {
            if (detail.TryGetProperty("retryDelay", out var delay) && delay.GetString() is { } text && text.EndsWith('s')
                && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether to set a temperature for <paramref name="model"/>: not for Gemini 3 and later, which Google keeps at its
    /// default (lower, they may loop and do worse).
    /// </summary>
    public static bool TakesTemperature(string model) => Version(model) is > 0 and < 3;

    private static string Describe(JsonElement error) =>
        error.TryGetProperty("message", out var message) ? message.GetString() ?? "" : error.ToString();
}
