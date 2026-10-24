using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.UI.Dispatching;
using Ilot.Modules.DropZone.Hosts;
using Ilot.Settings;
using Ilot.Localization;

namespace Ilot.Modules.DropZone;

/// <summary>
/// Sends files to the host chosen in the settings (Send, encrypted end to end, or Gofile), one at a time, and gets a
/// link for each.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Nothing runs at rest: the queue is worked through when something is added, progress is sampled a few times
/// per second only while sending, and the network is watched through Windows' events.</item>
/// <item>A dropped connection never surfaces as a crash: the upload pauses ("attente du réseau"), then starts again
/// as soon as Windows reports the network back, or after a growing delay (2 s, 4 s… up to a minute) otherwise. A
/// transfer that stalls is treated the same way. Hosts cannot resume a transfer: each attempt starts over.</item>
/// <item>When the host itself fails (5xx, 429), the reason is shown and a few spaced tries are made, then the upload
/// fails (it can be retried by hand): hammering the service gets the computer blocked by its anti-abuse.</item>
/// <item>The queue is kept on disk (<see cref="UploadQueueStore"/>): what was not sent is sent after a restart.</item>
/// </list>
/// All state is changed on the UI thread; the sending thread only publishes a byte count.
/// </remarks>
public sealed class FileUploadService : IDisposable
{
    /// <summary>Finished uploads kept in the list (with their link) besides the ongoing ones.</summary>
    private const int KeptDone = 20;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(2);

    // Nothing through for this long: "en attente…" on the tile. The host is given much longer (its storage may
    // be catching up, the public Send often pauses) before the attempt is dropped and started again.
    private static readonly TimeSpan StalledAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(60);

    // When the host itself fails (5xx, 429): tries in all, spaced 2, 4, 8 s, before handing over or giving up.
    private const int MaxServerAttempts = 4;

    private readonly DispatcherQueue _dispatcher;
    private readonly AppSettings _settings;
    private readonly UploadQueueStore _store;
    private readonly ObservableCollection<UploadItem> _items = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _cancellations = [];
    private readonly DispatcherQueueTimer _sampler;
    private TaskCompletionSource _networkBack = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _attempt;
    private UploadItem? _current;
    private long _sentBytes;
    private readonly Queue<(long At, long Bytes)> _samples = [];
    private long _movedAt;
    private bool _running;

    public FileUploadService(DispatcherQueue dispatcher, AppSettings settings, UploadQueueStore store)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _store = store;
        Items = new ReadOnlyObservableCollection<UploadItem>(_items);
        foreach (var item in store.Load().OrderByDescending(item => item.CreatedAt))
        {
            _items.Add(item);
        }

