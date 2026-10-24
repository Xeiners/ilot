using System.Runtime.CompilerServices;
using Ilot.Localization;
using Ilot.Settings;

namespace Ilot.Modules.Assistant;

/// <summary>A message of the conversation: the user's question (and the images they joined), or the assistant's answer as it is written.</summary>
public sealed class ChatMessage(bool fromUser, string text, IReadOnlyList<ChatImage>? images = null)
{
    public bool FromUser { get; } = fromUser;

    public IReadOnlyList<ChatImage> Images { get; } = images ?? [];

    public string Text { get; internal set; } = text;

    /// <summary>Not an answer: what went wrong (no key, no network…).</summary>
    public bool IsError { get; internal set; }

    /// <summary>Still being written.</summary>
    public bool IsWriting { get; internal set; }
}

/// <summary>Where the assistant is at, which its mascot shows.</summary>
public enum AssistantState
{
    Idle,
    Thinking,
    Talking,
    Done,
    Failed,
}

/// <summary>
/// The conversation with the assistant (Gemini): kept while the notch runs, until a new one is started; nothing is
/// written to the disk. One question at a time; its answer streams in piece by piece.
/// </summary>
public sealed class AssistantConversation(GeminiClient gemini, ApiKeyStore keys, AppSettings settings, AssistantHistory history)
{
    private readonly List<ChatMessage> _messages = [];

    /// <summary>This conversation, in the history (see <see cref="Remember"/>): a new one at each fresh start.</summary>
    private Guid _id = Guid.NewGuid();

    /// <summary>The conversations kept (see <see cref="Open"/>).</summary>
    public AssistantHistory History => history;
    private CancellationTokenSource? _answering;
    private Task<IReadOnlyList<string>>? _listing;
    private string? _listedFor;
    private readonly Dictionary<string, DateTime> _resting = [];
    private readonly HashSet<string> _thinksItsWay = [];
    private static readonly TimeSpan RestAfterRefusal = TimeSpan.FromMinutes(2);

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public AssistantState State { get; private set; }

    public bool IsBusy => _answering is not null;

    /// <summary>Raised on the UI thread when a message comes, or the one being written grows.</summary>
    public event EventHandler<ChatMessage>? Changed;

    /// <summary>Raised on the UI thread when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>A fresh start (an answer being written is stopped): this conversation stays in the history, a new one begins.</summary>
    public void Clear()
    {
        _answering?.Cancel();
        _answering = null;
        _messages.Clear();
        _id = Guid.NewGuid();
        _voiceQuestion = null;
        _voiceAnswer = null;
        SetState(AssistantState.Idle);
        Changed?.Invoke(this, new ChatMessage(true, ""));
    }

    /// <summary>
    /// A conversation from the history, opened to go on with it: it takes the place of this one (kept already), and what is
    /// said next is added to it. Images were not kept: a question that was only one says so.
    /// </summary>
    public void Open(SavedConversation saved)
    {
        _answering?.Cancel();
        _answering = null;
        _messages.Clear();
        _voiceQuestion = null;
        _voiceAnswer = null;
        foreach (var message in saved.Messages)
        {
            string text = message.Text.Length > 0 ? message.Text : Loc.T("(une image)");
            _messages.Add(new ChatMessage(message.FromUser, text));
        }

        _id = saved.Id;
        SetState(AssistantState.Idle);
        Changed?.Invoke(this, new ChatMessage(true, ""));
    }

    /// <summary>
    /// Keeps this conversation in the history as it is now (each time an answer is done): its first question as title,
    /// its messages written (not those being written, nor what went wrong).
    /// </summary>
    private void Remember()
    {
        var messages = _messages
            .Where(message => !message.IsWriting && !message.IsError && (message.Text.Length > 0 || message.Images.Count > 0))
            .Select(message => new SavedMessage(message.FromUser, message.Text, message.Images.Count))
            .ToList();
        if (messages.FirstOrDefault(message => message.FromUser) is not { } first)
        {
            return;
        }

        string title = first.Text.Length == 0 ? Loc.T("Une image") : first.Text.ReplaceLineEndings(" ").Trim();
        if (title.Length > TitleLength)
        {
            title = title[..TitleLength].TrimEnd() + "…";
        }

        history.Keep(new SavedConversation(_id, title, DateTimeOffset.Now, messages));
    }

    private const int TitleLength = 70;

    // ── Talking (see Voice/AssistantVoice) ──────────────────────────────────────────────────────────────────

    private ChatMessage? _voiceQuestion;
    private ChatMessage? _voiceAnswer;

