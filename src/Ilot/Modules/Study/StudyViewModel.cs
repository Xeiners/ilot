using System.Collections.ObjectModel;
using Ilot.Localization;
using Ilot.Modules.Assistant;

namespace Ilot.Modules.Study;

/// <summary>
/// The revisions: the courses (read when the tab first shows), a course made from a document (copied, then read by
/// Notch: its outline, then its summary sheet and its first flashcards at the same time, each followed as it is
/// written), more cards on demand, the cards answered, edited, removed. Each course's work goes on in the background
/// and can be followed on it (<see cref="StudyDeck.Busy"/> and its steps); <see cref="Announced"/> tells the closed notch.
/// </summary>
public sealed class StudyViewModel(StudyStore store, StudyGenerator generator)
{
    private readonly Dictionary<long, CancellationTokenSource> _work = [];
    private readonly HashSet<long> _choosing = [];
    private readonly Dictionary<long, DateTime> _choicesAsked = []; // When each course's missing choices were last asked for.
    private readonly HashSet<long> _more = []; // Courses getting more cards (not their first ones).
    private readonly Dictionary<long, CancellationTokenSource> _drawing = []; // Courses whose mind maps Notch draws.

    /// <summary>Cards given their choices in one request, at most.</summary>
    private const int ChoicesPerRequest = 40;

    /// <summary>
    /// A course's missing choices asked for again only after this long (the whole document goes with the request: asked at
    /// each opening of the course, cards Notch could not give four choices to would spend the key's quota).
    /// </summary>
    private static readonly TimeSpan ChoicesAgain = TimeSpan.FromMinutes(30);
    private Task? _loading;

    public ObservableCollection<StudyDeck> Decks { get; } = [];

    /// <summary>The sections (subjects) the user made, in their order.</summary>
    public ObservableCollection<StudySection> Sections { get; } = [];

    /// <summary>A new section's colour, in turn: the mascot's palette (lavender, sky, mint, lemon, peach, pink, coral).</summary>
    public static readonly string[] SectionColors = ["#FFB7A0FB", "#FF5CC9F9", "#FF58DBAB", "#FFFBD53F", "#FFFBA65F", "#FFF68BC3", "#FFF98B8B"];

    /// <summary>Whether the assistant has its key (without it, nothing can be made).</summary>
    public bool HasKey => generator.HasKey;

    /// <summary>
    /// Something to say on the closed notch: a glyph, a message, and how far the work is (0 to 1), or <c>null</c> when
    /// it is done.
    /// </summary>
    public event EventHandler<(string Glyph, string Message, double? Progress)>? Announced;

    /// <summary>
    /// The closed notch's live activity: how far the course being prepared is (« Fiche 4/12 · 18/50 cartes », and a
    /// ring); <c>null</c> once no course is.
    /// </summary>
    public event EventHandler<Ilot.Notch.NotchActivity?>? ActivityChanged;

    /// <summary>A course is ready (its sheet and its first cards made).</summary>
    public event EventHandler<StudyDeck>? Prepared;

    /// <summary>The courses, read once.</summary>
    public Task LoadAsync() => _loading ??= Load();

    private async Task Load()
    {
        foreach (var deck in await store.LoadAsync())
        {
            if (deck.Summary.Length == 0 || deck.Cards.Count == 0)
            {
                // Its making stopped before the end (the app closed, it failed): it can be finished.
                deck.Failed = true;
                deck.Progress = Loc.T("Pas terminé : réessayez.");
                deck.OutlineStep = deck.Chapters.Count > 0 ? StudyStep.Done : StudyStep.Failed;
                deck.SummaryStep = deck.Summary.Length > 0 ? StudyStep.Done : StudyStep.Failed;
                deck.CardsStep = deck.Cards.Count > 0 ? StudyStep.Done : StudyStep.Failed;
            }

            Decks.Add(deck);
        }

        foreach (var section in await store.LoadSectionsAsync())
        {
            Sections.Add(section);
        }
    }

    /// <summary>A new section's colour, unless another is chosen: the next of the palette.</summary>
    public string NextSectionColor => SectionColors[Sections.Count % SectionColors.Length];

