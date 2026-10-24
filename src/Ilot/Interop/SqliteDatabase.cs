using System.Runtime.InteropServices;
using static Ilot.Interop.WinSqlite;

namespace Ilot.Interop;

/// <summary>
/// A SQLite database of the app's own, through the <c>winsqlite3.dll</c> of Windows (see <see cref="WinSqlite"/>):
/// statements with parameters (<c>?1</c>, <c>?2</c>… bound to text or integers), read row by row. One connection,
/// used by one thread at a time.
/// </summary>
internal sealed class SqliteDatabase : IDisposable
{
    private const int SQLITE_OPEN_READWRITE = 0x2;
    private const int SQLITE_OPEN_CREATE = 0x4;

    private nint _db;

    /// <summary>Opens the file, creating it if needed.</summary>
    /// <exception cref="SqliteException">It cannot be opened (no room, damaged…).</exception>
    public SqliteDatabase(string path, TimeSpan busyTimeout)
    {
        int result = sqlite3_open_v2(path, out _db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, 0);
        if (result != SQLITE_OK)
        {
            sqlite3_close_v2(_db);
            throw new SqliteException(result, "open");
        }

        sqlite3_busy_timeout(_db, (int)busyTimeout.TotalMilliseconds);
    }

    /// <summary>Runs a statement that returns nothing (CREATE, INSERT, DELETE…).</summary>
    public void Execute(string sql, params object?[] parameters)
    {
        nint statement = Prepare(sql, parameters);
        try
        {
            int step = sqlite3_step(statement);
            while (step == SQLITE_ROW)
            {
                step = sqlite3_step(statement);
            }

            Check(step, SQLITE_DONE, "step");
        }
        finally
        {
            sqlite3_finalize(statement);
        }
    }

    /// <summary>Runs a query: <paramref name="read"/> makes something of each row.</summary>
    public List<T> Query<T>(string sql, Func<Row, T> read, params object?[] parameters)
    {
        nint statement = Prepare(sql, parameters);
        try
        {
            var rows = new List<T>();
            int step;
            while ((step = sqlite3_step(statement)) == SQLITE_ROW)
            {
                rows.Add(read(new Row(statement)));
            }

            Check(step, SQLITE_DONE, "step");
            return rows;
        }
        finally
        {
            sqlite3_finalize(statement);
        }
    }

    public void Dispose()
    {
        if (_db != 0)
        {
            sqlite3_close_v2(_db);
            _db = 0;
        }
    }

    private nint Prepare(string sql, object?[] parameters)
    {
        ObjectDisposedException.ThrowIf(_db == 0, this);
        Check(sqlite3_prepare_v2(_db, sql, -1, out nint statement, 0), SQLITE_OK, "prepare");
        try
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                int result = parameters[i] switch
                {
                    long value => sqlite3_bind_int64(statement, i + 1, value),
                    int value => sqlite3_bind_int64(statement, i + 1, value),
                    // Bound up to the first NUL: there is none in what a person types.
                    string value => sqlite3_bind_text(statement, i + 1, value.Replace("\0", "", StringComparison.Ordinal), -1, SQLITE_TRANSIENT),
                    _ => throw new ArgumentException($"Cannot bind a {parameters[i]?.GetType().Name ?? "null"}.", nameof(parameters)),
                };
                Check(result, SQLITE_OK, "bind");
            }

            return statement;
        }
        catch
        {
            sqlite3_finalize(statement);
            throw;
        }
    }

    private void Check(int result, int expected, string step)
    {
        if (result != expected)
        {
            string? message = Marshal.PtrToStringUTF8(sqlite3_errmsg(_db));
            throw new SqliteException(result, message is null ? step : $"{step}: {message}");
        }
    }

    /// <summary>The current row of a query, read by column index.</summary>
    public readonly struct Row(nint statement)
    {
        public long Int64(int column) => sqlite3_column_int64(statement, column);

        public string Text(int column)
        {
            nint text = sqlite3_column_text(statement, column);
            return text == 0 ? "" : Marshal.PtrToStringUTF8(text, sqlite3_column_bytes(statement, column));
        }
    }
}
