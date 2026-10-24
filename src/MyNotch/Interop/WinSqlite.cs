using System.Runtime.InteropServices;

namespace MyNotch.Interop;

/// <summary>
/// Read-only access to a SQLite database through <c>winsqlite3.dll</c>, the SQLite that ships with Windows 10 and
/// later: no package, no native library to bring along. Just enough to read one value here; <see cref="SqliteDatabase"/>
/// keeps a database of its own (the notes).
/// </summary>
internal static partial class WinSqlite
{
    private const string Library = "winsqlite3.dll";
    internal const int SQLITE_OK = 0;
    internal const int SQLITE_ROW = 100;
    internal const int SQLITE_DONE = 101;
    private const int SQLITE_OPEN_READONLY = 0x1;
    internal static readonly nint SQLITE_TRANSIENT = -1;

    /// <summary>
    /// The <paramref name="valueColumn"/> of the row whose <paramref name="keyColumn"/> is <paramref name="key"/>, as
    /// bytes; <c>null</c> if there is none. Waits up to <paramref name="busyTimeout"/> for a writer to finish.
    /// </summary>
    /// <exception cref="SqliteException">The file cannot be opened or read (locked, damaged…).</exception>
    public static byte[]? ReadValue(string path, string table, string keyColumn, string valueColumn, string key, TimeSpan busyTimeout)
    {
        Check(sqlite3_open_v2(path, out nint db, SQLITE_OPEN_READONLY, 0), db, "open");
        try
        {
            sqlite3_busy_timeout(db, (int)busyTimeout.TotalMilliseconds);
            Check(sqlite3_prepare_v2(db, $"SELECT {valueColumn} FROM {table} WHERE {keyColumn} = ?1", -1, out nint statement, 0), db, "prepare");
            try
            {
                Check(sqlite3_bind_text(statement, 1, key, -1, SQLITE_TRANSIENT), db, "bind");
                int step = sqlite3_step(statement);
                if (step != SQLITE_ROW)
                {
                    return step == SQLITE_DONE ? null : throw new SqliteException(step, "step");
                }

                int length = sqlite3_column_bytes(statement, 0);
                nint data = sqlite3_column_blob(statement, 0);
                var value = new byte[length];
                if (length > 0)
                {
                    Marshal.Copy(data, value, 0, length);
                }

                return value;
            }
            finally
            {
                sqlite3_finalize(statement);
            }
        }
        finally
        {
            sqlite3_close_v2(db);
        }
    }

    private static void Check(int result, nint db, string step)
    {
        if (result != SQLITE_OK)
        {
            throw new SqliteException(result, step);
        }
    }

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int sqlite3_open_v2(string filename, out nint db, int flags, nint vfs);

    [LibraryImport(Library)]
    internal static partial int sqlite3_busy_timeout(nint db, int milliseconds);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int sqlite3_prepare_v2(nint db, string sql, int length, out nint statement, nint tail);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int sqlite3_bind_text(nint statement, int index, string text, int length, nint destructor);

    [LibraryImport(Library)]
    internal static partial int sqlite3_step(nint statement);

    [LibraryImport(Library)]
    internal static partial nint sqlite3_column_blob(nint statement, int column);

    [LibraryImport(Library)]
    internal static partial int sqlite3_column_bytes(nint statement, int column);

    [LibraryImport(Library)]
    internal static partial int sqlite3_finalize(nint statement);

    [LibraryImport(Library)]
    internal static partial int sqlite3_close_v2(nint db);

    [LibraryImport(Library)]
    internal static partial int sqlite3_bind_int64(nint statement, int index, long value);

    [LibraryImport(Library)]
    internal static partial long sqlite3_column_int64(nint statement, int column);

    [LibraryImport(Library)]
    internal static partial nint sqlite3_column_text(nint statement, int column);

    [LibraryImport(Library)]
    internal static partial nint sqlite3_errmsg(nint db);
}

/// <summary>SQLite said no: 5 (busy) and 6 (locked) mean another process is writing.</summary>
internal sealed class SqliteException(int code, string step) : Exception($"SQLite {code} ({step})")
{
    public int Code { get; } = code;

    public bool IsLocked => Code is 5 or 6;
}