    /// <summary>A new section, last, in <paramref name="color"/> (by default <see cref="NextSectionColor"/>).</summary>
    public StudySection CreateSection(string name, string? color = null)
    {
        var section = store.NewSection(name.Trim(), color ?? NextSectionColor, Sections.Count == 0 ? 0 : Sections.Max(each => each.Position) + 1);
        Sections.Add(section);
        store.Save(section);
        return section;
    }

    public void Rename(StudySection section, string name)
    {
        name = name.Trim();
        if (name.Length > 0 && name != section.Name)
        {
            section.Name = name;
            store.Save(section);
        }
    }

    public void Recolor(StudySection section, string color)
    {
        if (color != section.Color)
        {
            section.Color = color;
            store.Save(section);
        }
    }

    /// <summary>A section gone: its courses stay, in none (« Global » still shows them).</summary>
    public void Delete(StudySection section)
    {
        foreach (var deck in Decks.Where(deck => deck.SectionId == section.Id))
        {
            deck.SectionId = 0;
        }

        Sections.Remove(section);
        store.Delete(section);
    }

    /// <summary><paramref name="deck"/> put in the section <paramref name="section"/> (0: none).</summary>
    public void Move(StudyDeck deck, long section)
    {
        if (deck.SectionId != section)
        {
            deck.SectionId = section;
            store.Save(deck);
        }
    }

    /// <summary>The courses of a section; all of them for « Global » (0).</summary>
    public IEnumerable<StudyDeck> In(long section) => section == 0 ? Decks : Decks.Where(deck => deck.SectionId == section);


    /// <summary>
    /// A course from <paramref name="path"/>: copied, then made into its sheet and first cards. Says why when it cannot
    /// even start (no key, a format not read).
    /// </summary>
    /// <param name="section">The section it goes in (the one chosen when it was dropped); 0: none.</param>
    public async Task<string?> ImportAsync(string path, long section = 0)
    {
        if (!generator.HasKey)
        {
            return Loc.T("Ajoutez d'abord une clé Gemini dans Paramètres, Assistant.");
        }

        if (!StudyDocument.IsSupported(path))
        {
            return Loc.F("« {0} » : format non pris en charge (PDF, Word, PowerPoint, image ou texte).", Path.GetFileName(path));
        }

        await LoadAsync();
        if (await store.CreateAsync(path) is not { } deck)
        {
            return Loc.T("Le document n'a pas pu être copié.");
        }

        if (Sections.Any(each => each.Id == section))
        {
            deck.SectionId = section;
            store.Save(deck);
        }

        Decks.Insert(0, deck);
        _ = MakeAsync(deck);
        return null;
    }

    /// <summary>Makes again what a course lacks (its outline, its sheet, its first cards), after a failure.</summary>
    public void Retry(StudyDeck deck)
    {
        if (!deck.Busy)
        {
            _ = MakeAsync(deck);
        }
    }

    /// <summary>
    /// <paramref name="count"/> more cards on <paramref name="focus"/>, none like those the course has. Returns how many
    /// came (0 when it failed: the course says why).
    /// </summary>
    public async Task<int> MoreCardsAsync(StudyDeck deck, CardFocus focus, int count)
    {
        if (deck.Busy)
        {
            return 0;
        }

        var cancellation = Begin(deck);
        _more.Add(deck.Id);
        try
        {
            deck.CardsTarget = count;
            deck.CardsWritten = 0;
            deck.CardsStep = StudyStep.Working;
            Report(deck);
            var document = await StudyDocument.ReadAsync(deck.File);
            var made = await generator.CardsAsync(document, deck, focus, count, variety: true, written =>
            {
                if (Math.Min(written, count) != deck.CardsWritten)
                {
                    deck.CardsWritten = Math.Min(written, count);
                    Report(deck);
                }
            }, note => deck.CardsNote = note, cancellation.Token);
            deck.CardsStep = StudyStep.Done;
            return Add(deck, made);
        }
        catch (GeminiException ex)
        {
            deck.CardsStep = StudyStep.Failed;
            Fail(deck, ex.Message);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            deck.CardsStep = StudyStep.Failed;
            Fail(deck, Unforeseen(ex));
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            _more.Remove(deck.Id);
            End(deck, cancellation);
        }
    }

