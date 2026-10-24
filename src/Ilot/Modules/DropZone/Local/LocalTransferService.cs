using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Ilot.Localization;

namespace Ilot.Modules.DropZone.Local;

/// <summary>
/// Files between the PC and a phone on the same Wi-Fi, straight through the router: a tiny HTTP server on this PC
/// (port 80 when free, else 8080), reached at <c>http://192.168.x.x/&lt;key&gt;/</c>, or by its name on the network,
/// <c>http://mynotch.local/</c> (see <see cref="MdnsResponder"/>). It serves a page with the files
/// shared (<c>GET download/{i}</c>, folders zipped on the fly) and takes files the other way (<c>POST upload</c>,
/// saved in Downloads). Nothing goes through the Internet.
/// </summary>
/// <remarks>
/// <para>
/// A plain <see cref="TcpListener"/> rather than <c>HttpListener</c>: the latter needs an administrator to listen
/// on anything but localhost. Each session gets a random key in its address: nobody else on the network can list
/// or fetch the files, and an old QR code no longer works.
/// </para>
/// <para>
/// The server only exists while a sharing is open: started by the Drop Zone, stopped by the user, after
/// <see cref="IdleTimeout"/> without any request, or when the PC leaves that network. In between it waits on an
/// accept, which costs nothing. Events are raised on the UI thread.
/// </para>
/// </remarks>
public sealed class LocalTransferService : IDisposable
{
    // 80 spares typing a port; Windows may hold it (IIS, http.sys…): then 8080, then any free one.
    private static readonly int[] Ports = [80, 8080, 0];
    private const string NetworkName = "mynotch";
    private const int MaxConnections = 64;
    private const int BufferSize = 256 * 1024;
    private const int MaxHeadSize = 16 * 1024;
    private const int MaxTextBytes = 64 * 1024;
    private const long MaxInlineImageBytes = 100 * 1024 * 1024;
    private const string KeyAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";
    private const string CorsHeaders = "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, HEAD, OPTIONS\r\nAccess-Control-Allow-Headers: *\r\n";
    private const int MaxMisses = 10;
    private const byte TlsHandshake = 0x16;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DecisionWait = TimeSpan.FromSeconds(20);

    private static readonly string SessionFolder = Path.Combine(
        AppData.Folder, "LocalChat");

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _idle;
    private readonly List<LocalTransfer> _active = [];
    private readonly List<IncomingFile> _incoming = [];
    private readonly ConcurrentDictionary<string, IncomingFile> _incomingById = [];
    private readonly List<LocalMessage> _messages = [];
    private volatile LocalMessage[] _messagesSnapshot = [];
    private long _sequence;
    private TaskCompletionSource _feedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpListener? _listener;
    private MdnsResponder? _mdns;
    private CancellationTokenSource? _session;
    private IPAddress? _address;
    private volatile SharedFile[] _shared = [];
    private readonly Lock _gate = new();
    private string _key = "";
    private string _code = "";
    private int _misses;
    private long _lockedUntil;
    private int _connections;

