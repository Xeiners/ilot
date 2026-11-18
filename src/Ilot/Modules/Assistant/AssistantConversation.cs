using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Ilot.Localization;
using Ilot.Settings;

namespace Ilot.Modules.Assistant;

/// <summary>
/// A message of the conversation: the user's question (and the images they joined), the assistant's answer as it is
/// written, or something it did on the PC (<see cref="Action"/>: a tool it called).
/// </summary>
public sealed class ChatMessage(bool fromUser, string text, IReadOnlyList<ChatImage>? images = null, AssistantAction? action = null)
{
    public bool FromUser { get; } = fromUser;

    public IReadOnlyList<ChatImage> Images { get; } = images ?? [];

    public string Text { get; internal set; } = text;

    /// <summary>Not an answer: what went wrong (no key, no network…).</summary>
    public bool IsError { get; internal set; }

    /// <summary>Still being written.</summary>
    public bool IsWriting { get; internal set; }

    /// <summary>An answer the user stopped (« Interrompre », Escape): what it had written (or said) so far, said to be cut short.</summary>
    public bool Stopped { get; internal set; }

    /// <summary>Something the assistant does on the PC (neither said to Gemini again, nor kept in the history).</summary>
    public AssistantAction? Action { get; } = action;

    /// <summary>Changes each time the message does other than by its text being written (an action going on): drawn again.</summary>
    public int Revision { get; internal set; }
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
/// The conversation with the assistant (Gemini): kept while the notch runs, until a new one is started (and in the
/// history, see <see cref="AssistantHistory"/>). One question at a time; its answer streams in piece by piece. With the
/// actions on (the settings), Gemini may use the assistant's tools to answer (see <see cref="AssistantTools"/>): it asks,
/// the tool runs here (once the user allows it, for what must be), what it gave goes back, and Gemini goes on.
/// </summary>
public sealed class AssistantConversation(GeminiClient gemini, ApiKeyStore keys, AppSettings settings, AssistantHistory history, IAssistantTools tools, FileJournal? journal = null)
{
    /// <summary>Rounds of tools for one question at most (Gemini asking again and again would never answer).</summary>
    private const int MostRounds = 8;

    private readonly List<ChatMessage> _messages = [];

    /// <summary>Models that refused the tools (Google said the request was wrong with them): asked without, this run.</summary>
    private readonly HashSet<string> _noTools = [];

    /// <summary>A tool ran for the question being answered: no other model is asked it again (it would act twice).</summary>
    private bool _acted;

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

    /// <summary>A written answer is coming (thought about, written, or its tools running): the user may stop it (see <see cref="Stop"/>).</summary>
    public bool IsAnswering => IsBusy && State is AssistantState.Thinking or AssistantState.Talking;

    /// <summary>The written answer being written, while it is (see <see cref="Stop"/>).</summary>
    private ChatMessage? _writing;

    /// <summary>
    /// The written answer stopped by the user (« Interrompre », Escape): Gemini is left at once, a tool waiting for their
    /// yes or running is called off (« Annulé »), and what it wrote so far stays, said to be cut short (alone, if it had
    /// not begun). The conversation goes on from there; nothing else is asked of Gemini meanwhile.
    /// </summary>
    public void Stop()
    {
        if (_answering is not { } answering || _writing is not { } answer)
        {
            return;
        }

        _answering = null;
        _writing = null;
        answering.Cancel();
        answer.IsWriting = false;
        answer.Stopped = true;
        answer.Text = answer.Text.TrimEnd();
        answer.Revision++;
        SetState(AssistantState.Idle);
        Changed?.Invoke(this, answer);
        Remember();
    }

    /// <summary>Raised on the UI thread when a message comes, or the one being written grows.</summary>
    public event EventHandler<ChatMessage>? Changed;

