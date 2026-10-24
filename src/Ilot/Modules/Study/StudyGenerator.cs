using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ilot.Localization;
using Ilot.Modules.Assistant;
using Ilot.Settings;

namespace Ilot.Modules.Study;

/// <summary>A course's outline: its title and its chapters.</summary>
public sealed record StudyOutline(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("chapters")] List<StudyChapter> Chapters);

/// <summary>A flashcard as Notch writes it: its question, its answer, and its answer in short among three decoys.</summary>
public sealed record GeneratedCard(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("answer")] string Answer,
    [property: JsonPropertyName("choice")] string Choice,
    [property: JsonPropertyName("decoys")] List<string> Decoys,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("chapter")] string Chapter);

/// <summary>The four choices of a card made before them: its answer in short, three decoys (<see cref="Id"/>: the card's).</summary>
public sealed record GeneratedChoices(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("choice")] string Choice,
    [property: JsonPropertyName("decoys")] List<string> Decoys);

/// <summary>What new cards are about: the whole course, one chapter, or the notions the user keeps missing.</summary>
public sealed record CardFocus(StudyChapter? Chapter = null, bool Weak = false)
{
    public static CardFocus All { get; } = new();
}

/// <summary>
/// Turns a course into revisions with Gemini (« Notch », to the user), the key and the model of the assistant: first its
/// outline (quick: its title and parts), then its summary sheet and its flashcards, both at once, each streamed so what
/// is written can be followed (the parts of the sheet, the cards); more cards later, as many as asked, on the whole
/// course, a chapter or the weak points, never again on the notions already carded. Precision first: everything from the
/// document only, its exact terms, its page cited; the answers are JSON that follows a schema (Gemini's structured
/// output), so a card is never half made.
/// </summary>
public sealed partial class StudyGenerator(GeminiClient gemini, ApiKeyStore keys, AppSettings settings)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// A request is given up when nothing has come for this long: before its first word (the document read, a little
    /// thought), then between two pieces (a stalled connection, a model stuck). A long answer may take longer in all.
    /// </summary>
    private static readonly TimeSpan FirstWordWithin = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan PieceWithin = TimeSpan.FromMinutes(2);

    // Every model set aside (quotas spent, busy): the soonest back waited for this many times, this long at most each.
    private const int QuotaWaits = 4;
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(75);

    // A model set aside: busy, for a moment; its quota for the day spent, for a while (asked again now and then).
    private static readonly TimeSpan BusyRest = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DailyRest = TimeSpan.FromHours(1);

    // Models that refused a thinking setting: asked without one.
    private readonly HashSet<string> _thinksItsWay = [];

    private readonly Dictionary<string, DateTime> _resting = [];
    private Task<IReadOnlyList<string>>? _listing;
    private string? _listedFor;

    /// <summary>Whether the assistant has its key (without it, nothing can be made).</summary>
    public bool HasKey => keys.HasKey;

    /// <summary>The course's title and parts (with their pages): quick, the document read, little written.</summary>
    public async Task<StudyOutline> OutlineAsync(StudyDocument document, Action<string>? note, CancellationToken cancellation)
    {
        const string ask = """
            Donne le plan de ce cours.
            1. title : son titre, court (celui du document s'il en a un).
            2. chapters : ses grandes parties, dans l'ordre du cours, chacune avec ses pages ou diapositives (« 3-12 ») ; entre 2 et 12 parties, en regroupant les petites, avec des titres courts et clairs.
            """;
        var outline = await AskAsync<StudyOutline>(document, ask, OutlineSchema(), temperature: 0.1, quick: true, progress: null, note, cancellation);
        var chapters = outline.Chapters.Where(chapter => chapter.Title.Trim().Length > 0)
            .Select(chapter => new StudyChapter(chapter.Title.Trim(), chapter.Pages.Trim())).ToList();
        return outline with { Title = outline.Title.Trim(), Chapters = chapters };
    }

    /// <summary>
    /// The summary sheet (Markdown), part after part as in <paramref name="deck"/>'s outline; <paramref name="parts"/> is
    /// told how many parts are written so far, as they come.
    /// </summary>
    public async Task<string> SummaryAsync(StudyDocument document, StudyDeck deck, Action<int> parts, Action<string>? note, CancellationToken cancellation)
    {
        string unit = document.IsSlides ? "la diapositive (« diapo 4 »)" : "la page (« p. 12 »)";
        string outline = deck.Chapters.Count == 0 ? "ses grandes parties, dans l'ordre du cours (entre 2 et 12), chaque titre suivi de ses pages entre parenthèses : « ## Ostéologie (p. 3-12) »"
            : string.Join("\n", deck.Chapters.Select(chapter => $"- {chapter.Title} ({chapter.Pages})"));
        string ask = $"""
            Rédige la fiche de révision de ce cours, en Markdown, qu'un étudiant pourrait apprendre à la place du cours. Ses parties, dans cet ordre, avec exactement ces titres :
            {outline}

            - un titre « ## » par partie ;
            - des listes à puces courtes et denses ; les termes clés en **gras** ; une définition s'écrit « **Terme** : définition » ;
            - garde tout ce qui se retient pour un examen : définitions, classifications, mécanismes étape par étape, localisations et rapports, structure, innervation et vascularisation, fonctions, valeurs chiffrées et unités, signes cliniques, exceptions ;
            - termine chaque partie par « ### À retenir » (3 à 6 puces, l'essentiel) et, si le cours en contient, « ### À ne pas confondre » (notions du cours proches l'une de l'autre, et ce qui les distingue) ;
            - à la fin de chaque puce, {unit} d'où elle vient, entre parenthèses ;
            - pas de tableau, pas d'image, pas d'introduction ni de conclusion générale.
            """;
        var sheet = await AskAsync<SummaryAnswer>(document, ask, SummarySchema(), temperature: 0.2, quick: false,
            written => parts(PartHeading().Count(written)), note, cancellation);
        return sheet.Summary.Trim().Length > 0 ? sheet.Summary.Trim()
            : throw new GeminiException(Loc.T("Notch n'a rien pu tirer de ce document."));
    }

    /// <summary>
    /// <paramref name="count"/> new flashcards on <paramref name="focus"/>, none on a notion <paramref name="deck"/>
    /// already has a card for; <paramref name="written"/> is told how many are written so far, as they come.
    /// <paramref name="variety"/>: asked again for more, a little more freely worded.
    /// </summary>
    public async Task<IReadOnlyList<GeneratedCard>> CardsAsync(
        StudyDocument document, StudyDeck deck, CardFocus focus, int count, bool variety, Action<int> written, Action<string>? note, CancellationToken cancellation)
    {
        string unit = document.IsSlides ? "la diapositive exacte (« diapo 4 »)" : "la page exacte (« p. 12 »)";
        string chapters = deck.Chapters.Count == 0 ? "(le cours n'a pas de parties)"
            : string.Join("; ", deck.Chapters.Select(chapter => $"« {chapter.Title} »"));
        string on = focus switch
        {
            { Weak: true } => "les notions que l'étudiant oublie (ses cartes ratées, ci-dessous) : de nouvelles cartes qui les reprennent sous d'autres angles, et les notions voisines qui les éclairent"
                + "\n" + string.Join("\n", deck.Cards.Where(card => card.Lapses > 0 || card.State == CardState.Relearning)
                    .OrderByDescending(card => card.Lapses).Take(40).Select(card => $"- {card.Question} → {card.Answer}")),
            { Chapter: { } chapter } => $"la partie « {chapter.Title} » ({chapter.Pages}) uniquement",
            _ => "l'ensemble du cours, en couvrant toutes ses parties selon leur importance",
        };

        var existing = deck.Cards.Select(card => card.Question).ToList();
        string already = existing.Count == 0 ? "" : $"""

            Ces questions existent déjà : ne les répète pas, ni la même notion autrement formulée. Trouve d'autres notions du cours, ou d'autres angles sur elles :
            {string.Join("\n", existing.Select(question => "- " + question))}
            """;

        string ask = $"""
            Écris {count.ToString(CultureInfo.InvariantCulture)} flashcards question-réponse sur {on}.

            Ce qui fait une bonne carte (cours universitaire, médecine comprise) :
            - Une seule notion par carte. Une question précise, sans ambiguïté, qui appelle une seule réponse exacte ; jamais « Parlez de… » ni « Que savez-vous de… ».
            - Une carte se comprend seule : donne le contexte dans la question (« Au niveau du coude, … », « Dans la glycolyse, … »).
            - La question ne contient pas sa réponse, ni d'indice qui la donne.
            - La réponse est courte et exacte, avec les termes du cours : une phrase, ou une liste de 2 à 5 éléments si la question demande une énumération (au-delà, fais plusieurs cartes). Pas de phrase d'introduction.
            - Varie les angles selon ce que le cours contient : définition, localisation et rapports, structure ou composition, innervation, vascularisation, fonction ou action, mécanisme (étapes, dans l'ordre), valeurs chiffrées, classification, différence entre deux notions proches, signes cliniques et applications si le cours en parle.
            - Ce qui compte pour un examen, jamais l'anecdotique (auteurs, mise en page, numérotation).
            - source : {unit} où se trouve la réponse.
            - chapter : la partie du cours, écrite exactement comme dans cette liste : {chapters}.
            {ChoiceRules}
            {already}
            """;

        var answer = await AskAsync<CardsAnswer>(document, ask, CardsSchema(), temperature: variety ? 0.8 : 0.4, quick: false,
            text => written(QuestionKey().Count(text)), note, cancellation);

        // Never twice the same question, nor an empty card; choices only when there are four distinct ones.
        var seen = new HashSet<string>(existing.Select(Normalize));
        return answer.Cards
            .Where(card => card.Question.Trim().Length > 0 && card.Answer.Trim().Length > 0 && seen.Add(Normalize(card.Question)))
            .Select(card =>
            {
                var (choice, decoys) = Tidy(card.Choice, card.Decoys);
                return card with
                {
                    Question = card.Question.Trim(), Answer = card.Answer.Trim(), Choice = choice, Decoys = decoys,
                    Source = card.Source.Trim(), Chapter = card.Chapter.Trim(),
                };
            })
            .ToList();
    }

    /// <summary>
    /// The four choices of <paramref name="cards"/> (made before there were any): for each, its answer in short and three
    /// decoys from the course. Only those that came whole.
    /// </summary>
    public async Task<IReadOnlyList<GeneratedChoices>> ChoicesAsync(StudyDocument document, IReadOnlyList<StudyCard> cards, CancellationToken cancellation)
    {
        string list = string.Join("\n", cards.Select(card => $"- id {card.Id.ToString(CultureInfo.InvariantCulture)} : {card.Question} → {card.Answer}"));
        string ask = $"""
            Pour chacune de ces flashcards du cours, écris ses quatre choix de QCM.
            {ChoiceRules}

            Les cartes (id : question → réponse) :
            {list}
            """;
        var answer = await AskAsync<ChoicesAnswer>(document, ask, ChoicesSchema(), temperature: 0.4, quick: false, progress: null, note: null, cancellation);
        return answer.Cards
            .Select(card => (card, tidy: Tidy(card.Choice, card.Decoys)))
            .Where(each => each.tidy.Choice.Length > 0)
            .Select(each => each.card with { Choice = each.tidy.Choice, Decoys = each.tidy.Decoys })
            .ToList();
    }

    /// <summary>
    /// The course as mind maps: one per important notion, in the course's order, each under its part, short enough to be
    /// taken in at a glance, with its definition and its formulas. From the summary sheet when the course has one (lighter,
    /// so quicker), else from <paramref name="document"/>; <paramref name="written"/> is told how many are drawn so far and
    /// the part the last one is in, as they come (they come in the course's order).
    /// </summary>
    public async Task<IReadOnlyList<MindMap>> MindMapsAsync(
        StudyDocument? document, StudyDeck deck, Action<int, string?> written, Action<string>? note, CancellationToken cancellation)
    {
        string chapters = deck.Chapters.Count == 0 ? "(le cours n'a pas de parties : laisse chapter vide)"
            : string.Join("; ", deck.Chapters.Select(chapter => $"« {chapter.Title} »"));
        string ask = $"""
            Fais les cartes mentales de ce cours, pour le réviser d'un coup d'œil : une carte par notion importante (ce qu'un examen peut demander), et chaque notion importante a la sienne. Dans l'ordre du cours.
            Pour chaque carte :
            - chapter : la partie du cours où est la notion, écrite exactement comme dans cette liste : {chapters} ;
            - notion : la notion, au centre de la carte, en 1 à 5 mots ;
            - definition : sa définition telle que le cours la donne, en une phrase courte (25 mots au plus) ; vide si le cours n'en donne pas ;
            - formulas : les formules du cours sur cette notion (0 à 4 : théorèmes, relations, lois, unités), exactes, écrites en texte avec les symboles Unicode (x², √, π, Σ, ∫, ≤, ≠, →, indices v₀, fractions a/b), jamais en LaTeX ; vide s'il n'y en a pas ;
            - branches : 3 à 6 branches, chacune un aspect de la notion selon ce que le cours en dit (structure, localisation, fonction, mécanisme, propriétés, classification, valeurs, signes, à ne pas confondre…), en 1 à 4 mots (label) ;
            - details : 0 à 3 détails par branche : des mots-clés (2 à 7 mots), ou « Terme : définition courte » pour un terme que le cours définit (12 mots au plus) ; les termes exacts du cours, pas de point final.
            L'essentiel d'abord : les définitions, les formules, les valeurs à retenir. Concis : rien d'inutile, rien qui ne soit dans le cours, rien deux fois d'une carte à l'autre.
            """;
        var sheet = deck.Summary.Trim().Length > 0
            ? new JsonObject { ["text"] = $"<document>\n# {deck.Title}\n\n{deck.Summary}\n</document>" }
            : null;
        var answer = await AskAsync<MindMapsAnswer>(document, ask, MindMapsSchema(), temperature: 0.3, quick: true,
            text => written(NotionKey().Count(text), LastChapter(text)), note, cancellation, sheet);
        return MindMap.Tidy(answer.Maps);
    }

    /// <summary>What makes good choices: the right one short, three decoys from the course that a student could pick.</summary>
    private const string ChoiceRules = """
        - choice : la bonne réponse en version courte (quelques mots, sans phrase), telle qu'elle apparaît parmi quatre choix.
        - decoys : exactement 3 mauvaises réponses, de la même nature et de la même longueur que choice (un autre nerf pour un nerf, une autre valeur pour une valeur…), prises de préférence dans le vocabulaire du cours, plausibles pour un étudiant qui confond, mais fausses sans ambiguïté selon le cours. Jamais « aucune de ces réponses », ni « toutes ».
        """;

    /// <summary>The choices as shown: trimmed, the decoys distinct from each other and from the right one; none unless four.</summary>
    private static (string Choice, List<string> Decoys) Tidy(string? choice, List<string>? decoys)
    {
        string right = choice?.Trim() ?? "";
        var wrong = (decoys ?? []).Select(decoy => decoy.Trim())
            .Where(decoy => decoy.Length > 0 && Normalize(decoy) != Normalize(right))
            .DistinctBy(Normalize)
            .Take(3)
            .ToList();
        return right.Length > 0 && wrong.Count == 3 ? (right, wrong) : ("", []);
    }

    /// <summary>How many cards to start with: thirty (twenty for a very short document); more come on demand.</summary>
    public static int FirstCount(StudyDocument document) => document.Pages is > 0 and <= 10 ? 20 : 30;

    /// <summary>The rules every request follows.</summary>
    private static string Instructions() => """
        Tu es un enseignant expert et rigoureux qui prépare les révisions d'un étudiant à partir de SON cours, joint à la demande.
        Règles absolues :
        - Tout ce que tu écris vient du document. N'ajoute aucune information qui n'y est pas, même exacte ; ne complète pas avec tes connaissances. Si le cours est ambigu, reste au plus près de sa formulation.
        - Exactitude : reprends les termes exacts du cours (nomenclature anatomique, noms de structures, de molécules, de médicaments, valeurs chiffrées et unités) sans les simplifier ni les déformer.
        - Les sources : pour un PDF, la page est sa position dans le fichier (la première page est p. 1), pas un numéro imprimé ; pour une présentation, le numéro de diapositive ; pour un texte sans pages, la section (« § Innervation »).
        - Écris dans la langue du document.
        """;

    /// <summary>
    /// Asks <paramref name="ask"/> about <paramref name="document"/> (or about <paramref name="source"/>, a text that stands
    /// for it: the summary sheet), the answer streamed (<paramref name="progress"/>, if
    /// given, is told what is written so far): from the assistant's model if one is chosen, else from the best the key may
    /// use; one that cannot answer now (no quota, overloaded) before writing anything is passed for the next. The model
    /// thinks little (<paramref name="quick"/>: as little as it may), so the first words come within a minute or two.
    /// </summary>
    private async Task<T> AskAsync<T>(
        StudyDocument? document, string ask, JsonObject schema, double temperature, bool quick, Action<string>? progress, Action<string>? note,
        CancellationToken cancellation, JsonObject? source = null)
    {
        string key = keys.Key is { Length: > 0 } saved ? saved
            : throw new GeminiException(Loc.T("Ajoutez d'abord une clé Gemini dans Paramètres, Assistant."));
        var part = source ?? await (document ?? throw new ArgumentNullException(nameof(document))).PartAsync(gemini, key, cancellation);

        // A model that cannot answer now (its quota spent, busy) is set aside and the next one asked, as the assistant does
        // (it answered while this waited on one model); cut off while writing (Google overloaded mid-answer: seen after four
        // parts of the mind maps), the answer is asked again, from its start, of the next one. Only when none can is the
        // soonest one back waited for, as Google says, a few times at most.
        GeminiException? last = null;
        for (int waited = 0; ; waited++)
        {
            var models = await ModelsAsync(key);
            foreach (string model in models)
            {
                if (_resting.TryGetValue(model, out var until) && DateTime.UtcNow < until)
                {
                    continue;
                }

                var thinking = _thinksItsWay.Contains(model) ? null : quick ? GeminiClient.QuickThinking(model) : GeminiClient.LightThinking(model);
                var text = new StringBuilder();
                try
                {
                    try
                    {
                        await StreamAsync(model, thinking);
                    }
                    catch (GeminiException ex) when (ex.Malformed && thinking is not null && text.Length == 0)
                    {
                        _thinksItsWay.Add(model); // It does not take that setting: as it likes, then.
                        await StreamAsync(model, null);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException)
                    {
                        // The connection dropped (a long answer streams for minutes): once more, from the start.
                        text.Clear();
                        progress?.Invoke("");
                        try
                        {
                            await StreamAsync(model, thinking);
                        }
                        catch (Exception again) when (again is HttpRequestException or IOException)
                        {
                            throw new GeminiException(Loc.T("La connexion avec Notch a été coupée : réessayez."));
                        }
                    }

                    note?.Invoke("");
                    return JsonSerializer.Deserialize<T>(text.ToString(), Json) ?? throw new GeminiException(Loc.T("Notch n'a rien pu tirer de ce document."));
                }
                catch (GeminiException ex) when (ex.ModelUnusable)
                {
                    ErrorLog.Write($"[Study] {model} passed{(text.Length > 0 ? $" after {text.Length} characters" : "")}: {ex.Message}{(ex.Daily ? " (the day's quota)" : "")}");
                    if (text.Length > 0)
                    {
                        progress?.Invoke(""); // What was written is dropped: the next one writes it all again.
                    }

                    last = ex;
                    _resting[model] = ex.Lasting ? DateTime.MaxValue
                        : DateTime.UtcNow + (ex.Daily ? DailyRest : ex.RetryAfter is { } after ? after : BusyRest);
                }
                catch (GeminiException ex)
                {
                    ErrorLog.Write($"[Study] {model} failed: {ex.Message}");
                    throw;
                }
                catch (JsonException)
                {
                    ErrorLog.Write($"[Study] {model} wrote JSON that does not read ({text.Length} characters).");
                    throw new GeminiException(Loc.T("La réponse de Notch était incomplète : réessayez."));
                }

                async Task StreamAsync(string model, JsonObject? thinking)
                {
                    using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    silence.CancelAfter(FirstWordWithin);
                    var parts = new JsonArray(part.DeepClone(), new JsonObject { ["text"] = ask });
                    try
                    {
                        await foreach (string piece in gemini.StreamJsonAsync(key, model, Instructions(), parts, schema, temperature, thinking, silence.Token))
                        {
                            silence.CancelAfter(PieceWithin);
                            text.Append(piece);
                            progress?.Invoke(text.ToString());
                        }
                    }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    {
                        throw new GeminiException(text.Length == 0
                            ? Loc.T("Notch n'a pas commencé à répondre : réessayez dans un moment.")
                            : Loc.T("Notch s'est arrêté en cours de route : réessayez."));
                    }
                }
            }

            // None could answer: the soonest one back is waited for, if it is soon (a minute's quota, a busy moment); the
            // day's quotas spent everywhere, or still nothing after a few pauses, it is said.
            var now = DateTime.UtcNow;
            var back = models.Select(model => _resting.TryGetValue(model, out var until) ? until : now).DefaultIfEmpty(DateTime.MaxValue).Min();
            if (last is null || waited >= QuotaWaits || back == DateTime.MaxValue || back - now > LongestWait * 2)
            {
                throw last is { Daily: true }
                    ? new GeminiException(Loc.T("Le quota du jour de votre clé est atteint pour les modèles disponibles : réessayez plus tard (il revient chaque jour)."))
                    : last ?? new GeminiException(Loc.T("Aucun modèle Gemini disponible pour cette clé."));
            }

            var wait = back - now;
            wait = wait < TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : wait > LongestWait ? LongestWait : wait;
            ErrorLog.Write($"[Study] No model can answer now: waiting {wait.TotalSeconds:0} s.");
            note?.Invoke(Loc.F("Google demande une pause (quota ou affluence) : Notch reprend dans {0} s.", Math.Ceiling(wait.TotalSeconds)));
            await Task.Delay(wait, cancellation);
            note?.Invoke(Loc.T("Notch reprend…"));
        }
    }

    /// <summary>
    /// The chapters of a summary sheet written without an outline: its « ## » parts, their pages from « (p. 3-12) ».
    /// </summary>
    public static List<StudyChapter> ChaptersOf(string summary) => SheetPart().Matches(summary)
        .Select(match => new StudyChapter(match.Groups["title"].Value.Trim(), match.Groups["pages"].Value.Trim()))
        .Where(chapter => chapter.Title.Length > 0)
        .ToList();

    /// <summary>
    /// The models to ask, best first: the one chosen for the assistant, if any; then the everyday « Flash » ones, the
    /// « Pro » ones, and the « Lite » ones last (precision before speed here). Looked up once per key.
    /// </summary>
    private async Task<IReadOnlyList<string>> ModelsAsync(string key)
    {
        if (_listing is null || _listedFor != key || _listing.IsFaulted || _listing.IsCanceled)
        {
            if (_listedFor != key)
            {
                _resting.Clear();
            }

            _listedFor = key;
            _listing = List();
        }

        var listed = await _listing;
        string chosen = settings.AssistantModel.Trim();
        return chosen.Length > 0 ? [chosen, .. listed.Where(model => model != chosen)] : listed;

        async Task<IReadOnlyList<string>> List() => GeminiClient.Candidates(await gemini.ListModelsAsync(key))
            .OrderBy(model => model.Contains("lite", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();
    }

    /// <summary>A question as compared with the others: letters and digits only, no case, no accents.</summary>
    private static string Normalize(string question)
    {
        var text = new StringBuilder();
        foreach (char c in question.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                text.Append(char.ToLowerInvariant(c));
            }
        }

        return text.ToString();
    }

    private static JsonObject OutlineSchema() => Object(
        ("title", String()),
        ("chapters", new JsonObject { ["type"] = "ARRAY", ["items"] = Object(("title", String()), ("pages", String())) }));

    private static JsonObject SummarySchema() => Object(("summary", String()));

    private static JsonObject CardsSchema() => Object(
        ("cards", new JsonObject
        {
            ["type"] = "ARRAY",
            ["items"] = Object(("question", String()), ("answer", String()), ("choice", String()), ("decoys", Strings(3)), ("source", String()), ("chapter", String())),
        }));

    private static JsonObject ChoicesSchema() => Object(
        ("cards", new JsonObject
        {
            ["type"] = "ARRAY",
            ["items"] = Object(("id", String()), ("choice", String()), ("decoys", Strings(3))),
        }));

    private static JsonObject MindMapsSchema() => Object(
        ("maps", new JsonObject
        {
            ["type"] = "ARRAY",
            ["items"] = Object(
                ("chapter", String()),
                ("notion", String()),
                ("definition", String()),
                ("formulas", new JsonObject { ["type"] = "ARRAY", ["items"] = String() }),
                ("branches", new JsonObject
                {
                    ["type"] = "ARRAY",
                    ["items"] = Object(("label", String()), ("details", new JsonObject { ["type"] = "ARRAY", ["items"] = String() })),
                })),
        }));

    /// <summary>A list of texts (<paramref name="count"/>, as the prompt asks; kept to it by <see cref="Tidy"/>).</summary>
    private static JsonObject Strings(int count) => new()
    {
        ["type"] = "ARRAY",
        ["items"] = String(),
        ["description"] = $"exactly {count}",
    };

    private static JsonObject String() => new() { ["type"] = "STRING" };

    /// <summary>An object schema: these properties, all required, in this order.</summary>
    private static JsonObject Object(params (string Name, JsonObject Schema)[] properties)
    {
        var shape = new JsonObject();
        foreach (var (name, schema) in properties)
        {
            shape[name] = schema;
        }

        var names = properties.Select(property => (JsonNode)JsonValue.Create(property.Name)!).ToArray();
        return new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = shape,
            ["required"] = new JsonArray(names.Select(name => name.DeepClone()).ToArray()),
            ["propertyOrdering"] = new JsonArray(names),
        };
    }

    // A part of the sheet begun (« ## », not « ### »), at the start of the text or of a line (« \n » in the JSON).
    [GeneratedRegex(@"(?:^|\\n|"")##(?!#)")]
    private static partial Regex PartHeading();

    // A part of a finished sheet: its title, and its pages if given (« ## Ostéologie (p. 3-12) »).
    [GeneratedRegex(@"^##(?!#)\s*(?<title>.+?)\s*(?:\((?:p\.|pages?|diapos?\.?)\s*(?<pages>[^)]*)\))?\s*$", RegexOptions.Multiline)]
    private static partial Regex SheetPart();

    /// <summary>The part of the last mind map begun, as its JSON is written (its name whole); <c>null</c> before any.</summary>
    internal static string? LastChapter(string written)
    {
        var parts = ChapterValue().Matches(written);
        if (parts.Count == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>($"\"{parts[^1].Groups["value"].Value}\"");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A mind map's part, written whole (its closing quote come).
    [GeneratedRegex(@"""chapter""\s*:\s*""(?<value>(?:[^""\\]|\\.)*)""")]
    private static partial Regex ChapterValue();

    // A mind map begun, as its JSON is written.
    [GeneratedRegex(@"""notion""\s*:")]
    private static partial Regex NotionKey();

    // A card begun, as its JSON is written.
    [GeneratedRegex(@"""question""\s*:")]
    private static partial Regex QuestionKey();

    private sealed record SummaryAnswer([property: JsonPropertyName("summary")] string Summary);

    private sealed record CardsAnswer([property: JsonPropertyName("cards")] List<GeneratedCard> Cards);

    private sealed record ChoicesAnswer([property: JsonPropertyName("cards")] List<GeneratedChoices> Cards);

    private sealed record MindMapsAnswer([property: JsonPropertyName("maps")] List<MindMap> Maps);
}