    /// <summary>
    /// More of what the user said aloud (or typed while talking), written out: into their question being said, a new one
    /// if Gemini was answering (they talked over it, or asked the next thing).
    /// </summary>
    internal void HearUser(string text)
    {
        if (_voiceAnswer is not null)
        {
            EndVoiceTurn();
        }

        if (_voiceQuestion is null)
        {
            _voiceQuestion = new ChatMessage(true, "");
            _messages.Add(_voiceQuestion);
        }

        _voiceQuestion.Text = Join(_voiceQuestion.Text, text);
        Changed?.Invoke(this, _voiceQuestion);
    }

    /// <summary>More of what Gemini said aloud, written out: its answer, being written as it speaks.</summary>
    internal void HearModel(string text)
    {
        _voiceQuestion = null; // Answered: what the user says next is another question.
        if (_voiceAnswer is null)
        {
            _voiceAnswer = new ChatMessage(false, "") { IsWriting = true };
            _messages.Add(_voiceAnswer);
        }

        _voiceAnswer.Text = Join(_voiceAnswer.Text, text);
        Changed?.Invoke(this, _voiceAnswer);
    }

    /// <summary>Gemini is done answering aloud (or the talk is over): its answer is written.</summary>
    internal void EndVoiceTurn()
    {
        _voiceQuestion = null;
        if (_voiceAnswer is { } answer)
        {
            _voiceAnswer = null;
            answer.Text = answer.Text.Trim();
            answer.IsWriting = false;
            Changed?.Invoke(this, answer);
            Remember();
        }
    }

    /// <summary>Something went wrong outside a question (the voice: no microphone, no model…): said in the conversation.</summary>
    internal void Fail(string message)
    {
        var failure = new ChatMessage(false, message) { IsError = true };
        _messages.Add(failure);
        Changed?.Invoke(this, failure);
        SetState(AssistantState.Failed);
    }

    /// <summary>A written-out piece of speech after what came before (pieces come with their own spaces, or none).</summary>
    private static string Join(string before, string piece) =>
        before.Length == 0 ? piece.TrimStart() : before + piece;

    /// <summary>Asks <paramref name="question"/> (about <paramref name="images"/>, if any), and writes the answer as it comes.</summary>
    public async void Ask(string question, IReadOnlyList<ChatImage>? images = null)
    {
        question = question.Trim();
        images ??= [];
        if ((question.Length == 0 && images.Count == 0) || IsBusy)
        {
            return;
        }

        var asked = new ChatMessage(true, question, images);
        _messages.Add(asked);
        Changed?.Invoke(this, asked);
        var answer = new ChatMessage(false, "") { IsWriting = true };
        _messages.Add(answer);
        Changed?.Invoke(this, answer);

        var answering = new CancellationTokenSource();
        _answering = answering;
        SetState(AssistantState.Thinking);
        try
        {
            if (keys.Key is not { Length: > 0 } key)
            {
                throw new GeminiException(Loc.T("Ajoutez votre clé API Gemini dans les paramètres (Assistant)."));
            }

            var turns = _messages.Where(message => message != answer && !message.IsError)
                .Select(message => new GeminiTurn(message.FromUser, message.Text, message.Images.Select(image => image.Image).ToList()))
                .ToList();
            await AnswerAsync(key, turns, answer, answering.Token);

            if (answer.Text.Length == 0)
            {
                throw new GeminiException(Loc.T("Gemini n'a rien répondu."));
            }

            SetState(AssistantState.Done);
        }
        catch (GeminiException ex)
        {
            answer.Text = ex.Message;
            answer.IsError = true;
            SetState(AssistantState.Failed);
        }
        catch (OperationCanceledException)
        {
            return; // Started afresh meanwhile: that answer is not wanted any more.
        }
        finally
        {
            answer.IsWriting = false;
            if (_answering == answering)
            {
                _answering = null;
            }

            answering.Dispose();
        }

        Changed?.Invoke(this, answer);
        Remember();
    }

    /// <summary>
    /// The assistant opens: on « Automatique », the models the key may use are looked up now, in the background (the
    /// connection to Google is made at the same time), rather than once the question is asked.
    /// </summary>
    public void Prepare()
    {
        if (string.IsNullOrWhiteSpace(settings.AssistantModel) && keys.Key is { Length: > 0 } key)
        {
            _ = ListAsync(key).ContinueWith(listing => _ = listing.Exception, TaskScheduler.Default); // Its failure is told when asked.
        }
    }