    /// <summary>Raised on the UI thread when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>A fresh start (an answer being written is stopped): this conversation stays in the history, a new one begins.</summary>
    public void Clear()
    {
        _answering?.Cancel();
        _answering = null;
        _writing = null;
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
        _writing = null;
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
            .Where(message => message.Action is null && !message.IsWriting && !message.IsError && (message.Text.Length > 0 || message.Images.Count > 0))
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
        _voiceCaptures = 0; // They spoke: the screen may be looked at again, once.
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

    /// <summary>
    /// The user stopped its answer aloud (« Interrompre », Escape): what it said so far is its message, said to be cut
    /// short; its voice cut (<paramref name="speaking"/>) while the words were all written out already, that last answer;
    /// a stop before it said a word, after their question, a line of its own (a stopped action says « Annulé » already).
    /// </summary>
    internal void StopVoiceTurn(bool speaking)
    {
        var stopped = _voiceAnswer;
        if (stopped is null && _messages.LastOrDefault() is { IsError: false, Action: null } last && (speaking || last.FromUser))
        {
            stopped = last.FromUser ? new ChatMessage(false, "") : last;
            if (last.FromUser)
            {
                _messages.Add(stopped);
            }
        }

        if (stopped is null)
        {
            _voiceQuestion = null;
            return;
        }

        stopped.Stopped = true;
        stopped.Revision++;
        if (stopped == _voiceAnswer)
        {
            EndVoiceTurn(); // Written, and kept in the history.
            return;
        }

        _voiceQuestion = null;
        Changed?.Invoke(this, stopped);
    }

    /// <summary>Something went wrong outside a question (the voice: no microphone, no model…): said in the conversation.</summary>
    internal void Fail(string message)
    {
        var failure = new ChatMessage(false, message) { IsError = true };
        _messages.Add(failure);
        Changed?.Invoke(this, failure);
        SetState(AssistantState.Failed);
    }

    /// <summary>
    /// What is told to Gemini of the conversation (written, and aloud): what was said, not what went wrong nor what was
    /// done on the PC (it went with its question), nor an answer stopped before its first word.
    /// </summary>
    internal static bool Said(ChatMessage message) =>
        !message.IsError && message.Action is null && (message.Text.Length > 0 || message.Images.Count > 0);

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
        _writing = answer;
        _acted = false;
        SetState(AssistantState.Thinking);
        try
        {
            if (keys.Key is not { Length: > 0 } key)
            {
                throw new GeminiException(Loc.T("Ajoutez votre clé API Gemini dans les paramètres (Assistant)."));
            }

            // What was said (what was done on the PC went with its question: Gemini has its answer; an answer stopped
            // before its first word says nothing).
            var turns = _messages.Where(message => message != answer && Said(message))
                .Select(message => new GeminiTurn(message.FromUser, message.Text, message.Images.Select(image => image.Image).ToList()))
                .ToList();
            await AnswerAsync(key, turns, answer, answering.Token);
            answering.Token.ThrowIfCancellationRequested(); // Stopped as it ended: the stop has said so already.

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
            return; // Stopped (see Stop), or started afresh meanwhile: that answer is not wanted any more.
        }
        finally
        {
            answer.IsWriting = false;
            if (_answering == answering)
            {
                _answering = null;
            }

            if (_writing == answer)
            {
                _writing = null;
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
            catch (GeminiException ex) when (ex.ModelUnusable && answer.Text.Length == 0 && !_acted)
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
            catch (GeminiException ex) when (ex.ModelUnusable && answer.Text.Length == 0 && !_acted)
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
    /// first word comes in a second rather than after several). With the actions on, it may ask for its tools instead:
    /// they run (see <see cref="RunAsync"/>), the call and what they gave are added to <paramref name="turns"/>, and it is
    /// asked again, until it answers. A model that refuses a setting (the thinking, the tools) answers without it.
    /// </summary>
    private async Task StreamAsync(string key, string model, List<GeminiTurn> turns, ChatMessage answer, CancellationToken cancellation)
    {
        bool quick = !_thinksItsWay.Contains(model) && GeminiClient.QuickThinking(model) is not null;
        bool acting = settings.AssistantActions && !_noTools.Contains(model);
        for (int round = 1; ; round++)
        {
            List<GeminiCall> calls;
            List<JsonObject> parts;
            try
            {
                (calls, parts) = await Write(quick, acting, last: round > MostRounds);
            }
            catch (GeminiException ex) when (ex.Malformed && answer.Text.Length == 0 && !_acted && (quick || acting))
            {
                // Google said the request was wrong: what it names (the tools, mostly), else the thinking, is left out.
                bool tools = acting && (!quick || ex.Message.Contains("function", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("tool", StringComparison.OrdinalIgnoreCase));
                if (tools)
                {
                    _noTools.Add(model);
                    acting = false;
                }
                else
                {
                    _thinksItsWay.Add(model);
                    quick = false;
                }

                round--;
                continue;
            }

            if (calls.Count == 0)
            {
                return;
            }

            // Its tools: what it asked for, then what each gave (with the screen, if it was captured), and it goes on.
            _acted = true;
            turns.Add(new GeminiTurn(false, "", Parts: [.. parts]));
            var given = new JsonArray();
            var pictures = new List<ChatImage>();
            foreach (var call in calls)
            {
                var (response, picture) = await RunAsync(call, answer, cancellation);
                var function = new JsonObject { ["name"] = call.Name, ["response"] = response };
                if (call.Id is { } id)
                {
                    function["id"] = id;
                }

                given.Add(new JsonObject { ["functionResponse"] = function });
                if (picture is not null)
                {
                    pictures.Add(picture);
                }
            }

            foreach (var picture in pictures)
            {
                given.Add(new JsonObject
                {
                    ["inlineData"] = new JsonObject { ["mimeType"] = picture.Image.MimeType, ["data"] = Convert.ToBase64String(picture.Image.Data) },
                });
            }

            turns.Add(new GeminiTurn(true, "", Parts: given));
            SetState(AssistantState.Thinking);
        }

        // One round: the text shown as it comes, the tools asked for and the parts as sent, kept to be given back.
        // The last round allowed is told to answer, without its tools.
        async Task<(List<GeminiCall> Calls, List<JsonObject> Parts)> Write(bool quick, bool acting, bool last)
        {
            var calls = new List<GeminiCall>();
            var parts = new List<JsonObject>();
            bool begun = false;
            await foreach (var chunk in gemini.ChatAsync(key, model, turns, Instructions(acting), quick, acting ? tools.Declarations : null, answerOnly: last, cancellation))
            {
                cancellation.ThrowIfCancellationRequested(); // Stopped: not a word more (a piece may have been read already).
                calls.AddRange(chunk.Calls);
                Keep(parts, chunk.Parts);
                if (chunk.Text.Length == 0)
                {
                    continue;
                }

                // What it says after its tools follows what it said before them, a paragraph apart.
                if (!begun && answer.Text.Length > 0 && chunk.Text.Trim().Length > 0)
                {
                    answer.Text = answer.Text.TrimEnd() + "\n\n";
                }

                begun = true;
                answer.Text += chunk.Text;
                SetState(AssistantState.Talking);
                Changed?.Invoke(this, answer);
            }

            return (calls, parts);
        }
    }

    /// <summary>
    /// The parts of a streamed answer, to be given back: the text pieces one after the other joined (a long answer
    /// comes in many), every other part (a call, a part carrying a thought signature) as Google sent it.
    /// </summary>
    private static void Keep(List<JsonObject> parts, IReadOnlyList<JsonObject> more)
    {
        foreach (var part in more)
        {
            if (part.Count == 1 && part["text"] is JsonValue text && parts.Count > 0 && parts[^1].Count == 1 && parts[^1]["text"] is JsonValue before)
            {
                parts[^1]["text"] = before.GetValue<string>() + text.GetValue<string>();
            }
            else
            {
                parts.Add(part);
            }
        }
    }

    /// <summary>
    /// A tool Gemini asked for, shown in the conversation as it goes (before <paramref name="answer"/>, if given; else
    /// last): allowed first by the user when it must be, run, and what it gave, for Gemini; the screen, if it was
    /// captured. Stopped meanwhile (a fresh start, talked over), it says so, and the cancellation goes on up.
    /// </summary>
    private async Task<(JsonObject Response, ChatImage? Picture)> RunAsync(GeminiCall call, ChatMessage? answer, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); // Stopped meanwhile: the next tool asked for is not even shown.
        var plan = tools.Plan(call.Name, call.Args);
        var action = new AssistantAction(plan.Label, plan.Glyph, plan.Detail, plan.Remember);
        var message = new ChatMessage(false, "", action: action);
        int at = answer is null ? -1 : _messages.IndexOf(answer);
        if (at >= 0)
        {
            _messages.Insert(at, message);
        }
        else
        {
            _messages.Add(message);
        }

        Changed?.Invoke(this, message);
        if (plan.Refusal is { } refusal)
        {
            Update(message, ActionState.Failed, refusal);
            return (new JsonObject { ["error"] = refusal }, null);
        }

        try
        {
            // Asked first, unless the user allowed it for good (« Toujours autoriser »: kept in the settings).
            if (plan.Confirm && !(plan.Remember is { } kept && settings.AssistantAllowed.Contains(kept)))
            {
                var allowing = action.AskAsync(cancellation);
                Update(message, ActionState.Asking, "");
                var (allowed, always) = await allowing;
                if (!allowed)
                {
                    Update(message, ActionState.Refused, Loc.T("Refusé"));
                    return (new JsonObject { ["error"] = "The user declined this action. Do not try it again; say so if it matters." }, null);
                }

                if (always && plan.Remember is { } remembered && !settings.AssistantAllowed.Contains(remembered))
                {
                    settings.AssistantAllowed = [.. settings.AssistantAllowed, remembered];
                }
            }

            Update(message, ActionState.Running, "");
            var outcome = await plan.Run(cancellation);
            action.Image = outcome.Image;
            action.Output = outcome.Shown;
            action.Opens = outcome.Opens;
            action.Entry = outcome.Entry;
            action.Said = outcome.Said;
            Update(message, outcome.Failed ? ActionState.Failed : ActionState.Done, outcome.Note);
            return (outcome.Response, outcome.Image);
        }
        catch (OperationCanceledException)
        {
            Update(message, ActionState.Refused, Loc.T("Annulé"));
            throw;
        }
        catch (Exception ex)
        {
            Update(message, ActionState.Failed, Loc.T("Échec"));
            return (new JsonObject { ["error"] = ex.Message }, null);
        }
    }

    // ── Acting while talking (see Voice/AssistantVoice) ─────────────────────────────────────────────────────

    /// <summary>The tools Gemini is told of, with the actions on (the settings); <c>null</c> otherwise.</summary>
    internal JsonArray? Declarations => settings.AssistantActions ? tools.Declarations : null;

    /// <summary>
    /// A tool Gemini asked for aloud: what it said so far is its message, the action comes after it, and what it says
    /// next is a new one, under the action (as it happened).
    /// </summary>
    internal Task<(JsonObject Response, ChatImage? Picture)> ActAsync(GeminiCall call, CancellationToken cancellation)
    {
        // Talking, a model may look at the screen again and again (seen): once per thing the user says, then it is told
        // to use that picture; it may look again when they speak next.
        if (call.Name == "capture_screen" && _voiceCaptures++ >= MostVoiceCaptures)
        {
            return Task.FromResult<(JsonObject, ChatImage?)>((new JsonObject
            {
                ["error"] = "The screen was already captured for this request: use that picture. Do not capture it again unless the user asks you to.",
            }, null));
        }

        EndVoiceTurn();
        return RunAsync(call, null, cancellation);
    }

    /// <summary>Captures of the screen asked for aloud since the user last spoke (see <see cref="ActAsync"/>).</summary>
    private int _voiceCaptures;

    private const int MostVoiceCaptures = 1;

    /// <summary>The screen, shown to Gemini while talking (the bar's button): a line of the conversation, the picture under it.</summary>
    internal void Showed(ChatImage screen)
    {
        EndVoiceTurn();
        var message = new ChatMessage(false, "", action: new AssistantAction(Loc.T("Capture de l'écran"), "", null)
        {
            State = ActionState.Done,
            Outcome = Loc.F("{0} × {1}", screen.Width, screen.Height),
            Image = screen,
        });
        _messages.Add(message);
        Changed?.Invoke(this, message);
    }

    // ── Taking back what it did to the files (see FileJournal) ──────────────────────────────────────────────

    /// <summary>Something it did to the files since Ilot started is there to take back (Ctrl + Z).</summary>
    public bool CanUndo => journal?.LastOfThisRun is not null;

    /// <summary>Ctrl + Z: the latest thing it did to the files since Ilot started, taken back.</summary>
    public Task UndoLastAsync() => journal?.LastOfThisRun is { } entry ? UndoAsync(entry) : Task.CompletedTask;

    /// <summary>
    /// <paramref name="entry"/> taken back (« Annuler » beside its action, Ctrl + Z): what it made to the Recycle Bin,
    /// what it moved back where it was. Its line says so; not in the conversation (shown again), a line of its own.
    /// </summary>
    public async Task UndoAsync(FileEntry entry)
    {
        if (journal is null)
        {
            return;
        }

        string? failed = entry.Undone ? null : await journal.UndoAsync(entry); // Taken back already (from the settings): said so here too.
        string done = entry.Change == FileChange.Move ? Loc.T("Annulé : remis à sa place") : Loc.T("Annulé : mis à la Corbeille");
        var message = _messages.FirstOrDefault(each => each.Action?.Entry == entry);
        if (message is not null)
        {
            Update(message, failed is null ? ActionState.Undone : message.Action!.State, failed ?? done);
            return;
        }

        var line = new ChatMessage(false, "", action: new AssistantAction(Loc.F("Retour en arrière : {0}", Path.GetFileName(entry.To)), "", null)
        {
            State = failed is null ? ActionState.Done : ActionState.Failed,
            Outcome = failed ?? done,
        });
        _messages.Add(line);
        Changed?.Invoke(this, line);
    }

    /// <summary>The action of <paramref name="message"/> where it is at now: drawn again.</summary>
    private void Update(ChatMessage message, ActionState state, string outcome)
    {
        if (message.Action is { } action)
        {
            action.State = state;
            action.Outcome = outcome;
            message.Revision++;
            Changed?.Invoke(this, message);
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

    /// <summary>Who it is, and how it answers: briefly, plainly, in the user's language; with <paramref name="acting"/>, what its tools are for.</summary>
    private static string Instructions(bool acting = false) =>
        Loc.T("Tu t'appelles Notch : tu es la mascotte d'Ilot, un petit assistant qui vit dans l'encoche de l'écran. Réponds brièvement et clairement, avec chaleur, dans la langue de la question. Mets en forme en Markdown simple (gras, listes, `code`, blocs de code) seulement quand cela aide.")
        + (acting ? " " + ToolInstructions : "");

    /// <summary>What its tools are for, and how to use them: written, and aloud (see Voice/AssistantVoice).</summary>
    internal static string ToolInstructions => Loc.T("Tu peux aussi agir sur ce PC Windows avec tes outils, quand l'utilisateur te le demande : voir son écran, chercher ses fichiers, lire ou écrire son presse-papier, ouvrir un fichier, un dossier, une appli, une page web ou un projet dans VS Code, lancer quelques commandes de diagnostic qui ne font que lire, créer un dossier ou un fichier texte, et copier, déplacer, renommer ou supprimer un fichier ou un dossier (rien n'est jamais remplacé ; une suppression va à la Corbeille, seulement s'il la demande clairement), fermer des fenêtres (comme leur croix : liste-les d'abord pour trouver les bonnes) ou en remettre une au premier plan ; ce que tu ouvres passe devant. N'utilise un outil que s'il sert sa demande ; ne regarde l'écran ou le presse-papier que s'il le demande, ou si c'est clairement ce qu'il veut. Certaines actions attendent son accord : s'il refuse, ne recommence pas. Ensuite, dis en une phrase ce que tu as fait, ou pourquoi ça n'a pas marché ; quand une action attend son accord, dis-lui de cliquer sur « Autoriser » dans la notch.");

    private void SetState(AssistantState state)
    {
        if (state != State)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
