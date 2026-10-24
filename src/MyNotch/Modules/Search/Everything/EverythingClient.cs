using System.Buffers.Binary;
using System.Diagnostics;
using MyNotch.Interop;
using MyNotch.Windowing;

namespace MyNotch.Modules.Search.Everything;

/// <summary>
/// Asks Everything (voidtools) for files and folders, the way its SDK does, without its DLL: a query goes to
/// Everything's hidden window as a <c>WM_COPYDATA</c> message (<c>EVERYTHING_IPC_QUERY2</c>), and the results come back
/// to a hidden window of ours, the same way, a few milliseconds later. Any Everything 1.4.1 or later answers, 32 or 64
/// bit, run by the notch or by the user (there is one at a time: it is also the one the Everything service serves).
/// </summary>
/// <remarks>
/// Lives on the UI thread: sending a query only hands it over (Everything searches on its own thread), and the reply is
/// copied out of the message at once. Everything does one query per window at a time: a new one replaces the last.
/// </remarks>
internal sealed class EverythingClient : IDisposable
{
    private const string WindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";
    private const uint CopyDataQuery2W = 18;
    private const uint IncrementRunCountW = 24;
    private const nint IsDatabaseLoadedCommand = 401;
    private const nint ExitCommand = 4;
    private const uint Timeout = 1000;

