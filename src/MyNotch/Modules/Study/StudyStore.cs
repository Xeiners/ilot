using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using MyNotch.Interop;

namespace MyNotch.Modules.Study;

/// <summary>
/// Keeps the revisions on this computer: the courses and their cards in a SQLite database,
/// <c>%LOCALAPPDATA%\MyNotch\study.db</c> (the <c>winsqlite3.dll</c> of Windows), and a copy of each document beside it,
/// in <c>Study\</c> (new cards are made from it, wherever the original goes). Every access goes down one chain on the
/// thread pool, one at a time, in order; results come back on the UI thread.
/// </summary>
public sealed class StudyStore : IDisposable
{
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(3);

    private static readonly string AppFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyNotch");

    private readonly DispatcherQueue _dispatcher;
    private readonly string _folder;
    private Task _tail = Task.CompletedTask;
    private SqliteDatabase? _db;
    private long _lastDeck;
    private long _lastCard;
    private long _lastSection;
    private bool _disposed;
    private bool _closed;

    public StudyStore(DispatcherQueue dispatcher)
        : this(dispatcher, AppFolder)
    {
    }

    /// <summary>A store in <paramref name="folder"/> rather than the app's (the tests, on a folder of their own).</summary>
    internal StudyStore(DispatcherQueue dispatcher, string folder)
    {
        _dispatcher = dispatcher;
        _folder = folder;
    }

    /// <summary>Where the documents' copies are kept.</summary>
    private string Documents => Path.Combine(_folder, "Study");

    /// <summary>Every course with its cards, the latest first. An unreadable database gives none (and is left as it is).</summary>
    public async Task<IReadOnlyList<StudyDeck>> LoadAsync()
    {
        var (decks, cards) = await Run(db =>
        {
            var decks = db.Query(
                "SELECT id, title, source_name, file, summary, chapters, pages, created_at, mindmaps, mapink, section FROM Decks ORDER BY created_at DESC",
                row =>
                {
                    var maps = ReadMaps(row.Text(8));
                    return new StudyDeck(row.Int64(0), row.Text(1), row.Text(2), row.Text(3), FromStored(row.Int64(7)))
                    {
                        Summary = row.Text(4),
                        Chapters = ReadChapters(row.Text(5)),
                        Pages = (int)row.Int64(6),
                        MindMaps = maps,
                        MindMapsStep = maps.Count > 0 ? StudyStep.Done : StudyStep.Waiting,
                        MapInks = ReadInks(row.Text(9)),
                        SectionId = row.Int64(10),
                    };
                });
            var cards = db.Query(
                "SELECT id, deck_id, question, answer, source, chapter, created_at, state, step, due, interval_days, ease, reps, lapses, choice, decoys FROM Cards ORDER BY id",
                row => new StudyCard(row.Int64(0), row.Int64(1), row.Text(2), row.Text(3), row.Text(4), row.Text(5), FromStored(row.Int64(6)))
                {
                    State = (CardState)row.Int64(7),
                    Step = (int)row.Int64(8),
                    Due = FromStored(row.Int64(9)),
                    IntervalDays = (int)row.Int64(10),
                    Ease = row.Int64(11) / 1000.0,
                    Reps = (int)row.Int64(12),
                    Lapses = (int)row.Int64(13),
                    Choice = row.Text(14),
                    Decoys = ReadList(row.Text(15)),
                });
            return (decks, cards);
        });

        if (decks is null || cards is null)
        {
            return [];
        }

        var byId = decks.ToDictionary(deck => deck.Id);
        foreach (var card in cards)
        {
            if (byId.TryGetValue(card.DeckId, out var deck))
            {
                deck.Cards.Add(card);
            }
        }

        _lastDeck = Math.Max(_lastDeck, decks.Count > 0 ? decks.Max(deck => deck.Id) : 0);
        _lastCard = Math.Max(_lastCard, cards.Count > 0 ? cards.Max(card => card.Id) : 0);
        return decks;
    }

