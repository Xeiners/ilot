using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Ilot.Interop;

namespace Ilot.Modules.Notes;

/// <summary>
/// Keeps the notes in a SQLite database, <c>%LOCALAPPDATA%\Ilot\notes.db</c> (the <c>winsqlite3.dll</c> of
/// Windows). Nothing is read before the Notes tab first shows; changes are written 300 ms after the last keystroke,
/// off the UI thread, one write after another. Closing the app writes what is left.
/// </summary>
public sealed class NotesService : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(3);

    private static readonly string FilePath = Path.Combine(
        AppData.Folder, "notes.db");

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly Dictionary<long, Note> _pending = [];

    // Every access to the database goes down this chain, on the thread pool: one at a time, in order.
    private Task _tail = Task.CompletedTask;
    private SqliteDatabase? _db;
    private long _lastId;
    private bool _disposed;
    private bool _closed; // On the worker: the database has been closed for good.

    public NotesService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = SaveDelay;
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => WritePending();
    }

    /// <summary>Changes have landed in the database (<c>true</c>), or could not be written (<c>false</c>).</summary>
    public event EventHandler<bool>? Saved;

    /// <summary>Changes wait for their write.</summary>
    public bool HasPendingChanges => _pending.Count > 0;

    /// <summary>Every note, latest changed first. An unreadable database gives none (and is left as it is).</summary>
    public async Task<IReadOnlyList<Note>> LoadAsync()
    {
        var notes = await Run(db => db.Query(
            "SELECT id, title, content, plain, created_at, updated_at FROM Notes ORDER BY updated_at DESC",
            row => Restore(row.Int64(0), row.Text(1), row.Text(2), row.Text(3), row.Int64(4), row.Int64(5))));
        _lastId = Math.Max(_lastId, notes?.Count > 0 ? notes.Max(note => note.Id) : 0);
        return notes ?? [];
    }

    /// <summary>A new, empty note. It is written only once something is written in it (<see cref="Schedule"/>).</summary>
    public Note Create()
    {
        var now = DateTimeOffset.Now;
        return new Note(++_lastId, "", "", "", now, now);
    }

    /// <summary>The note changed: it is written a moment after the last change.</summary>
    public void Schedule(Note note)
    {
        _pending[note.Id] = note;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Delete(Note note)
    {
        _pending.Remove(note.Id);
        long id = note.Id;
        _ = Run(db =>
        {
            db.Execute("DELETE FROM Notes WHERE id = ?1", id);
            return true;
        });
    }

    /// <summary>Writes what waits at once (switching notes, leaving the tab…).</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        WritePending();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Flush();
        _disposed = true;

        // Waits for the chain itself (on the thread pool), not for results handed to this very thread.
        _tail = _tail.ContinueWith(_ =>
        {
            _closed = true;
            _db?.Dispose();
            _db = null;
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (!_tail.Wait(ExitTimeout))
        {
            Debug.WriteLine("[Notes] The last write did not finish in time.");
        }
    }

    private void WritePending()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        // A snapshot taken on the UI thread: the worker never touches the notes themselves.
        var rows = _pending.Values
            .Select(note => (note.Id, note.Title, note.Content, note.Plain, Created: ToStored(note.CreatedAt), Updated: ToStored(note.UpdatedAt)))
            .ToList();
        _pending.Clear();
        _ = Write();

        async Task Write()
        {
            bool saved = await Run(db =>
            {
                db.Execute("BEGIN");
                try
                {
                    foreach (var row in rows)
                    {
                        db.Execute(
                            "INSERT OR REPLACE INTO Notes (id, title, content, plain, created_at, updated_at) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
                            row.Id, row.Title, row.Content, row.Plain, row.Created, row.Updated);
                    }

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
            });

            if (!_disposed)
            {
                Saved?.Invoke(this, saved);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the database once what was asked before is done; the result comes back on
    /// the UI thread. <c>default</c> when the database cannot be used.
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
                Debug.WriteLine($"[Notes] Database: {ex.Message}");
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
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Notes] No folder for the database: {ex.Message}");
            return null;
        }

        var db = new SqliteDatabase(FilePath, BusyTimeout);
        try
        {
            db.Execute("PRAGMA journal_mode = WAL");
            db.Execute("PRAGMA synchronous = NORMAL");
            db.Execute("""
                CREATE TABLE IF NOT EXISTS Notes (
                    id INTEGER PRIMARY KEY,
                    title TEXT NOT NULL DEFAULT '',
                    content TEXT NOT NULL DEFAULT '',
                    plain TEXT NOT NULL DEFAULT '',
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL)
                """);

            // Notes written before the rich editor have no plain version yet: the column comes, filled on load.
            if (db.Query("SELECT COUNT(*) FROM pragma_table_info('Notes') WHERE name = 'plain'", row => row.Int64(0))[0] == 0)
            {
                db.Execute("ALTER TABLE Notes ADD COLUMN plain TEXT NOT NULL DEFAULT ''");
            }

            db.Execute("CREATE INDEX IF NOT EXISTS Notes_updated_at ON Notes (updated_at DESC)");
        }
        catch
        {
            db.Dispose();
            throw;
        }

        return _db = db;
    }

    /// <summary>A note as stored; one in Markdown (from before the rich editor) gets its plain version here.</summary>
    private static Note Restore(long id, string title, string content, string plain, long created, long updated)
    {
        if (plain.Length == 0 && content.Length > 0 && !content.StartsWith(@"{\rtf", StringComparison.Ordinal))
        {
            plain = Note.PlainFromMarkdown(content);
        }

        return new Note(id, title, content, plain, FromStored(created), FromStored(updated));
    }

    // Dates are stored as Unix milliseconds (UTC).
    private static long ToStored(DateTimeOffset date) => date.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromStored(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime();
}