    public LocalTransferService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _idle = dispatcher.CreateTimer();
        _idle.Interval = IdleTimeout;
        _idle.IsRepeating = false;
        _idle.Tick += (_, _) =>
        {
            if (_active.Count > 0)
            {
                _idle.Start(); // A slow transfer is still going: not idle.
            }
            else
            {
                Stop();
            }
        };
        _ = Task.Run(CleanSessionFolder);
    }

    /// <summary>The address in the QR code, while the server runs.</summary>
    public Uri? Address { get; private set; }

    /// <summary>
    /// The same, short enough to type on a phone without a camera: "192.168.1.12:8080/4821", a 4-digit code that
    /// leads to the session's key (after <see cref="MaxMisses"/> wrong codes in a row, none is taken for a minute).
    /// </summary>
    public string? TypedAddress => Address is { } address ? $"{address.Authority}/{_code}" : null;

    /// <summary>The PC's name on the network, once claimed: "mynotch.local". Not every phone resolves it.</summary>
    public string? HostName { get; private set; }

    /// <summary>The address to type with that name: "mynotch.local/4821" (or "mynotch.local:8080/4821").</summary>
    public string? NamedAddress => HostName is { } name && Address is { } address
        ? $"{name}{(address.IsDefaultPort ? "" : $":{address.Port}")}/{_code}"
        : null;

    /// <summary>On Wi-Fi (rather than a cable): what the phone is told to join.</summary>
    public bool IsWireless { get; private set; }

    public bool IsRunning => _listener is not null;

    public IReadOnlyList<SharedFile> Shared => _shared;

    /// <summary>Everything exchanged in the local chat during this sharing, oldest first.</summary>
    public IReadOnlyList<LocalMessage> Messages => _messagesSnapshot;

    /// <summary>Transfers under way, newest last.</summary>
    public IReadOnlyList<LocalTransfer> Active => _active;

    /// <summary>Files from the phone waiting to be accepted or refused, oldest first (see <see cref="IncomingFile"/>).</summary>
    public IReadOnlyList<IncomingFile> Incoming => _incoming;

    /// <summary>Where files from the phone are saved.</summary>
    public static string ReceiveFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Started, stopped, or the files shared changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>A chat message was added (passed as argument) or updated (<c>null</c>).</summary>
    public event EventHandler<LocalMessage?>? MessagesChanged;

    /// <summary>A transfer began, or moved on (a few times a second at most).</summary>
    public event EventHandler<LocalTransfer>? Progressed;

    public event EventHandler<LocalTransfer>? Completed;

    /// <summary>A transfer stopped before the end (the phone gave up, the network went away…).</summary>
    public event EventHandler<LocalTransfer>? Interrupted;

    /// <summary>A file from the phone now waits for a decision, or got one.</summary>
    public event EventHandler? IncomingChanged;

    /// <summary>
    /// Opens the sharing (the server, if it isn't running yet: same address as before otherwise) with these files and
    /// folders; none, to only receive. Returns why it could not, if it could not.
    /// </summary>
    public async Task<string?> StartAsync(IReadOnlyList<string> paths)
    {
        var added = await Task.Run(() => Describe(paths));
        if (!IsRunning && Listen() is { } refusal)
        {
            return refusal;
        }

        if (added.Length > 0)
        {
            var existing = _shared;
            var merged = existing.ToList();
            foreach (var file in added)
            {
                if (!merged.Any(item => string.Equals(item.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    merged.Add(file);
                }

                bool inlineImage = !file.IsFolder && LocalMessage.IsImageFile(file.Name) && file.Size <= MaxInlineImageBytes;
                PublishMessage(new LocalMessage(
                    RandomNumberGenerator.GetString(KeyAlphabet, 10),
                    fromPc: true,
                    inlineImage ? LocalMessageKind.Image : LocalMessageKind.File,
                    DateTimeOffset.Now)
                {
                    FilePath = file.Path,
                    FileName = file.Name,
                    FileSize = file.Size,
                    IsFolder = file.IsFolder,
                });
            }

            _shared = [.. merged];
        }

        _idle.Start();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Sends a text message or web link from the PC into the local chat.</summary>
    public LocalMessage? SendText(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || Encoding.UTF8.GetByteCount(trimmed) > MaxTextBytes || !IsRunning)
        {
            return null;
        }

        bool isLink = LocalMessage.TryParseLink(trimmed, out var link);
        var message = new LocalMessage(
            RandomNumberGenerator.GetString(KeyAlphabet, 10),
            fromPc: true,
            isLink ? LocalMessageKind.Link : LocalMessageKind.Text,
            DateTimeOffset.Now)
        {
            Text = trimmed,
            Link = isLink ? link : null,
        };
        PublishMessage(message);
        _idle.Start();
        return message;
    }

    /// <summary>Encodes a pasted image as PNG into the session folder and shares it in the local chat.</summary>
    public async Task<LocalMessage?> SendImageAsync(byte[] pixels, int width, int height)
    {
        if (!IsRunning)
        {
            return null;
        }

        string id = RandomNumberGenerator.GetString(KeyAlphabet, 10);
        string name = $"Capture {DateTime.Now:HH.mm.ss}.png";
        Directory.CreateDirectory(SessionFolder);
        string path = Path.Combine(SessionFolder, $"{id}_{name}");
        using (var memory = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync();
            memory.Seek(0);
            await using var file = File.Create(path);
            await memory.AsStreamForRead().CopyToAsync(file);
        }

        long size = new FileInfo(path).Length;
        _shared = [.. _shared, new SharedFile(path, name, size, IsFolder: false)];
        var message = new LocalMessage(id, fromPc: true, LocalMessageKind.Image, DateTimeOffset.Now)
        {
            FilePath = path,
            FileName = name,
            FileSize = size,
            ImageWidth = width,
            ImageHeight = height,
        };
        PublishMessage(message);
        _idle.Start();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return message;
    }

    /// <summary>Saves an image from the chat into Downloads, under a unique name.</summary>
    public static async Task<string?> SaveChatImageAsync(LocalMessage message)
    {
        if (message.FilePath is not { } source || !File.Exists(source))
        {
            return null;
        }

        string name = string.IsNullOrWhiteSpace(message.FileName)
            ? $"Image {message.SentAt.LocalDateTime:yyyy-MM-dd HH.mm.ss}.png"
            : message.FileName;
        return await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(ReceiveFolder);
                string stem = Path.GetFileNameWithoutExtension(name);
                string extension = Path.GetExtension(name);
                if (string.IsNullOrEmpty(extension))
                {
                    extension = ".png";
                }

                for (int n = 1; ; n++)
                {
                    string target = Path.Combine(ReceiveFolder, n == 1 ? stem + extension : $"{stem} ({n}){extension}");
                    if (!File.Exists(target))
                    {
                        File.Copy(source, target, overwrite: false);
                        return target;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Local] Image not saved: {ex.Message}");
                return null;
            }
        });
    }

    /// <summary>Closes the sharing: the server stops, transfers under way are cut.</summary>
    public void Stop()
    {
        if (_listener is null)
        {
            return;
        }

        _session?.Cancel(); // Not disposed: connections still finishing read its token.
        _session = null;
        _listener.Stop();
        _listener = null;
        _mdns?.Dispose(); // Withdraws the name at once.
        _mdns = null;
        HostName = null;
        _idle.Stop();
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _shared = [];
        ClearMessages();
        Address = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(CleanSessionFolder);
    }

    /// <summary>Into Downloads, under its own name (made unique if needed). Returns where, or <c>null</c> if it could not.</summary>
    public string? Accept(IncomingFile file)
    {
        if (file.IsDecided)
        {
            return file.Decision.Result;
        }

        try
        {
            string saved = Keep(file.PartialPath, file.Name);
            Decide(file, saved);
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Local] Not kept: {ex.Message}");
            return null;
        }
    }

    /// <summary>Deleted, as if it never came.</summary>
    public void Refuse(IncomingFile file)
    {
        TryDelete(file.PartialPath);
        Decide(file, null);
    }

    /// <summary>Closing the app: what was never accepted is not kept.</summary>
    public void Dispose()
    {
        Stop();
        foreach (var file in _incoming.ToArray())
        {
            Refuse(file);
        }

        CleanSessionFolder();
    }

    private void Decide(IncomingFile file, string? saved)
    {
        file.Decide(saved);
        _incomingById.TryRemove(file.Id, out _);
        TouchIncomingMessage(file, saved);
        if (_incoming.Remove(file))
        {
            IncomingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PublishMessage(LocalMessage message)
    {
        TaskCompletionSource signal;
        LocalMessage[] snapshot;
        lock (_gate)
        {
            message.Sequence = ++_sequence;
            _messages.Add(message);
            snapshot = [.. _messages];
            signal = _feedSignal;
            _feedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _messagesSnapshot = snapshot;
        signal.TrySetResult();
        _dispatcher.TryEnqueue(() => MessagesChanged?.Invoke(this, message));
    }

    private void TouchIncomingMessage(IncomingFile file, string? saved)
    {
        TaskCompletionSource? signal = null;
        LocalMessage[]? snapshot = null;
        lock (_gate)
        {
            var match = _messages.FirstOrDefault(item => item.Incoming == file);
            if (match is not null)
            {
                if (saved is not null)
                {
                    match.FilePath = saved;
                }

                match.Sequence = ++_sequence;
                snapshot = [.. _messages];
                signal = _feedSignal;
                _feedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (snapshot is not null)
        {
            _messagesSnapshot = snapshot;
            signal?.TrySetResult();
            _dispatcher.TryEnqueue(() => MessagesChanged?.Invoke(this, null));
        }
    }

    private void ClearMessages()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _messages.Clear();
            _sequence = 0;
            signal = _feedSignal;
            _feedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _messagesSnapshot = [];
        signal.TrySetResult();
        MessagesChanged?.Invoke(this, null);
    }

    private static void CleanSessionFolder()
    {
        try
        {
            if (Directory.Exists(SessionFolder))
            {
                foreach (string file in Directory.EnumerateFiles(SessionFolder))
                {
                    if (!file.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(file);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Local] Session folder not cleaned: {ex.Message}");
        }
    }

    private string? Listen()
    {
        if (LocalNetwork.Find() is not { } endpoint)
        {
            return Loc.T("Aucun réseau local");
        }

        // On that interface only (not every adapter: VPN, Hyper-V…): the phone reaches it by its address or by name.
        TcpListener? listener = null;
        foreach (int port in Ports)
        {
            try
            {
                listener = new TcpListener(endpoint.Address, port);
                listener.Start();
                break;
            }
            catch (SocketException ex)
            {
                Debug.WriteLine($"[Local] Port {port} refused: {ex.Message}");
                listener = null;
            }
        }

        if (listener is null)
        {
            return Loc.T("Partage local impossible");
        }

        _listener = listener;
        _address = endpoint.Address;
        _key = RandomNumberGenerator.GetString(KeyAlphabet, 10);
        _code = RandomNumberGenerator.GetInt32(10000).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
        _misses = 0;
        _lockedUntil = 0;
        IsWireless = endpoint.IsWireless;
        Address = new Uri($"http://{endpoint.Address}:{((IPEndPoint)listener.LocalEndpoint).Port}/{_key}/");
        _session = new CancellationTokenSource();
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        _ = AcceptAsync(listener, _session.Token);
        _ = PublishAsync(endpoint.Address, _session);
        return null;
    }

    /// <summary>
    /// Claims "mynotch.local" for this sharing (under a second: the network is asked first). The IP address works
    /// meanwhile, and if mDNS cannot be used here.
    /// </summary>
    private async Task PublishAsync(IPAddress address, CancellationTokenSource session)
    {
        var responder = await MdnsResponder.StartAsync(address, NetworkName, session.Token);
        if (responder is null)
        {
            return;
        }

        if (session != _session)
        {
            responder.Dispose(); // Stopped meanwhile.
            return;
        }

        _mdns = responder;
        HostName = responder.Name;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Off that Wi-Fi (or given another address): the QR code would lead nowhere, the sharing ends.</summary>
    private void OnAddressChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        if (_address is { } address && !LocalNetwork.Has(address))
        {
            Stop();
        }
    });

    private static SharedFile[] Describe(IReadOnlyList<string> paths)
    {
        var shared = new List<SharedFile>();
        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    shared.Add(new SharedFile(path, Path.GetFileName(path), new FileInfo(path).Length, IsFolder: false));
                }
                else if (Directory.Exists(path))
                {
                    long size = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
                    shared.Add(new SharedFile(path, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), size, IsFolder: true));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Local] Not shared: {path}: {ex.Message}");
            }
        }

        return [.. shared];
    }

    // ── Server ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task AcceptAsync(TcpListener listener, CancellationToken session)
    {
        while (!session.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(session);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return; // Stopped.
            }

            if (Interlocked.Increment(ref _connections) > MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                client.Dispose();
                continue;
            }

            _ = ServeAsync(client, session);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken session)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                client.SendTimeout = (int)StallTimeout.TotalMilliseconds; // The zip is written synchronously.
                var stream = client.GetStream();
                if (await ReadRequestAsync(stream, session) is not { } request)
                {
                    return;
                }

                if (!request.Path.EndsWith("/chat/events", StringComparison.Ordinal))
                {
                    _dispatcher.TryEnqueue(() => _idle.Start());
                }

                await RouteAsync(request, stream, session);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The phone went away, or the sharing was closed: nothing to answer to.
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
        }
    }

    private async Task RouteAsync(Request request, NetworkStream stream, CancellationToken session)
    {
        if (request.Method == "OPTIONS")
        {
            await RespondAsync(stream, "204 No Content", "text/plain", "", session);
            return;
        }

        string prefix = $"/{_key}";
        if (!request.Path.StartsWith(prefix, StringComparison.Ordinal))
        {
            await AskCodeAsync(request, stream, session);
            return;
        }

        string route = request.Path[prefix.Length..];
        switch (request.Method, route)
        {
            case ("GET" or "HEAD", ""):
                await RespondAsync(stream, "301 Moved Permanently", "text/plain", "", session, $"Location: {prefix}/\r\n");
                break;

            case ("GET" or "HEAD", "/"):
                long seq;
                LocalMessage[] initial;
                lock (_gate)
                {
                    seq = _sequence;
                    initial = [.. _messages];
                }

                await RespondAsync(stream, "200 OK", "text/html; charset=utf-8", LocalPage.Render(_shared, initial, seq, Address?.AbsoluteUri ?? ""), session);
                break;

            case ("GET" or "HEAD", _) when route.StartsWith("/download", StringComparison.Ordinal):
                string index = route["/download".Length..].TrimStart('/');
                var shared = _shared;
                if (!int.TryParse(index.Length == 0 ? "0" : index, out int i) || i < 0 || i >= shared.Length)
                {
                    await RespondAsync(stream, "404 Not Found", "text/plain", "", session);
                }
                else if (shared[i].IsFolder)
                {
                    await SendFolderAsync(shared[i], request, stream, session);
                }
                else
                {
                    await SendFileAsync(shared[i], request, stream, session);
                }

                break;

            case ("GET" or "HEAD", _) when route.StartsWith("/media/", StringComparison.Ordinal):
                await SendMediaAsync(route["/media/".Length..], request, stream, session);
                break;

            case ("POST", "/chat/text"):
                await ReceiveTextAsync(request, stream, session);
                break;

            case ("POST", "/chat/image"):
                await ReceiveChatImageAsync(request, stream, session);
                break;

            case ("GET", "/chat/events"):
                await AnswerEventsAsync(request.Query, stream, session);
                break;

            case ("POST", "/upload"):
                await ReceiveAsync(request, stream, session);
                break;

            case ("GET", _) when route.StartsWith("/decision/", StringComparison.Ordinal):
                await AnswerDecisionAsync(route["/decision/".Length..], stream, session);
                break;

            default:
                await RespondAsync(stream, "404 Not Found", "text/plain", "", session);
                break;
        }
    }

    private async Task SendMediaAsync(string id, Request request, NetworkStream stream, CancellationToken session)
    {
        var message = _messagesSnapshot.FirstOrDefault(item => item.Id == id);
        if (message?.FilePath is not { } path || (!File.Exists(path) && !Directory.Exists(path)))
        {
            await RespondAsync(stream, "404 Not Found", "text/plain", "", session);
            return;
        }

        string name = message.FileName ?? Path.GetFileName(path);
        if (message.IsFolder)
        {
            await SendFolderAsync(new SharedFile(path, name, message.FileSize, IsFolder: true), request, stream, session);
            return;
        }

        if (message.Kind == LocalMessageKind.Image && !request.Query.Contains("download=1", StringComparison.Ordinal))
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
            long length = input.Length;
            string head = $"HTTP/1.1 200 OK\r\nContent-Type: {LocalMessage.ContentTypeFor(name)}\r\nContent-Length: {length}\r\n{CorsHeaders}"
                + "Cache-Control: private, max-age=3600\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), session);
            if (request.Method != "HEAD" && length > 0)
            {
                await input.CopyToAsync(stream, BufferSize, session);
            }

            return;
        }

        await SendFileAsync(new SharedFile(path, name, message.FileSize, IsFolder: false), request, stream, session);
    }

    /// <summary>A text or web link sent from the other device into the chat.</summary>
    private async Task ReceiveTextAsync(Request request, NetworkStream stream, CancellationToken session)
    {
        if (!request.Headers.TryGetValue("content-length", out string? lengthText)
            || !int.TryParse(lengthText, out int length)
            || length is <= 0 or > MaxTextBytes)
        {
            await RespondAsync(stream, "400 Bad Request", "application/json", "{\"ok\":false}", session);
            return;
        }

        byte[] body = new byte[length];
        int copied = Math.Min(request.Leftover.Length, length);
        if (copied > 0)
        {
            request.Leftover.AsSpan(0, copied).CopyTo(body);
        }

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(session);
        while (copied < length)
        {
            stall.CancelAfter(StallTimeout);
            int read = await stream.ReadAsync(body.AsMemory(copied, length - copied), stall.Token);
            if (read == 0)
            {
                return;
            }

            copied += read;
        }

        string text = Encoding.UTF8.GetString(body).Trim();
        if (text.Length == 0)
        {
            await RespondAsync(stream, "400 Bad Request", "application/json", "{\"ok\":false}", session);
            return;
        }

        bool isLink = LocalMessage.TryParseLink(text, out var link);
        var message = new LocalMessage(
            RandomNumberGenerator.GetString(KeyAlphabet, 10),
            fromPc: false,
            isLink ? LocalMessageKind.Link : LocalMessageKind.Text,
            DateTimeOffset.Now)
        {
            Text = text,
            Link = isLink ? link : null,
        };
        PublishMessage(message);
        await RespondAsync(stream, "200 OK", "application/json", $"{{\"ok\":true,\"id\":\"{message.Id}\"}}", session);
    }

    /// <summary>
    /// An image pasted or picked in the chat on the other device: kept in the session folder and shown straight away
    /// in the chat on both sides (unlike a file for Downloads, it does not ask to accept or refuse first).
    /// </summary>
    private async Task ReceiveChatImageAsync(Request request, NetworkStream stream, CancellationToken session)
    {
        if (!request.Headers.TryGetValue("content-length", out string? lengthText)
            || !long.TryParse(lengthText, out long length)
            || length is <= 0 or > MaxInlineImageBytes)
        {
            await RespondAsync(stream, "400 Bad Request", "application/json", "{\"ok\":false}", session);
            return;
        }

        string rawName = SafeName(request.Headers.GetValueOrDefault("x-file-name"));
        string ext = Path.GetExtension(rawName);
        if (!LocalMessage.IsImageFile(rawName))
        {
            ext = ".png";
            rawName = Path.GetFileNameWithoutExtension(rawName) + ext;
        }

        if (request.Headers.TryGetValue("expect", out string? expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), session);
        }

        string id = RandomNumberGenerator.GetString(KeyAlphabet, 10);
        Directory.CreateDirectory(SessionFolder);
        string path = Path.Combine(SessionFolder, $"{id}_{rawName}");
        var transfer = new LocalTransfer(TransferDirection.FromPhone, rawName, length);
        LocalMessage? message = null;
        bool received = false;
        try
        {
            received = await TrackAsync(transfer, async report =>
            {
                await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);
                long remaining = length;
                if (request.Leftover.Length > 0)
                {
                    int first = (int)Math.Min(request.Leftover.Length, remaining);
                    await output.WriteAsync(request.Leftover.AsMemory(0, first), session);
                    remaining -= first;
                    report(first);
                }

                byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(session);
                    while (remaining > 0)
                    {
                        stall.CancelAfter(StallTimeout);
                        int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), stall.Token);
                        if (read == 0)
                        {
                            throw new IOException("The device stopped sending.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), session);
                        remaining -= read;
                        report(read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }, finish: () =>
            {
                message = new LocalMessage(id, fromPc: false, LocalMessageKind.Image, DateTimeOffset.Now)
                {
                    FilePath = path,
                    FileName = rawName,
                    FileSize = length,
                };
                PublishMessage(message);
            });
        }
        finally
        {
            if (!received)
            {
                TryDelete(path);
            }
        }

        string json = received ? $"{{\"ok\":true,\"id\":\"{message!.Id}\"}}" : "{\"ok\":false}";
        await RespondAsync(stream, received ? "200 OK" : "500 Internal Server Error", "application/json", json, session);
    }

    /// <summary>
    /// Long-polling feed for the web page: answers at once when there are messages newer than <c>since</c>, or waits
    /// up to <see cref="DecisionWait"/> for one to arrive.
    /// </summary>
    private async Task AnswerEventsAsync(string query, NetworkStream stream, CancellationToken session)
    {
        long since = 0;
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("since=", StringComparison.Ordinal) && long.TryParse(pair[6..], out long parsed))
            {
                since = parsed;
            }
        }

        Task wait;
        lock (_gate)
        {
            wait = _sequence > since ? Task.CompletedTask : _feedSignal.Task;
        }

        if (!wait.IsCompleted)
        {
            await Task.WhenAny(wait, Task.Delay(DecisionWait, session));
            session.ThrowIfCancellationRequested();
        }

        long currentSeq;
        LocalMessage[] changed;
        lock (_gate)
        {
            currentSeq = _sequence;
            changed = _messages.Where(item => item.Sequence > since).ToArray();
        }

        await RespondAsync(stream, "200 OK", "application/json", LocalPage.EventsJson(currentSeq, changed), session);
    }

    /// <summary>
    /// Without the key: the code, typed after the address ("/4821") or on the page asking for it ("/?code=4821"),
    /// leads to the session; anything else gets that page.
    /// </summary>
    private async Task AskCodeAsync(Request request, NetworkStream stream, CancellationToken session)
    {
        if (request.Path == "/favicon.ico")
        {
            await RespondAsync(stream, "404 Not Found", "text/plain", "", session);
            return;
        }

        string segment = request.Path.Trim('/');
        string? attempt = segment.Length > 0 && segment.All(char.IsAsciiDigit) ? segment
            : request.Query.Split('&').Select(pair => pair.Split('=', 2)).FirstOrDefault(pair => pair[0] == "code") is [_, var value] ? value
            : null;
        string? message = segment.Length > 0 && attempt is null ? Loc.T("Ce lien n'est plus valable : entrez le code affiché par la notch.") : null;
        if (attempt is not null)
        {
            switch (Check(attempt))
            {
                case true:
                    await RespondAsync(stream, "302 Found", "text/plain", "", session, $"Location: /{_key}/\r\n");
                    return;
                case false:
                    message = Loc.T("Code incorrect.");
                    break;
                case null:
                    message = Loc.T("Trop d'essais : patientez une minute.");
                    break;
            }
        }

        await RespondAsync(stream, "200 OK", "text/html; charset=utf-8", LocalPage.Code(message), session);
    }

    /// <summary>Right, wrong, or <c>null</c> while locked after too many wrong codes (guessing 4 digits takes a day).</summary>
    private bool? Check(string attempt)
    {
        lock (_gate)
        {
            if (Stopwatch.GetTimestamp() < _lockedUntil)
            {
                return null;
            }

            if (attempt == _code)
            {
                _misses = 0;
                return true;
            }

            if (++_misses >= MaxMisses)
            {
                _misses = 0;
                _lockedUntil = Stopwatch.GetTimestamp() + (long)(Lockout.TotalSeconds * Stopwatch.Frequency);
            }

            return false;
        }
    }

    // ── PC → phone ──────────────────────────────────────────────────────────────────────────────────────────

    private async Task SendFileAsync(SharedFile file, Request request, NetworkStream stream, CancellationToken session)
    {
        await using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
        long length = input.Length;

        // A download resumed by the phone (its download manager asks for the rest only).
        long start = 0;
        long end = length - 1;
        bool partial = request.Headers.TryGetValue("range", out string? range) && TryParseRange(range, length, out start, out end);
        if (range is not null && !partial)
        {
            await RespondAsync(stream, "416 Range Not Satisfiable", "text/plain", "", session, $"Content-Range: bytes */{length}\r\n");
            return;
        }

        long count = Math.Max(0, end - start + 1);
        string head = $"HTTP/1.1 {(partial ? "206 Partial Content" : "200 OK")}\r\n"
            + $"Content-Type: {LocalMessage.ContentTypeFor(file.Name)}\r\nContent-Length: {count}\r\n{CorsHeaders}"
            + (partial ? $"Content-Range: bytes {start}-{end}/{length}\r\n" : "")
            + $"Content-Disposition: {Disposition(file.Name)}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), session);
        if (request.Method == "HEAD" || count == 0)
        {
            return;
        }

        input.Position = start;
        var transfer = new LocalTransfer(TransferDirection.ToPhone, file.Name, count);
        await TrackAsync(transfer, async report =>
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(session);
                long remaining = count;
                while (remaining > 0)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), session);
                    if (read == 0)
                    {
                        throw new IOException("The file got shorter.");
                    }

                    stall.CancelAfter(StallTimeout);
                    await stream.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                    remaining -= read;
                    report(read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        });
    }

    /// <summary>
    /// A folder, as a zip written straight into the response: stored (a local network is faster than compressing),
    /// so its size is known in advance only roughly; the connection's end marks the end of the file.
    /// </summary>
    private async Task SendFolderAsync(SharedFile folder, Request request, NetworkStream stream, CancellationToken session)
    {
        string name = folder.Name + ".zip";
        string head = $"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Disposition: {Disposition(name)}\r\n"
            + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), session);
        if (request.Method == "HEAD")
        {
            return;
        }

        var transfer = new LocalTransfer(TransferDirection.ToPhone, name, folder.Size);
        await TrackAsync(transfer, report => Task.Run(() =>
        {
            string root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder.Path)) ?? folder.Path;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories))
                {
                    session.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.NoCompression);
                    entry.LastWriteTime = File.GetLastWriteTime(file);
                    using var input = File.OpenRead(file);
                    using var output = entry.Open();
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        session.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        report(read);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }, session));
    }

    // ── Phone → PC ──────────────────────────────────────────────────────────────────────────────────────────

    private async Task ReceiveAsync(Request request, NetworkStream stream, CancellationToken session)
    {
        if (!request.Headers.TryGetValue("content-length", out string? lengthText) || !long.TryParse(lengthText, out long length) || length < 0)
        {
            await RespondAsync(stream, "411 Length Required", "text/plain", "", session);
            return;
        }

        string name = SafeName(request.Headers.GetValueOrDefault("x-file-name"));
        if (request.Headers.TryGetValue("expect", out string? expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), session);
        }

        Directory.CreateDirectory(SessionFolder);
        string partial = Path.Combine(SessionFolder, $"incoming_{Guid.NewGuid():N}.part");
        var transfer = new LocalTransfer(TransferDirection.FromPhone, name, length);
        IncomingFile? incoming = null;
        bool received = false;
        try
        {
            received = await TrackAsync(transfer, async report =>
            {
                await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);
                long remaining = length;
                if (request.Leftover.Length > 0)
                {
                    int first = (int)Math.Min(request.Leftover.Length, remaining);
                    await output.WriteAsync(request.Leftover.AsMemory(0, first), session);
                    remaining -= first;
                    report(first);
                }

                byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(session);
                    while (remaining > 0)
                    {
                        stall.CancelAfter(StallTimeout);
                        int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), stall.Token);
                        if (read == 0)
                        {
                            throw new IOException("The phone stopped sending.");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), session);
                        remaining -= read;
                        report(read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }, finish: () => incoming = Hold(partial, name, length));
        }
        finally
        {
            if (!received)
            {
                TryDelete(partial);
            }
        }

        // The page then asks what the user decides (decision/{id}).
        string json = received ? $"{{\"ok\":true,\"id\":\"{incoming!.Id}\"}}" : "{\"ok\":false}";
        await RespondAsync(stream, received ? "200 OK" : "500 Internal Server Error", "application/json", json, session);
    }

    /// <summary>A complete file from the phone: it waits, hidden, for the user to accept or refuse it.</summary>
    private IncomingFile Hold(string partial, string name, long size)
    {
        var file = new IncomingFile(RandomNumberGenerator.GetString(KeyAlphabet, 12), name, size, partial);
        _incomingById[file.Id] = file;
        PublishMessage(new LocalMessage(file.Id, fromPc: false, LocalMessageKind.File, DateTimeOffset.Now)
        {
            FileName = name,
            FileSize = size,
            Incoming = file,
        });
        _dispatcher.TryEnqueue(() =>
        {
            if (!file.IsDecided)
            {
                _incoming.Add(file);
                IncomingChanged?.Invoke(this, EventArgs.Empty);
            }
        });
        return file;
    }

    /// <summary>
    /// What became of a file the phone sent: answered as soon as the user decides, or after a while with "pending"
    /// (the page asks again), so no connection hangs for long.
    /// </summary>
    private async Task AnswerDecisionAsync(string id, NetworkStream stream, CancellationToken session)
    {
        if (!_incomingById.TryGetValue(id, out var file))
        {
            await RespondAsync(stream, "404 Not Found", "application/json", "{\"state\":\"unknown\"}", session);
            return;
        }

        await Task.WhenAny(file.Decision, Task.Delay(DecisionWait, session));
        session.ThrowIfCancellationRequested();
        string json = !file.Decision.IsCompleted ? "{\"state\":\"pending\"}"
            : file.Decision.Result is { } saved ? $"{{\"state\":\"accepted\",\"name\":\"{JavaScriptEncode(Path.GetFileName(saved))}\"}}"
            : "{\"state\":\"refused\"}";
        await RespondAsync(stream, "200 OK", "application/json", json, session, "Cache-Control: no-store\r\n");
    }

    /// <summary>The received file under its own name, made unique ("photo (2).jpg") if there is one already.</summary>
    private static string Keep(string partial, string name)
    {
        Directory.CreateDirectory(ReceiveFolder);
        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        for (int n = 1; n <= 1000; n++)
        {
            string target = Path.Combine(ReceiveFolder, n == 1 ? name : $"{stem} ({n}){extension}");
            if (File.Exists(target) || Directory.Exists(target))
            {
                continue;
            }

            try
            {
                File.Move(partial, target);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // If Downloads is on another volume or Windows Defender holds a read handle on the temp file,
                // copying with FileShare.ReadWrite succeeds where File.Move cannot.
                using (var source = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize))
                using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read, BufferSize))
                {
                    source.CopyTo(destination, BufferSize);
                }

                TryDelete(partial);
                return target;
            }
        }

        throw new IOException("No free file name in Downloads.");
    }

    private static string SafeName(string? encoded)
    {
        string name;
        try
        {
            name = Path.GetFileName(Uri.UnescapeDataString(encoded ?? ""));
        }
        catch (UriFormatException)
        {
            name = "";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? $"{Loc.T("Fichier du téléphone")} {DateTime.Now:yyyy-MM-dd HH.mm.ss}" : name[..Math.Min(name.Length, 180)];
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Local] Not deleted: {ex.Message}");
        }
    }

    // ── Progress ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a transfer and keeps the UI told: begun, its progress a few times a second, then complete (after
    /// <paramref name="finish"/>) or interrupted. Returns whether it completed.
    /// </summary>
    private async Task<bool> TrackAsync(LocalTransfer transfer, Func<Action<int>, Task> run, Action? finish = null)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _active.Add(transfer);
            Progressed?.Invoke(this, transfer);
        });

        long reportedAt = Stopwatch.GetTimestamp();
        int pending = 0;
        void Report(int bytes)
        {
            transfer.Add(bytes);
            if (Stopwatch.GetElapsedTime(reportedAt) >= ReportInterval && Interlocked.Exchange(ref pending, 1) == 0)
            {
                reportedAt = Stopwatch.GetTimestamp();
                _dispatcher.TryEnqueue(() =>
                {
                    Volatile.Write(ref pending, 0);
                    transfer.Sample();
                    Progressed?.Invoke(this, transfer);
                });
            }
        }

        bool done = false;
        try
        {
            await run(Report);
            finish?.Invoke();
            done = true;
            return true;
        }
        finally
        {
            _dispatcher.TryEnqueue(() =>
            {
                _active.Remove(transfer);
                _idle.Start();
                (done ? Completed : Interrupted)?.Invoke(this, transfer);
            });
        }
    }

    // ── HTTP ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <param name="Leftover">What came after the head in the same read: the start of the body.</param>
    private sealed record Request(string Method, string Path, string Query, Dictionary<string, string> Headers, byte[] Leftover);

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken session)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session);
        timeout.CancelAfter(HeadTimeout);
        byte[] buffer = new byte[MaxHeadSize];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                return null;
            }

            int read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
            // A browser trying https:// first (Chrome does, for an address typed by hand): cut at once, so it falls
            // back to http:// right away instead of waiting for the head that never comes.
            if (read == 0 || (length == 0 && buffer[0] == TlsHandshake))
            {
                return null;
            }

            length += read;
            int end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end < 0)
            {
                continue;
            }

            string[] lines = Encoding.Latin1.GetString(buffer, 0, end).Split("\r\n");
            string[] start = lines[0].Split(' ');
            if (start.Length < 3)
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            string path = start[1];
            int query = path.IndexOf('?');
            return new Request(
                start[0].ToUpperInvariant(),
                query >= 0 ? path[..query] : path,
                query >= 0 ? path[(query + 1)..] : "",
                headers,
                buffer[(end + 4)..length]);
        }
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string type, string body, CancellationToken session, string extra = "")
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {content.Length}\r\n{CorsHeaders}{extra}"
            + "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), session);
        await stream.WriteAsync(content, session);
    }

    /// <summary>"bytes=500-" or "bytes=500-999", within the file.</summary>
    private static bool TryParseRange(string range, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (!range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || range.Contains(','))
        {
            return false;
        }

        string[] bounds = range[6..].Split('-');
        if (bounds.Length != 2 || !long.TryParse(bounds[0], out start) || start >= length)
        {
            return false;
        }

        if (bounds[1].Length > 0 && (!long.TryParse(bounds[1], out end) || end < start))
        {
            return false;
        }

        end = Math.Min(end, length - 1);
        return true;
    }

    /// <summary>The file name for the download, accents included (RFC 6266), with a plain fallback.</summary>
    private static string Disposition(string name)
    {
        string plain = new(name.Select(c => c is >= ' ' and < '\x7f' and not '"' and not '\\' ? c : '_').ToArray());
        return $"attachment; filename=\"{plain}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    private static string JavaScriptEncode(string text) => System.Text.Json.JsonEncodedText.Encode(text).ToString();
}