    /// <summary>
    /// Writes the answer into <paramref name="answer"/> as it streams. With the model chosen in the settings, from it; on
    /// « Automatique », from the first model that answers (see <see cref="CandidatesAsync"/>): a key has no free quota
    /// for some models (the newest often), so one that says so before writing a word is passed for the next, and left
    /// aside for a while (see <see cref="Resting"/>) rather than asked again at each question.
    /// </summary>
    private async Task AnswerAsync(string key, List<GeminiTurn> turns, ChatMessage answer, CancellationToken cancellation)
    {
        // The model chosen in the settings: it first, and only it, unless it cannot answer now (overloaded, no quota):
        // then the others rather than nothing.
        string chosen = settings.AssistantModel.Trim();
        if (chosen.Length > 0 && !Resting(chosen))
        {
            try
            {
                await StreamAsync(key, chosen, turns, answer, cancellation);
                return;
            }
            catch (GeminiException ex) when (ex.ModelUnusable && answer.Text.Length == 0)
            {
                Rest(chosen, ex);
            }
        }

        bool tried = false;
        await foreach (string model in CandidatesAsync(key, cancellation))
        {
            if (model == chosen)
            {
                continue;
            }

            tried = true;
            try
            {
                await StreamAsync(key, model, turns, answer, cancellation);
                settings.AssistantAnswered = model;
                return;
            }
            catch (GeminiException ex) when (ex.ModelUnusable && answer.Text.Length == 0)
            {
                Rest(model, ex);
            }
        }

        throw new GeminiException(tried || _resting.Count > 0
            ? Loc.T("Aucun modèle Gemini ne peut répondre pour cette clé en ce moment (quota ou surcharge) : réessayez dans un moment.")
            : Loc.T("Aucun modèle Gemini disponible pour cette clé."));
    }

    /// <summary>
    /// <paramref name="model"/> cannot answer: set aside. No quota at all for it on this key, or no such model: for this
    /// run. Overloaded, or its quota for the minute or the day spent: a little while.
    /// </summary>
    private void Rest(string model, GeminiException why)
    {
        _resting[model] = why.Lasting ? DateTime.MaxValue : DateTime.UtcNow + RestAfterRefusal;
        if (settings.AssistantAnswered == model)
        {
            settings.AssistantAnswered = "";
        }
    }

    /// <summary>
    /// Streams <paramref name="model"/>'s answer into <paramref name="answer"/>, the model thinking as little as it may (the
    /// first word comes in a second rather than after several); a model that refuses that setting answers without it.
    /// </summary>
    private async Task StreamAsync(string key, string model, List<GeminiTurn> turns, ChatMessage answer, CancellationToken cancellation)
    {
        bool quick = !_thinksItsWay.Contains(model) && GeminiClient.QuickThinking(model) is not null;
        try
        {
            await Write(quick);
        }
        catch (GeminiException ex) when (quick && ex.Malformed && answer.Text.Length == 0)
        {
            _thinksItsWay.Add(model);
            await Write(quick: false);
        }

        async Task Write(bool quick)
        {
            await foreach (string piece in gemini.StreamAsync(key, model, turns, Instructions(), quick, cancellation))
            {
                answer.Text += piece;
                SetState(AssistantState.Talking);
                Changed?.Invoke(this, answer);
            }
        }
    }

    /// <summary>
    /// The models to try on « Automatique »: the one that answered last (remembered between runs: asked at once, before
    /// the list is even known), then the others the key may use, best first (see <see cref="GeminiClient.Candidates"/>);
    /// none that is resting.
    /// </summary>
    private async IAsyncEnumerable<string> CandidatesAsync(string key, [EnumeratorCancellation] CancellationToken cancellation)
    {
        string answered = settings.AssistantAnswered;
        if (answered.Length > 0 && !Resting(answered))
        {
            yield return answered;
        }

        foreach (string model in (await ListAsync(key).WaitAsync(cancellation)).Where(model => model != answered))
        {
            if (!Resting(model))
            {
                yield return model;
            }
        }
    }

    /// <summary>A model set aside, until when (see <see cref="AnswerAsync"/>).</summary>
    private bool Resting(string model) => _resting.TryGetValue(model, out var until) && DateTime.UtcNow < until;

    /// <summary>The models <paramref name="key"/> may use, best first: looked up once (again if it failed, or for another key).</summary>
    private Task<IReadOnlyList<string>> ListAsync(string key)
    {
        if (_listing is { } listing && _listedFor == key && !listing.IsFaulted && !listing.IsCanceled)
        {
            return listing;
        }

        if (_listedFor != key)
        {
            _listedFor = key;
            _resting.Clear();
        }

        return _listing = List();

        async Task<IReadOnlyList<string>> List() => GeminiClient.Candidates(await gemini.ListModelsAsync(key));
    }

    /// <summary>Who it is, and how it answers: briefly, plainly, in the user's language.</summary>
    private static string Instructions() => Loc.T("Tu t'appelles Notch : tu es la mascotte d'Ilot, un petit assistant qui vit dans l'encoche de l'écran. Réponds brièvement et clairement, avec chaleur, dans la langue de la question. Mets en forme en Markdown simple (gras, listes, `code`, blocs de code) seulement quand cela aide.");

    private void SetState(AssistantState state)
    {
        if (state != State)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