    /// <summary>
    /// The course's mind maps, drawn by Notch (again, replacing them, if it has some): from its sheet (or its document,
    /// without one), followed on the course as they come (and on the closed notch, while no course is being prepared);
    /// kept once they are all there. A failure keeps the ones before, and says why.
    /// </summary>
    public async void DrawMindMaps(StudyDeck deck)
    {
        if (_drawing.ContainsKey(deck.Id) || (deck.Busy && deck.Summary.Length == 0))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _drawing[deck.Id] = cancellation;
        deck.MindMapsWritten = 0;
        deck.MindMapsPart = 0;
        deck.MindMapsNote = "";
        deck.MindMapsStep = StudyStep.Working;
        ReportDrawing(deck);
        try
        {
            var document = deck.Summary.Trim().Length > 0 ? null : await StudyDocument.ReadAsync(deck.File);
            var maps = await generator.MindMapsAsync(document, deck, (written, chapter) =>
            {
                // The maps come in the course's order: the part of the last one begun is how far Notch is (from the start
                // again when another model writes them all over).
                int part = chapter is null ? 0 : deck.Chapters.FindIndex(each => MindMap.Key(each.Title) == MindMap.Key(chapter)) + 1;
                if (written != deck.MindMapsWritten || part > deck.MindMapsPart)
                {
                    deck.MindMapsPart = written == 0 ? 0 : Math.Max(deck.MindMapsPart, part);
                    deck.MindMapsWritten = written;
                    ReportDrawing(deck);
                }
            }, note => deck.MindMapsNote = note, cancellation.Token);
            if (!Decks.Contains(deck))
            {
                return;
            }

            if (maps.Count == 0)
            {
                throw new GeminiException(Loc.T("Notch n'a pas pu tirer de carte mentale de ce cours : réessayez."));
            }

            deck.MindMaps = maps;
            deck.MindMapsNote = "";
            deck.MindMapsStep = StudyStep.Done;
            store.Save(deck);
            Announced?.Invoke(this, ("\uF22C", Loc.F("« {0} » : {1}", deck.Title, Loc.Count(maps.Count, "carte mentale prête", "cartes mentales prêtes")), null));
        }
        catch (GeminiException ex)
        {
            NotDrawn(deck, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NotDrawn(deck, Unforeseen(ex));
        }
        catch (OperationCanceledException)
        {
            deck.MindMapsStep = deck.MindMaps.Count > 0 ? StudyStep.Done : StudyStep.Waiting;
        }
        finally
        {
            _drawing.Remove(deck.Id);
            cancellation.Dispose();
            ReportEnded(); // The live activity: a course being prepared, or none.
        }
    }

    private void NotDrawn(StudyDeck deck, string why)
    {
        deck.MindMapsNote = why;
        deck.MindMapsStep = StudyStep.Failed;
        Announced?.Invoke(this, ("\uE783", Loc.T("Les cartes mentales n'ont pas pu être dessinées."), null));
    }

    /// <summary>
    /// On the closed notch, while no course is being prepared: the part Notch draws (« Cartes mentales · 2/6 », a ring for
    /// the parts gone through), or the maps drawn so far when the course has no parts.
    /// </summary>
    private void ReportDrawing(StudyDeck deck)
    {
        if (Decks.Any(each => each.Busy))
        {
            return;
        }

        int parts = deck.Chapters.Count;
        bool known = parts > 0 && deck.MindMapsPart > 0;
        string text = known ? Loc.F("Cartes mentales · {0}/{1}", deck.MindMapsPart, parts)
            : deck.MindMapsWritten > 0 ? Loc.F("Cartes mentales · {0}", deck.MindMapsWritten)
            : Loc.T("Cartes mentales…");
        ActivityChanged?.Invoke(this, new Ilot.Notch.NotchActivity("\uF22C", text, known ? (deck.MindMapsPart - 0.5) / parts : null));
    }

    /// <summary>What was drawn or written on a course's mind maps: kept.</summary>
    public void SaveInk(StudyDeck deck) => store.Save(deck);

    public void Edit(StudyDeck deck, StudyCard card, string question, string answer)
    {
        bool answered = card.Answer != answer.Trim();
        card.Question = question.Trim();
        card.Answer = answer.Trim();
        if (answered)
        {
            card.Choice = "";
            card.Decoys = [];
        }

        store.Save([card]);
        if (answered)
        {
            CompleteChoices(deck);
        }
    }

    /// <summary>
    /// The course's cards without their four choices (made before them, or edited) get them, in the background, quietly
    /// (the classic answer meanwhile). Once at a time per course.
    /// </summary>
    public void CompleteChoices(StudyDeck deck) => CompleteChoices(deck, going: false);

    /// <param name="going">The next ones, the request before having given some: asked at once.</param>
    private async void CompleteChoices(StudyDeck deck, bool going)
    {
        var lacking = deck.Cards.Where(card => !card.HasChoices).Take(ChoicesPerRequest).ToList();
        if (lacking.Count == 0 || deck.Busy || !generator.HasKey
            || (!going && _choicesAsked.TryGetValue(deck.Id, out var asked) && DateTime.UtcNow - asked < ChoicesAgain)
            || !_choosing.Add(deck.Id))
        {
            return;
        }

        _choicesAsked[deck.Id] = DateTime.UtcNow;

        try
        {
            var document = await StudyDocument.ReadAsync(deck.File);
            var made = await generator.ChoicesAsync(document, lacking, CancellationToken.None);
            if (!Decks.Contains(deck))
            {
                return; // Removed meanwhile.
            }

            var byId = lacking.ToDictionary(card => card.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var filled = new List<StudyCard>();
            foreach (var choices in made)
            {
                if (byId.TryGetValue(choices.Id.Trim(), out var card))
                {
                    card.Choice = choices.Choice;
                    card.Decoys = choices.Decoys;
                    filled.Add(card);
                }
            }

            store.Save(filled);
            deck.Touch();
        }
        catch (Exception ex) when (ex is GeminiException or IOException or HttpRequestException)
        {
            System.Diagnostics.Debug.WriteLine($"[Study] Choices not made: {ex.Message}"); // The classic answer stays.
        }
        finally
        {
            _choosing.Remove(deck.Id);
        }

        if (Progressed() && deck.Cards.Any(card => !card.HasChoices))
        {
            CompleteChoices(deck, going: true); // The next ones, if there were more than one request's worth.
        }

        bool Progressed() => Decks.Contains(deck) && lacking.Any(card => card.HasChoices);
    }

    public void Delete(StudyDeck deck, StudyCard card)
    {
        deck.Cards.Remove(card);
        store.Delete(card);
        deck.Touch();
    }

    public void Delete(StudyDeck deck)
    {
        if (_work.Remove(deck.Id, out var work))
        {
            work.Cancel();
        }

        if (_drawing.Remove(deck.Id, out var drawing))
        {
            drawing.Cancel();
        }

        Decks.Remove(deck);
        store.Delete(deck);
    }

    /// <summary>A card answered: it is written at once (with its new due date).</summary>
    public void Answered(StudyDeck deck, StudyCard card)
    {
        store.Save([card]);
        deck.Touch();
    }

    /// <summary>Cards to see now, all courses together.</summary>
    public int DueCount(DateTimeOffset now) => Decks.Sum(deck => deck.DueCount(now));

    /// <summary>
    /// What the course lacks, made: its outline first (quick), then its summary sheet and its first cards at the same
    /// time (each may fail without the other: the cards can be revised while the sheet is still being written).
    /// </summary>
    private async Task MakeAsync(StudyDeck deck)
    {
        var cancellation = Begin(deck);
        try
        {
            deck.OutlineStep = deck.Chapters.Count > 0 ? StudyStep.Done : StudyStep.Working;
            deck.SummaryStep = deck.Summary.Length > 0 ? StudyStep.Done : StudyStep.Waiting;
            deck.CardsStep = deck.Cards.Count > 0 ? StudyStep.Done : StudyStep.Waiting;
            deck.Progress = Loc.T("Notch lit le cours…");
            deck.PropertyChanged += OnMaking;
            Report(deck);
            var document = await StudyDocument.ReadAsync(deck.File);
            deck.Pages = document.Pages;

            // The outline first (quick): the sheet's parts and the cards' chapters follow it. Without it, they go on all
            // the same: the sheet finds its parts itself, and they become the chapters.
            if (deck.Chapters.Count == 0)
            {
                try
                {
                    var outline = await generator.OutlineAsync(document, note => deck.OutlineNote = note, cancellation.Token);
                    deck.Title = outline.Title.Length > 0 ? outline.Title : deck.Title;
                    deck.Chapters = outline.Chapters;
                    deck.OutlineStep = StudyStep.Done;
                    store.Save(deck);
                }
                catch (GeminiException ex)
                {
                    deck.OutlineStep = StudyStep.Done; // Its parts will come from the sheet.
                    deck.OutlineNote = Loc.F("Plan sauté ({0}) : la fiche fera ses parties.", ex.Message);
                }
            }

            deck.Progress = Loc.T("Notch écrit la fiche et les flashcards…");
            Report(deck);
            var failures = await Task.WhenAll(
                deck.Summary.Length == 0 ? MakeSummaryAsync(deck, document, cancellation.Token) : Task.FromResult<string?>(null),
                deck.Cards.Count == 0 ? MakeCardsAsync(deck, document, cancellation.Token) : Task.FromResult<string?>(null));

            store.Save(deck);
            if (failures.FirstOrDefault(failure => failure is not null) is { } why)
            {
                Fail(deck, why);
                Announced?.Invoke(this, ("\uE783", Loc.F("« {0} » : pas terminé.", deck.Title), null));
            }
            else
            {
                Announced?.Invoke(this, ("\uE73E", Loc.F("« {0} » : {1}", deck.Title, Loc.Count(deck.Cards.Count, "carte prête", "cartes prêtes")), null));
                Prepared?.Invoke(this, deck);
            }


        }
        catch (GeminiException ex)
        {
            Fail(deck, ex.Message);
            Announced?.Invoke(this, ("\uE783", Loc.T("Le cours n'a pas pu être préparé."), null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(deck, Unforeseen(ex));
            Announced?.Invoke(this, ("\uE783", Loc.T("Le cours n'a pas pu être préparé."), null));
        }
        catch (OperationCanceledException)
        {
            // The course was removed meanwhile.
        }
        finally
        {
            End(deck, cancellation);
        }
    }

    /// <summary>The summary sheet, part after part; why it failed, if it did.</summary>
    private async Task<string?> MakeSummaryAsync(StudyDeck deck, StudyDocument document, CancellationToken cancellation)
    {
        deck.SummaryStep = StudyStep.Working;
        deck.SummaryParts = 0;
        deck.SummaryNote = "";
        try
        {
            deck.Summary = await generator.SummaryAsync(document, deck, parts =>
            {
                if (parts != deck.SummaryParts)
                {
                    deck.SummaryParts = parts;
                    Report(deck);
                }
            }, note => deck.SummaryNote = note, cancellation);
            if (deck.Chapters.Count == 0)
            {
                deck.Chapters = StudyGenerator.ChaptersOf(deck.Summary); // No outline: the sheet's parts.
            }

            deck.SummaryParts = Math.Max(deck.SummaryParts, deck.Chapters.Count);
            deck.SummaryStep = StudyStep.Done;
            store.Save(deck);
            return null;
        }
        catch (GeminiException ex)
        {
            deck.SummaryStep = StudyStep.Failed;
            return deck.SummaryNote = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            deck.SummaryStep = StudyStep.Failed;
            return deck.SummaryNote = Unforeseen(ex);
        }
    }

    /// <summary>The first cards, as many as the course is long; why it failed, if it did.</summary>
    private async Task<string?> MakeCardsAsync(StudyDeck deck, StudyDocument document, CancellationToken cancellation)
    {
        int count = StudyGenerator.FirstCount(document);
        deck.CardsTarget = count;
        deck.CardsWritten = 0;
        deck.CardsStep = StudyStep.Working;
        deck.CardsNote = "";
        try
        {
            var made = await generator.CardsAsync(document, deck, CardFocus.All, count, variety: false, written =>
            {
                written = Math.Min(written, count);
                if (written != deck.CardsWritten)
                {
                    deck.CardsWritten = written;
                    Report(deck);
                }
            }, note => deck.CardsNote = note, cancellation);
            Add(deck, made);
            deck.CardsStep = StudyStep.Done;
            return null;
        }
        catch (GeminiException ex)
        {
            deck.CardsStep = StudyStep.Failed;
            return deck.CardsNote = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            deck.CardsStep = StudyStep.Failed;
            return deck.CardsNote = Unforeseen(ex);
        }
    }

    private int Add(StudyDeck deck, IReadOnlyList<GeneratedCard> made)
    {
        var cards = made.Select(card =>
        {
            var fresh = store.NewCard(deck, card.Question, card.Answer, card.Source, card.Chapter);
            fresh.Choice = card.Choice;
            fresh.Decoys = card.Decoys;
            return fresh;
        }).ToList();
        foreach (var card in cards)
        {
            deck.Cards.Add(card);
        }

        store.Save(cards);
        deck.Touch();
        return cards.Count;
    }

    private CancellationTokenSource Begin(StudyDeck deck)
    {
        deck.Started = DateTimeOffset.Now;
        var cancellation = new CancellationTokenSource();
        _work[deck.Id] = cancellation;
        deck.Failed = false;
        deck.Busy = true;
        return cancellation;
    }

    /// <summary>A step of a course's making moved: the live activity follows.</summary>
    private void OnMaking(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is StudyDeck deck && e.PropertyName is nameof(StudyDeck.OutlineStep) or nameof(StudyDeck.SummaryStep) or nameof(StudyDeck.CardsStep))
        {
            Report(deck);
        }
    }

    private void End(StudyDeck deck, CancellationTokenSource cancellation)
    {
        deck.PropertyChanged -= OnMaking;
        if (_work.TryGetValue(deck.Id, out var current) && current == cancellation)
        {
            _work.Remove(deck.Id);
        }

        cancellation.Dispose();
        deck.Busy = false;
        ReportEnded();
        if (!deck.Failed)
        {
            deck.Progress = "";
        }

        CompleteChoices(deck); // Cards that came without their four choices get them.
    }

    /// <summary>What went wrong that was not foreseen (a file gone, the connection cut…): said, and logged.</summary>
    private static string Unforeseen(Exception ex)
    {
        ErrorLog.Write($"[Study] {ex}");
        return Loc.F("Un problème est survenu : {0}", ex.Message);
    }

    private static void Fail(StudyDeck deck, string why)
    {
        deck.Failed = true;
        deck.Progress = why;
    }

    /// <summary>
    /// The closed notch shows how far the course is, as a live activity: reading it, then its sheet (parts written) and its
    /// cards (written of asked), with a ring for the whole (the outline a sixth, then the sheet and the cards each half
    /// of what is left; the cards alone, for more cards).
    /// </summary>
    private void Report(StudyDeck deck)
    {
        if (!deck.Busy)
        {
            return;
        }

        int chapters = deck.Chapters.Count;
        double sheet = deck.SummaryStep == StudyStep.Done ? 1 : chapters == 0 ? 0 : Math.Min(1, deck.SummaryParts / (double)chapters);
        double cards = deck.CardsStep == StudyStep.Done ? 1 : deck.CardsTarget == 0 ? 0 : Math.Min(1, deck.CardsWritten / (double)deck.CardsTarget);

        var parts = new List<string>();
        if (deck.OutlineStep == StudyStep.Working)
        {
            parts.Add(Loc.T("Lecture du cours"));
        }
        else
        {
            if (deck.SummaryStep == StudyStep.Working)
            {
                parts.Add(chapters > 0 ? Loc.F("Fiche {0}/{1}", Math.Min(deck.SummaryParts, chapters), chapters) : Loc.F("Fiche {0}", deck.SummaryParts));
            }

            if (deck.CardsStep == StudyStep.Working)
            {
                parts.Add(Loc.F("{0}/{1} cartes", deck.CardsWritten, deck.CardsTarget));
            }
        }

        double progress = _more.Contains(deck.Id) ? cards
            : deck.OutlineStep == StudyStep.Working ? 0.04
            : 1 / 6.0 + 5 / 6.0 * (sheet + cards) / 2;
        if (parts.Count > 0)
        {
            ActivityChanged?.Invoke(this, new Ilot.Notch.NotchActivity("\uE7BE", string.Join(" · ", parts), Math.Clamp(progress, 0.02, 1)));
        }
    }

    /// <summary>No course is being prepared any more: the live activity ends; another being prepared shows instead.</summary>
    private void ReportEnded()
    {
        if (Decks.FirstOrDefault(other => other.Busy) is { } other)
        {
            Report(other);
        }
        else
        {
            ActivityChanged?.Invoke(this, null);
        }
    }
}