    /// <summary>
    /// A new course from <paramref name="source"/>: the document is copied into the app's folder (off the UI thread), the
    /// course written. <c>null</c> when it could not be copied.
    /// </summary>
    public async Task<StudyDeck?> CreateAsync(string source)
    {
        long id = ++_lastDeck;
        string name = Path.GetFileName(source);
        string copy = Path.Combine(Documents, $"{id}{Path.GetExtension(source).ToLowerInvariant()}");
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Documents);
                File.Copy(source, copy, overwrite: true);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Study] Not copied: {ex.Message}");
            return null;
        }

        var deck = new StudyDeck(id, Path.GetFileNameWithoutExtension(source), name, copy, DateTimeOffset.Now);
        Save(deck);
        return deck;
    }

    /// <summary>A card for <paramref name="deck"/>, new (not written: see <see cref="Add"/>).</summary>
    public StudyCard NewCard(StudyDeck deck, string question, string answer, string source, string chapter) =>
        new(++_lastCard, deck.Id, question, answer, source, chapter, DateTimeOffset.Now);

    /// <summary>Writes the course itself (its title, summary, chapters, mind maps and what was drawn on them).</summary>
    public void Save(StudyDeck deck)
    {
        var row = (deck.Id, deck.Title, deck.SourceName, deck.File, deck.Summary, Chapters: JsonSerializer.Serialize(deck.Chapters), deck.Pages,
            Created: ToStored(deck.Created), Maps: deck.MindMaps.Count == 0 ? "" : JsonSerializer.Serialize(deck.MindMaps),
            Ink: deck.MapInks.Values.All(ink => ink.IsEmpty) ? "" : JsonSerializer.Serialize(deck.MapInks.Where(each => !each.Value.IsEmpty).ToDictionary()),
            Section: deck.SectionId);
        _ = Run(db =>
        {
            db.Execute(
                "INSERT OR REPLACE INTO Decks (id, title, source_name, file, summary, chapters, pages, created_at, mindmaps, mapink, section) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)",
                row.Id, row.Title, row.SourceName, row.File, row.Summary, row.Chapters, row.Pages, row.Created, row.Maps, row.Ink, row.Section);
            return true;
        });
    }

    /// <summary>Writes cards, new or changed (answered, edited), in one go.</summary>
    public void Save(IEnumerable<StudyCard> cards)
    {
        var rows = cards.Select(card => (card.Id, card.DeckId, card.Question, card.Answer, card.Source, card.Chapter,
            Created: ToStored(card.Created), State: (long)card.State, Step: (long)card.Step, Due: ToStored(card.Due),
            Interval: (long)card.IntervalDays, Ease: (long)Math.Round(card.Ease * 1000), Reps: (long)card.Reps, Lapses: (long)card.Lapses,
            card.Choice, Decoys: JsonSerializer.Serialize(card.Decoys))).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        _ = Run(db => InTransaction(db, () =>
        {
            foreach (var row in rows)
            {
                db.Execute(
                    """
                    INSERT OR REPLACE INTO Cards (id, deck_id, question, answer, source, chapter, created_at, state, step, due, interval_days, ease, reps, lapses, choice, decoys)
                    VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16)
                    """,
                    row.Id, row.DeckId, row.Question, row.Answer, row.Source, row.Chapter, row.Created, row.State, row.Step, row.Due,
                    row.Interval, row.Ease, row.Reps, row.Lapses, row.Choice, row.Decoys);
            }
        }));
    }

    public void Delete(StudyCard card)
    {
        long id = card.Id;
        _ = Run(db =>
        {
            db.Execute("DELETE FROM Cards WHERE id = ?1", id);
            return true;
        });
    }

    /// <summary>The sections, in their order; none when the database cannot be read.</summary>
    public async Task<IReadOnlyList<StudySection>> LoadSectionsAsync()
    {
        var sections = await Run(db => db.Query(
            "SELECT id, name, color, position FROM Sections ORDER BY position, id",
            row => new StudySection(row.Int64(0), row.Text(1), row.Text(2), (int)row.Int64(3))));
        if (sections is null)
        {
            return [];
        }

        _lastSection = Math.Max(_lastSection, sections.Count > 0 ? sections.Max(section => section.Id) : 0);
        return sections;
    }

    /// <summary>A new section (not written: see <see cref="Save(StudySection)"/>).</summary>
    public StudySection NewSection(string name, string color, int position) => new(++_lastSection, name, color, position);

    /// <summary>Writes a section (its name, colour, place).</summary>
    public void Save(StudySection section)
    {
        var row = (section.Id, section.Name, section.Color, section.Position);
        _ = Run(db =>
        {
            db.Execute("INSERT OR REPLACE INTO Sections (id, name, color, position) VALUES (?1, ?2, ?3, ?4)", row.Id, row.Name, row.Color, row.Position);
            return true;
        });
    }

    /// <summary>A section gone: its courses stay, in none.</summary>
    public void Delete(StudySection section)
    {
        long id = section.Id;
        _ = Run(db => InTransaction(db, () =>
        {
            db.Execute("UPDATE Decks SET section = 0 WHERE section = ?1", id);
            db.Execute("DELETE FROM Sections WHERE id = ?1", id);
        }));
    }

    /// <summary>The course, its cards and its document's copy, gone.</summary>
    public void Delete(StudyDeck deck)
    {
        long id = deck.Id;
        string file = deck.File;
        _ = Run(db => InTransaction(db, () =>
        {
            db.Execute("DELETE FROM Cards WHERE deck_id = ?1", id);
            db.Execute("DELETE FROM Decks WHERE id = ?1", id);
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Study] Copy left: {ex.Message}");
            }
        }));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tail = _tail.ContinueWith(_ =>
        {
            _closed = true;
            _db?.Dispose();
            _db = null;
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (!_tail.Wait(ExitTimeout))
        {
            Debug.WriteLine("[Study] The last write did not finish in time.");
        }
    }

    private static bool InTransaction(SqliteDatabase db, Action work)
    {
        db.Execute("BEGIN");
        try
        {
            work();
            db.Execute("COMMIT");
            return true;
        }
        catch (SqliteException)
        {
            try
            {
                db.Execute("ROLLBACK");
            }
            catch (SqliteException)
            {
                // The transaction is gone already.
            }

            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the database once what was asked before is done; the result comes back on the UI
    /// thread. <c>default</c> when the database cannot be used.
    /// </summary>
    private Task<T?> Run<T>(Func<SqliteDatabase, T> work)
    {
        var result = new TaskCompletionSource<T?>();
        _tail = _tail.ContinueWith(_ =>
        {
            T? value = default;
            try
            {
                if (Open() is { } db)
                {
                    value = work(db);
                }
            }
            catch (Exception ex) when (ex is SqliteException or ObjectDisposedException or ArgumentException)
            {
                Debug.WriteLine($"[Study] Database: {ex.Message}");
            }

            if (!_dispatcher.TryEnqueue(() => result.SetResult(value)))
            {
                result.SetResult(value); // Shutting down: nobody waits on the UI thread any more.
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return result.Task;
    }

    /// <summary>The database, opened (and created, the first time) on first use.</summary>
    private SqliteDatabase? Open()
    {
        if (_db is not null || _closed)
        {
            return _db;
        }

        try
        {
            Directory.CreateDirectory(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Study] No folder for the database: {ex.Message}");
            return null;
        }

        var db = new SqliteDatabase(Path.Combine(_folder, "study.db"), BusyTimeout);
        try
        {
            db.Execute("PRAGMA journal_mode = WAL");
            db.Execute("PRAGMA synchronous = NORMAL");
            db.Execute("""
                CREATE TABLE IF NOT EXISTS Decks (
                    id INTEGER PRIMARY KEY,
                    title TEXT NOT NULL DEFAULT '',
                    source_name TEXT NOT NULL DEFAULT '',
                    file TEXT NOT NULL DEFAULT '',
                    summary TEXT NOT NULL DEFAULT '',
                    chapters TEXT NOT NULL DEFAULT '[]',
                    pages INTEGER NOT NULL DEFAULT 0,
                    created_at INTEGER NOT NULL)
                """);
            db.Execute("""
                CREATE TABLE IF NOT EXISTS Cards (
                    id INTEGER PRIMARY KEY,
                    deck_id INTEGER NOT NULL,
                    question TEXT NOT NULL,
                    answer TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT '',
                    chapter TEXT NOT NULL DEFAULT '',
                    created_at INTEGER NOT NULL,
                    state INTEGER NOT NULL DEFAULT 0,
                    step INTEGER NOT NULL DEFAULT 0,
                    due INTEGER NOT NULL,
                    interval_days INTEGER NOT NULL DEFAULT 0,
                    ease INTEGER NOT NULL DEFAULT 2500,
                    reps INTEGER NOT NULL DEFAULT 0,
                    lapses INTEGER NOT NULL DEFAULT 0)
                """);
            db.Execute("CREATE INDEX IF NOT EXISTS Cards_deck ON Cards (deck_id)");

            // Courses from before the mind maps, and before drawing on them: their columns come, empty.
            foreach (string column in new[] { "mindmaps", "mapink" })
            {
                if (db.Query($"SELECT COUNT(*) FROM pragma_table_info('Decks') WHERE name = '{column}'", row => row.Int64(0))[0] == 0)
                {
                    db.Execute($"ALTER TABLE Decks ADD COLUMN {column} TEXT NOT NULL DEFAULT ''");
                }
            }

            // The sections (subjects), and courses from before them: in none.
            db.Execute("""
                CREATE TABLE IF NOT EXISTS Sections (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL DEFAULT '',
                    color TEXT NOT NULL DEFAULT '',
                    position INTEGER NOT NULL DEFAULT 0)
                """);
            if (db.Query("SELECT COUNT(*) FROM pragma_table_info('Decks') WHERE name = 'section'", row => row.Int64(0))[0] == 0)
            {
                db.Execute("ALTER TABLE Decks ADD COLUMN section INTEGER NOT NULL DEFAULT 0");
            }

            // Cards made before the multiple choices: their columns come, empty (Notch fills them, see StudyViewModel).
            foreach (string column in new[] { "choice", "decoys" })
            {
                if (db.Query($"SELECT COUNT(*) FROM pragma_table_info('Cards') WHERE name = '{column}'", row => row.Int64(0))[0] == 0)
                {
                    db.Execute($"ALTER TABLE Cards ADD COLUMN {column} TEXT NOT NULL DEFAULT ''");
                }
            }
        }
        catch
        {
            db.Dispose();
            throw;
        }

        return _db = db;
    }

    /// <summary>A course's mind maps as written; none if there are none (or they do not read).</summary>
    private static List<MindMap> ReadMaps(string json)
    {
        if (json.Length == 0)
        {
            return [];
        }

        try
        {
            return MindMap.Tidy(JsonSerializer.Deserialize<List<MindMap>>(json, MapsJson));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions MapsJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>What was drawn on a course's mind maps; nothing if nothing was (or it does not read).</summary>
    private static Dictionary<string, MapInk> ReadInks(string json)
    {
        if (json.Length == 0)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, MapInk>>(json, MapsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> ReadList(string json)
    {
        if (json.Length == 0)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<StudyChapter> ReadChapters(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<StudyChapter>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // Dates are stored as Unix milliseconds (UTC).
    private static long ToStored(DateTimeOffset date) => date.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromStored(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime();
}