    // What each result carries (EVERYTHING_IPC_QUERY2_REQUEST_*), and how they are sorted: recent first, a sort
    // Everything keeps ready by default (fast_date_modified_sort).
    private const uint RequestName = 0x1;
    private const uint RequestPath = 0x2;
    private const uint RequestFullPath = 0x4;
    private const uint RequestExtension = 0x8;
    private const uint RequestSize = 0x10;
    private const uint RequestDateCreated = 0x20;
    private const uint RequestDateModified = 0x40;
    private const uint RequestDateAccessed = 0x80;
    private const uint RequestAttributes = 0x100;
    private const uint RequestFileListName = 0x200;
    private const uint RequestRunCount = 0x400;
    private const uint RequestDateRun = 0x800;
    private const uint RequestDateRecentlyChanged = 0x1000;
    private const uint RequestHighlightedName = 0x2000;
    private const uint Requested = RequestName | RequestPath | RequestSize | RequestDateModified | RequestRunCount | RequestHighlightedName;
    private const uint SortDateModifiedDescending = 14;
    private const uint ItemFolder = 0x1;

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);

    private readonly nint _hwnd;
    private readonly WindowMessageHook? _hook;
    private TaskCompletionSource<IReadOnlyList<EverythingItem>?>? _pending;
    private uint _pendingId;
    private uint _nextId = 0x4E4F0000;

    public EverythingClient()
    {
        // A predefined window class, so there is nothing to register; HWND_MESSAGE keeps it out of sight.
        _hwnd = NativeMethods.CreateWindowEx(0, "STATIC", "MyNotch.Everything", 0, 0, 0, 0, 0, NativeMethods.HWND_MESSAGE, 0, 0, 0);
        if (_hwnd == 0)
        {
            Debug.WriteLine("[Everything] No message window: no file search.");
            return;
        }

        _hook = new WindowMessageHook(_hwnd, OnMessage);
    }

    /// <summary>The Everything running (the notch's own, or the user's); 0 if none.</summary>
    public static nint FindEverything() => NativeMethods.FindWindow(WindowClass, null);

    /// <summary>
    /// Whether Everything has its index ready (it answers before, with what it has so far). It may take a while to say
    /// (busy loading): never ask from the UI thread.
    /// </summary>
    public static bool IsDatabaseLoaded(nint everything) =>
        NativeMethods.SendMessageTimeout(everything, NativeMethods.WM_USER, IsDatabaseLoadedCommand, 0, NativeMethods.SMTO_ABORTIFHUNG, Timeout, out nint loaded) != 0
        && loaded != 0;

    /// <summary>Closes that Everything (its index is saved for next time).</summary>
    public static void Exit(nint everything) =>
        NativeMethods.SendMessageTimeout(everything, NativeMethods.WM_USER, ExitCommand, 0, NativeMethods.SMTO_ABORTIFHUNG, Timeout, out _);

    /// <summary>
    /// Searches with Everything's syntax (spaces mean "and", "ext:pdf", "\folder\"…), most recently changed first.
    /// <c>null</c> when no Everything answers, or when a newer query replaced this one.
    /// </summary>
    public async Task<IReadOnlyList<EverythingItem>?> QueryAsync(string search, int maxResults)
    {
        _pending?.TrySetResult(null); // Replaced.
        _pending = null;

        nint everything = FindEverything();
        if (everything == 0 || _hwnd == 0)
        {
            return null;
        }

        // EVERYTHING_IPC_QUERY2: seven DWORDs, then the search, null terminated.
        uint id = ++_nextId;
        int size = 7 * sizeof(uint) + (search.Length + 1) * sizeof(char);
        var query = new byte[size];
        var header = query.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], (uint)_hwnd); // 32 bits are enough for a window handle.
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], id);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 0); // No match case, no whole word, no path, no regex.
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)maxResults);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], Requested);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], SortDateModifiedDescending);
        search.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(header[28..]));

        var reply = new TaskCompletionSource<IReadOnlyList<EverythingItem>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = reply;
        _pendingId = id;

        // Handed over from another thread: Everything busy (loading its index…) must never freeze the notch. Its reply
        // still comes to the window here, on the UI thread, which stays free to take it.
        nint self = _hwnd;
        if (!await Task.Run(() => Send(everything, self, CopyDataQuery2W, query)))
        {
            if (_pending == reply)
            {
                _pending = null;
            }

            reply.TrySetResult(null);
        }

        // An answer that never comes (Everything closed meanwhile) must not leave the search waiting.
        _ = Task.Delay(ReplyTimeout).ContinueWith(_ => reply.TrySetResult(null), TaskScheduler.Default);
        return await reply.Task;
    }

    /// <summary>Tells Everything a file was opened (EVERYTHING_IPC_COPYDATA_INC_RUN_COUNTW): it ranks it higher next time.</summary>
    public void CountRun(string path)
    {
        nint everything = FindEverything();
        if (everything == 0 || _hwnd == 0)
        {
            return;
        }

        var text = new byte[(path.Length + 1) * sizeof(char)];
        path.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(text.AsSpan()));
        nint self = _hwnd;
        _ = Task.Run(() => Send(everything, self, IncrementRunCountW, text));
    }

    /// <summary>A WM_COPYDATA to Everything; <c>false</c> if it would not take it (in time).</summary>
    private static unsafe bool Send(nint everything, nint replyTo, uint kind, byte[] data)
    {
        fixed (byte* bytes = data)
        {
            var copy = new NativeMethods.CopyData { Data = (nint)kind, Size = (uint)data.Length, Pointer = (nint)bytes };
            return NativeMethods.SendMessageTimeout(everything, NativeMethods.WM_COPYDATA, replyTo, (nint)(&copy), NativeMethods.SMTO_ABORTIFHUNG, Timeout, out nint accepted) != 0
                && accepted != 0;
        }
    }

    public void Dispose()
    {
        _pending?.TrySetResult(null);
        _hook?.Dispose();
        if (_hwnd != 0)
        {
            NativeMethods.DestroyWindow(_hwnd);
        }
    }

    private unsafe nint? OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message != NativeMethods.WM_COPYDATA || lParam == 0)
        {
            return null;
        }

        var copy = (NativeMethods.CopyData*)lParam;
        if ((uint)copy->Data != _pendingId || _pending is not { } pending)
        {
            return 1; // A reply to a query since replaced.
        }

        _pending = null;
        try
        {
            pending.TrySetResult(Parse(new ReadOnlySpan<byte>((void*)copy->Pointer, (int)copy->Size)));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            Debug.WriteLine($"[Everything] Unreadable reply: {ex.Message}");
            pending.TrySetResult(null);
        }

        return 1;
    }

    /// <summary>
    /// Reads an <c>EVERYTHING_IPC_LIST2</c>: a header, one (flags, offset) pair per item, then each item's data, in the
    /// order of the request flags, for those Everything could give (the reply says which).
    /// </summary>
    private static List<EverythingItem> Parse(ReadOnlySpan<byte> list)
    {
        int count = (int)UInt(list, 4);
        uint given = UInt(list, 12);
        var items = new List<EverythingItem>(count);
        for (int i = 0; i < count; i++)
        {
            uint flags = UInt(list, 20 + i * 8);
            int at = (int)UInt(list, 24 + i * 8);
            string name = "", folder = "";
            long? size = null;
            DateTimeOffset? modified = null;
            int runCount = 0;
            IReadOnlyList<(int, int)> matches = [];

            if ((given & RequestName) != 0)
            {
                name = Text(list, ref at);
            }

            if ((given & RequestPath) != 0)
            {
                folder = Text(list, ref at);
            }

            if ((given & RequestFullPath) != 0)
            {
                Text(list, ref at);
            }

            if ((given & RequestSize) != 0)
            {
                long value = BinaryPrimitives.ReadInt64LittleEndian(list[at..]);
                size = value >= 0 ? value : null; // Folders: -1 unless their sizes are indexed.
                at += 8;
            }

            if ((given & RequestExtension) != 0)
            {
                Text(list, ref at);
            }

            if ((given & RequestDateCreated) != 0)
            {
                at += 8;
            }

            if ((given & RequestDateModified) != 0)
            {
                modified = Date(BinaryPrimitives.ReadInt64LittleEndian(list[at..]));
                at += 8;
            }

            at += (given & RequestDateAccessed) != 0 ? 8 : 0;
            at += (given & RequestAttributes) != 0 ? 4 : 0;
            if ((given & RequestFileListName) != 0)
            {
                Text(list, ref at);
            }

            if ((given & RequestRunCount) != 0)
            {
                runCount = (int)UInt(list, at);
                at += 4;
            }

            at += (given & RequestDateRun) != 0 ? 8 : 0;
            at += (given & RequestDateRecentlyChanged) != 0 ? 8 : 0;
            if ((given & RequestHighlightedName) != 0)
            {
                matches = Highlights(Text(list, ref at));
            }

            items.Add(new EverythingItem(name, folder, (flags & ItemFolder) != 0, size, modified, runCount, matches));
        }

        return items;
    }

    private static uint UInt(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);

    /// <summary>A DWORD length in characters, then the text and its null terminator.</summary>
    private static string Text(ReadOnlySpan<byte> data, ref int at)
    {
        int length = (int)UInt(data, at);
        at += 4;
        string text = new(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(data.Slice(at, length * 2)));
        at += (length + 1) * 2;
        return text;
    }

    private static DateTimeOffset? Date(long fileTime) =>
        fileTime is > 0 and < long.MaxValue ? DateTimeOffset.FromFileTime(fileTime) : null;

    /// <summary>"*Fact*ure.pdf": the starred parts matched ("**" is a star itself).</summary>
    private static List<(int Start, int Length)> Highlights(string highlighted)
    {
        var matches = new List<(int, int)>();
        int position = 0;
        int start = -1;
        for (int i = 0; i < highlighted.Length; i++)
        {
            if (highlighted[i] == '*')
            {
                if (i + 1 < highlighted.Length && highlighted[i + 1] == '*')
                {
                    i++;
                    position++;
                    continue;
                }

                if (start < 0)
                {
                    start = position;
                }
                else
                {
                    if (position > start)
                    {
                        matches.Add((start, position - start));
                    }

                    start = -1;
                }

                continue;
            }

            position++;
        }

        return matches;
    }
}
