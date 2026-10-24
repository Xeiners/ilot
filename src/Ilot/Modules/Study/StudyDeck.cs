using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Ilot.Modules.Study;

/// <summary>Where a step of a course's making is.</summary>
public enum StudyStep
{
    Waiting,
    Working,
    Done,
    Failed,
}

/// <summary>A chapter of a course, as its outline gives it: its title and its pages (« 3-12 »).</summary>
public sealed record StudyChapter(string Title, string Pages);

/// <summary>
/// A course turned into revisions: the document it comes from (kept in the app's folder, so new cards can be made from
/// it later), its summary sheet (Markdown), its chapters, and its flashcards. <see cref="Busy"/> while Gemini works on
/// it, with what it is doing in <see cref="Progress"/>.
/// </summary>
public sealed class StudyDeck : ObservableObject
{
    public StudyDeck(long id, string title, string sourceName, string file, DateTimeOffset created)
    {
        Id = id;
        _title = title;
        SourceName = sourceName;
        File = file;
        Created = created;
    }

    public long Id { get; }

    /// <summary>The document's name as it was dropped (« Anatomie - membre supérieur.pdf »).</summary>
    public string SourceName { get; }

    /// <summary>Its copy, in the app's folder.</summary>
    public string File { get; }

    public DateTimeOffset Created { get; }

    private string _title;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private string _summary = "";

    /// <summary>The summary sheet, in Markdown, with its pages cited.</summary>
    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    private int _pages;

    /// <summary>How many pages (or slides) the document has; 0 when not known.</summary>
    public int Pages
    {
        get => _pages;
        set => SetProperty(ref _pages, value);
    }

    private bool _busy;

    /// <summary>Gemini is working on it (reading it, writing cards).</summary>
    public bool Busy
    {
        get => _busy;
        set => SetProperty(ref _busy, value);
    }

    private string _progress = "";

    /// <summary>What it is doing, while <see cref="Busy"/> (« Lecture du cours… »); or what went wrong, after.</summary>
    public string Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    private bool _failed;

    /// <summary>The last work on it failed (<see cref="Progress"/> says why).</summary>
    public bool Failed
    {
        get => _failed;
        set => SetProperty(ref _failed, value);
    }

    public List<StudyChapter> Chapters { get; set; } = [];

    private long _section;

    /// <summary>The section it is in (see <see cref="StudySection"/>); 0: none (only in « Global »).</summary>
    public long SectionId
    {
        get => _section;
        set => SetProperty(ref _section, value);
    }

    private IReadOnlyList<MindMap> _mindMaps = [];
    private StudyStep _mindMapsStep;
    private int _mindMapsWritten;
    private int _mindMapsPart;
    private string _mindMapsNote = "";

    /// <summary>What the user drew and wrote on its mind maps, each map's under its key (see <see cref="MapInk.KeyOf"/>).</summary>
    public Dictionary<string, MapInk> MapInks { get; set; } = [];

    /// <summary>Its mind maps (one per important notion, in the course's order), once Notch has drawn them.</summary>
    public IReadOnlyList<MindMap> MindMaps
    {
        get => _mindMaps;
        set => SetProperty(ref _mindMaps, value);
    }

    /// <summary>Where the mind maps are: none asked for yet, being drawn, there, or failed (the last ones kept).</summary>
    public StudyStep MindMapsStep
    {
        get => _mindMapsStep;
        set => SetProperty(ref _mindMapsStep, value);
    }

    /// <summary>How many mind maps are drawn so far, while they are.</summary>
    public int MindMapsWritten
    {
        get => _mindMapsWritten;
        set => SetProperty(ref _mindMapsWritten, value);
    }

    /// <summary>The part of the course Notch is drawing the maps of (1 for the first), while it does; 0 when not known.</summary>
    public int MindMapsPart
    {
        get => _mindMapsPart;
        set => SetProperty(ref _mindMapsPart, value);
    }

    /// <summary>What drawing them waits for (a pause asked by Google), or why it failed.</summary>
    public string MindMapsNote
    {
        get => _mindMapsNote;
        set => SetProperty(ref _mindMapsNote, value);
    }

    public ObservableCollection<StudyCard> Cards { get; } = [];

    // ── Its making, step by step (while Notch works on it; not kept) ──

    /// <summary>When Notch began its present work on it.</summary>
    public DateTimeOffset Started { get; set; }

    private StudyStep _outlineStep;
    private StudyStep _summaryStep;
    private StudyStep _cardsStep;
    private int _summaryParts;
    private int _cardsWritten;
    private int _cardsTarget;

    /// <summary>Reading the course: its title and its parts.</summary>
    public StudyStep OutlineStep
    {
        get => _outlineStep;
        set => SetProperty(ref _outlineStep, value);
    }

    /// <summary>Writing its summary sheet, part by part (<see cref="SummaryParts"/> of <see cref="Chapters"/>).</summary>
    public StudyStep SummaryStep
    {
        get => _summaryStep;
        set => SetProperty(ref _summaryStep, value);
    }

    /// <summary>Writing cards (<see cref="CardsWritten"/> of <see cref="CardsTarget"/>).</summary>
    public StudyStep CardsStep
    {
        get => _cardsStep;
        set => SetProperty(ref _cardsStep, value);
    }

    private string _outlineNote = "";
    private string _summaryNote = "";
    private string _cardsNote = "";

    /// <summary>What a step waits for (a quota), or why it failed; empty: nothing to say.</summary>
    public string OutlineNote
    {
        get => _outlineNote;
        set => SetProperty(ref _outlineNote, value);
    }

    /// <inheritdoc cref="OutlineNote"/>
    public string SummaryNote
    {
        get => _summaryNote;
        set => SetProperty(ref _summaryNote, value);
    }

    /// <inheritdoc cref="OutlineNote"/>
    public string CardsNote
    {
        get => _cardsNote;
        set => SetProperty(ref _cardsNote, value);
    }

    public int SummaryParts
    {
        get => _summaryParts;
        set => SetProperty(ref _summaryParts, value);
    }

    public int CardsWritten
    {
        get => _cardsWritten;
        set => SetProperty(ref _cardsWritten, value);
    }

    public int CardsTarget
    {
        get => _cardsTarget;
        set => SetProperty(ref _cardsTarget, value);
    }

    /// <summary>Cards to see now.</summary>
    public int DueCount(DateTimeOffset now) => Cards.Count(card => card.IsDue(now));

    /// <summary>Part of the cards known well (0 to 1).</summary>
    public double Mastery => Cards.Count == 0 ? 0 : Cards.Count(card => card.IsMastered) / (double)Cards.Count;

    /// <summary>The counts changed (a card answered, added, removed): what shows them reads them again.</summary>
    public void Touch() => OnPropertyChanged(nameof(Cards));
}