        _sampler = dispatcher.CreateTimer();
        _sampler.Interval = SampleInterval;
        _sampler.Tick += (_, _) => Sample();

        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        RunQueue();
    }

    /// <summary>Newest first: ongoing uploads, then the links still valid.</summary>
    public ReadOnlyObservableCollection<UploadItem> Items { get; }

    /// <summary>An upload went through: its link is ready.</summary>
    public event EventHandler<UploadItem>? Completed;

    /// <summary>An upload gave up for good (refused by the host, file gone…).</summary>
    public event EventHandler<UploadItem>? Failed;

    /// <summary>
    /// Takes dropped files and folders: one file is sent as it is, several (or a folder) are zipped first. Checks
    /// what it can at once and returns why it refused, if it did; zipping and sending go on in the background.
    /// </summary>
    public async Task<string?> EnqueueAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return null;
        }

        bool single = paths.Count == 1 && File.Exists(paths[0]);
        var (total, files) = await Task.Run(() => Measure(paths));
        if (files == 0)
        {
            return Loc.T("Rien à envoyer");
        }

        if (ChooseHost(total) is not { } host)
        {
            return TooBig(single);
        }

        var id = Guid.NewGuid();
        var now = DateTimeOffset.Now;
        if (single)
        {
            Add(new UploadItem(id, Path.GetFileName(paths[0]), paths[0], total, isArchive: false, isStaged: false, host.Id, _settings.UploadExpiry, now)
            {
                State = UploadState.Queued,
            });
            RunQueue();
            return null;
        }

        string name = ArchiveName(paths);
        var archive = new UploadItem(id, name, UploadQueueStore.StagedPath(id, name), total, isArchive: true, isStaged: true, host.Id, _settings.UploadExpiry, now)
        {
            State = UploadState.Preparing,
        };
        Add(archive);
        _ = ZipThenQueueAsync(archive, paths, total);
        return null;
    }

    /// <summary>Stops an upload (or its zipping) and forgets it; a finished one is just removed from the list.</summary>
    public void Remove(UploadItem item)
    {
        if (_cancellations.Remove(item.Id, out var cancellation))
        {
            cancellation.Cancel();
        }

        if (_items.Remove(item))
        {
            UploadQueueStore.DeleteStaged(item);
            Save();
        }
    }

    /// <summary>Tries a failed upload again.</summary>
    public void Retry(UploadItem item)
    {
        if (item.State != UploadState.Failed)
        {
            return;
        }

        if (!File.Exists(item.FilePath))
        {
            item.Error = Loc.T("Fichier introuvable");
            return;
        }

        item.Error = null;
        item.Progress = 0;
        item.State = UploadState.Queued;
        Save();
        RunQueue();
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _sampler.Stop();
        foreach (var cancellation in _cancellations.Values)
        {
            cancellation.Cancel();
        }
    }

    // ── Preparing ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The largest file any host takes.</summary>
    private static long LargestSize => UploadHosts.All.Max(host => host.MaxSize);

    /// <summary>
    /// The host chosen in the settings, if it takes a file of this size; <c>null</c> otherwise. A file never goes
    /// elsewhere on its own: one meant to be encrypted (Send) must not end up readable on another host.
    /// </summary>
    private IUploadHost? ChooseHost(long size)
    {
        var chosen = UploadHosts.Get(_settings.UploadHost);
        return size <= chosen.MaxSize ? chosen : null;
    }

    /// <summary>The largest file the chosen host accepts, as shown to the user.</summary>
    private string Reach => UploadHosts.Get(_settings.UploadHost).MaxSizeText;

    private string TooBig(bool single) =>
        single ? Loc.F("Fichier > {0} non supporté", Reach) : Loc.F("Fichiers > {0} non supportés", Reach);

    private static (long Total, int Files) Measure(IReadOnlyList<string> paths)
    {
        long total = 0;
        int files = 0;
        foreach (string file in EnumerateFiles(paths).Select(entry => entry.File))
        {
            total += new FileInfo(file).Length;
            files++;
            if (total > LargestSize)
            {
                break; // No host takes it anyway: no need to look further.
            }
        }

        return (total, files);
    }

    /// <summary>Every file under the dropped items, with its path inside the archive (folders keep their name).</summary>
    private static IEnumerable<(string File, string Entry)> EnumerateFiles(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                yield return (path, Path.GetFileName(path));
            }
            else if (Directory.Exists(path))
            {
                string root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path;
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    yield return (file, Path.GetRelativePath(root, file).Replace('\\', '/'));
                }
            }
        }
    }

    private static string ArchiveName(IReadOnlyList<string> paths)
    {
        string first = Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]));
        return paths.Count == 1 ? $"{first}.zip" : $"{Path.GetFileNameWithoutExtension(first)} et {paths.Count - 1} autre{(paths.Count > 2 ? "s" : "")}.zip";
    }

    private async Task ZipThenQueueAsync(UploadItem item, IReadOnlyList<string> paths, long total)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellations[item.Id] = cancellation;
        long done = 0;
        var token = cancellation.Token;
        try
        {
            // Already compressed formats (photos, videos…) gain nothing from more effort: fastest is best.
            var zipping = Task.Run(() =>
            {
                using var archive = ZipFile.Open(item.FilePath, ZipArchiveMode.Create);
                foreach (var (file, entryName) in EnumerateFiles(paths))
                {
                    token.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                    entry.LastWriteTime = File.GetLastWriteTime(file);
                    using var input = File.OpenRead(file);
                    using var output = entry.Open();
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        Interlocked.Add(ref done, read);
                    }
                }
            }, token);

            // The zipping progress, a few times per second until it is done.
            while (await Task.WhenAny(zipping, Task.Delay(SampleInterval * 2)) != zipping)
            {
                item.Progress = Math.Clamp((double)Interlocked.Read(ref done) / Math.Max(total, 1), 0, 1);
            }

            await zipping;
            long size = new FileInfo(item.FilePath).Length;
            item.SetSize(size);
            if (ChooseHost(size) is null)
            {
                Fail(item, Loc.F("Archive > {0} non supportée", Reach));
                UploadQueueStore.DeleteStaged(item);
                return;
            }

            item.Progress = 0;
            item.State = UploadState.Queued;
            Save();
            RunQueue();
        }
        catch (OperationCanceledException)
        {
            UploadQueueStore.DeleteStaged(item); // Removed by the user while zipping.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Debug.WriteLine($"[Uploads] Not zipped: {ex}");
            UploadQueueStore.DeleteStaged(item);
            Fail(item, Loc.T("Compression impossible"));
        }
        finally
        {
            _cancellations.Remove(item.Id);
        }
    }

    // ── Sending ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Works through the queue, oldest first, one upload at a time; idle once it is empty.</summary>
    private async void RunQueue()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        try
        {
            while (_items.LastOrDefault(item => item.State == UploadState.Queued) is { } next)
            {
                await SendWithRetriesAsync(next);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Uploads] Queue stopped: {ex}");
        }
        finally
        {
            _running = false;
        }
    }

    private async Task SendWithRetriesAsync(UploadItem item)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellations[item.Id] = cancellation;
        try
        {
            int serverFailures = 0;
            for (int attempt = 0; ; attempt++)
            {
                if (attempt > 0 || !NetworkInterface.GetIsNetworkAvailable())
                {
                    if (attempt == 0)
                    {
                        item.PauseReason = null; // Offline from the start: waiting for the network.
                    }

                    item.State = UploadState.WaitingForNetwork;
                    item.Progress = 0;
                    item.BytesPerSecond = 0;
                    Save();
                    await WaitForNetworkAsync(attempt, cancellation.Token);
                }

                try
                {
                    string url = await SendAsync(item, cancellation.Token);
                    item.Url = url;
                    item.UploadedAt = DateTimeOffset.Now;
                    item.Progress = 1;
                    item.State = UploadState.Done;
                    UploadQueueStore.DeleteStaged(item);
                    TrimDone();
                    Save();
                    Completed?.Invoke(this, item);
                    return;
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested && Classify(ex) is { } trouble)
                {
                    Debug.WriteLine($"[Uploads] Interrupted ({trouble}), will retry: {ex.Message}");
                    if (!trouble.FromServer)
                    {
                        item.PauseReason = null;
                    }
                    else if (++serverFailures < MaxServerAttempts)
                    {
                        item.PauseReason = Loc.F("{0} {1} – nouvel essai…", item.HostName, trouble.Reason);
                    }
                    else
                    {
                        // Up but failing for good: never hammer a host (its anti-abuse would block this computer).
                        throw new UploadRejectedException(Loc.F("{0} {1} – réessayez plus tard", item.HostName, trouble.Reason));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Removed by the user: nothing to report.
        }
        catch (UploadRejectedException ex)
        {
            Fail(item, ex.Message);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Fail(item, Loc.T("Fichier introuvable"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Fail(item, Loc.T("Fichier illisible"));
        }
        finally
        {
            _cancellations.Remove(item.Id);
            StopSampling();
        }
    }

    /// <summary>One attempt: the whole file, then the link the host answers with.</summary>
    private async Task<string> SendAsync(UploadItem item, CancellationToken cancellation)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _attempt = attempt;
        try
        {
            var host = UploadHosts.Get(item.Host);
            using var file = new FileStream(item.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            long length = file.Length;
            item.SetSize(length);
            item.Progress = 0;
            item.State = UploadState.Uploading;
            StartSampling(item);
            attempt.CancelAfter(StallTimeout);

            return await host.UploadAsync(item, file, sent =>
            {
                Interlocked.Exchange(ref _sentBytes, sent);

                // A transfer that stops moving is a lost connection; once all is sent, the host gets time to answer.
                try
                {
                    attempt.CancelAfter(sent >= length ? ResponseTimeout : StallTimeout);
                }
                catch (ObjectDisposedException)
                {
                    // The attempt is over.
                }
            }, attempt.Token);
        }
        catch (Exception ex) when (attempt.IsCancellationRequested && !cancellation.IsCancellationRequested
            && (ex is OperationCanceledException or IOException or System.Net.WebSockets.WebSocketException)
            && NetworkInterface.GetIsNetworkAvailable())
        {
            // Dropped by the stall timer while online: the host stopped taking the file, the network is not to blame.
            throw new TimeoutException("The host stopped taking the file.", ex);
        }
        finally
        {
            _attempt = null;
            StopSampling();
        }
    }

    /// <summary>
    /// Waits until the network is back (Windows tells), or for a growing delay, whichever comes first: Windows does not
    /// always notice when only the internet is gone.
    /// </summary>
    private async Task WaitForNetworkAsync(int attempt, CancellationToken cancellation)
    {
        var delay = TimeSpan.FromTicks(Math.Min(FirstRetry.Ticks << Math.Min(Math.Max(attempt - 1, 0), 6), LongestRetry.Ticks));
        await Task.WhenAny(Task.Delay(delay, cancellation), _networkBack.Task);
        cancellation.ThrowIfCancellationRequested();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        if (e.IsAvailable)
        {
            // Wake whoever waits, and arm for the next time.
            var back = _networkBack;
            _networkBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            back.TrySetResult();
        }
        else
        {
            // No need to wait for the transfer to stall: pause right away.
            _attempt?.Cancel();
        }
    });

    /// <summary>
    /// What went wrong, if it is worth another try: the connection (waited for, however long it takes), or the host
    /// itself (busy, failing: a few tries only). <c>null</c> when retrying cannot help.
    /// </summary>
    private static Trouble? Classify(Exception ex) => ex switch
    {
        UploadRejectedException or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException => null,
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => new Trouble(true, Loc.T("saturé")),
        HttpRequestException { StatusCode: { } status } when status == HttpStatusCode.RequestTimeout || (int)status >= 500
            => new Trouble(true, Loc.F("indisponible ({0})", (int)status)),
        HttpRequestException { StatusCode: null } => Trouble.Network,
        TimeoutException => new Trouble(true, Loc.T("ne répond plus")),
        OperationCanceledException or IOException or SocketException => Trouble.Network, // Cancelled here: stalled or offline.
        _ => null,
    };

    /// <summary>Why an upload is paused.</summary>
    private sealed record Trouble(bool FromServer, string Reason)
    {
        public static Trouble Network { get; } = new(false, Loc.T("réseau"));
    }

    // ── Progress ────────────────────────────────────────────────────────────────────────────────────────────

    private void StartSampling(UploadItem item)
    {
        _current = item;
        Interlocked.Exchange(ref _sentBytes, 0);
        _samples.Clear();
        _samples.Enqueue((Stopwatch.GetTimestamp(), 0));
        _movedAt = Stopwatch.GetTimestamp();
        item.BytesPerSecond = 0;
        item.IsStalled = false;
        _sampler.Start();
    }

    private void StopSampling()
    {
        _sampler.Stop();
        if (_current is { } item)
        {
            item.IsStalled = false;
        }

        _current = null;
    }

    /// <summary>
    /// A few times per second while sending: the fraction sent, the speed over the last seconds (what really went
    /// through, not a value fading out slowly when nothing does), and whether the sending is stuck.
    /// </summary>
    private void Sample()
    {
        if (_current is not { } item)
        {
            return;
        }

        long sent = Interlocked.Read(ref _sentBytes);
        long now = Stopwatch.GetTimestamp();
        if (sent != _samples.Last().Bytes)
        {
            _movedAt = now;
        }

        _samples.Enqueue((now, sent));
        while (_samples.Count > 2 && Stopwatch.GetElapsedTime(_samples.Peek().At, now) > SpeedWindow)
        {
            _samples.Dequeue();
        }

        var (oldestAt, oldestBytes) = _samples.Peek();
        double seconds = Stopwatch.GetElapsedTime(oldestAt, now).TotalSeconds;
        item.BytesPerSecond = seconds > 0 ? (sent - oldestBytes) / seconds : 0;
        item.IsStalled = sent < item.Size && Stopwatch.GetElapsedTime(_movedAt, now) >= StalledAfter;
        item.Progress = item.Size > 0 ? Math.Clamp((double)sent / item.Size, 0, 1) : 0;
    }

    // ── List ────────────────────────────────────────────────────────────────────────────────────────────────

    private void Add(UploadItem item)
    {
        _items.Insert(0, item);
        Save();
    }

    private void Fail(UploadItem item, string reason)
    {
        item.Error = reason;
        item.BytesPerSecond = 0;
        item.State = UploadState.Failed;
        Save();
        Failed?.Invoke(this, item);
    }

    private void TrimDone()
    {
        var done = _items.Where(item => item.State == UploadState.Done).Skip(KeptDone).ToList();
        foreach (var item in done)
        {
            _items.Remove(item);
        }
    }

    private void Save() => _store.ScheduleSave(() => _items.ToList());

}
