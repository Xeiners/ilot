using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Ilot.Modules.DropZone.Hosts;
using Ilot.Localization;

namespace Ilot.Modules.DropZone;

public enum UploadState
{
    /// <summary>Several files or a folder being zipped.</summary>
    Preparing,

    /// <summary>Waiting for its turn: one upload at a time.</summary>
    Queued,

    Uploading,

    /// <summary>The connection dropped: retried as soon as the network is back (and from time to time anyway).</summary>
    WaitingForNetwork,

    Done,

    Failed,
}

/// <summary>A file on its way to a host, or already there (with its link, until it expires).</summary>
public sealed class UploadItem : ObservableObject
{

    private UploadState _state;
    private double _progress;
    private double _bytesPerSecond;
    private bool _isStalled;
    private string? _url;
    private string? _error;
    private DateTimeOffset? _uploadedAt;
    private string? _pauseReason;

    public UploadItem(
        Guid id, string name, string filePath, long size, bool isArchive, bool isStaged, string host, string expiry, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        FilePath = filePath;
        Size = size;
        IsArchive = isArchive;
        IsStaged = isStaged;
        Expiry = expiry;
        Host = host;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    /// <summary>Name the file gets on the host.</summary>
    public string Name { get; }

    /// <summary>The file sent: the original, or a copy / an archive kept in the queue folder.</summary>
    public string FilePath { get; }

    public long Size { get; private set; }

    /// <summary>A ZIP made from several files or a folder.</summary>
    public bool IsArchive { get; }

    /// <summary>The file belongs to the queue folder (an archive, a copy): deleted once sent or abandoned.</summary>
    public bool IsStaged { get; }

    /// <summary>How long the host keeps it, when it lets us choose: "1h", "12h", "24h" or "72h".</summary>
    public string Expiry { get; }

    /// <summary>The host it goes to (<see cref="IUploadHost.Id"/>).</summary>
    public string Host { get; }

    public string HostName => UploadHosts.Get(Host).Name;

    public DateTimeOffset CreatedAt { get; }

    public UploadState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>0 to 1: zipping while preparing, sending while uploading.</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            if (SetProperty(ref _progress, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        set
        {
            if (SetProperty(ref _bytesPerSecond, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>Sending, but nothing has gone through for a few seconds: the host is not taking any more for now.</summary>
    public bool IsStalled
    {
        get => _isStalled;
        set
        {
            if (SetProperty(ref _isStalled, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string? Url
    {
        get => _url;
        set
        {
            if (SetProperty(ref _url, value))
            {
                OnPropertyChanged(nameof(DisplayUrl));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>Why a paused upload waits, when it is not the network: "Send indisponible (500) – nouvel essai…".</summary>
    public string? PauseReason
    {
        get => _pauseReason;
        set
        {
            if (SetProperty(ref _pauseReason, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public DateTimeOffset? UploadedAt
    {
        get => _uploadedAt;
        set => SetProperty(ref _uploadedAt, value);
    }

    /// <summary>Still being worked on: zipping, waiting, sending or paused.</summary>
    public bool IsActive => State is UploadState.Preparing or UploadState.Queued or UploadState.Uploading or UploadState.WaitingForNetwork;

    public DateTimeOffset? ExpiresAt => UploadedAt + ExpiryDuration;

    /// <summary>How long the host keeps the file, when it says (Send); <c>null</c> when it decides by itself.</summary>
    public TimeSpan? ExpiryDuration => UploadHosts.Get(Host).HasExpiry
        ? TimeSpan.FromHours(int.Parse(Expiry.TrimEnd('h'), CultureInfo.InvariantCulture))
        : null;

    /// <summary>The link without its scheme, as shown on the tile.</summary>
    public string DisplayUrl => Url is null ? "" : Url.Replace("https://", "", StringComparison.OrdinalIgnoreCase);

    /// <summary>What is happening, in a few words: "45 % • 3,2 Mo/s", "En pause – attente du réseau…"…</summary>
    public string StatusText => State switch
    {
        UploadState.Preparing => Loc.F("Compression… {0:0} %", Progress * 100),
        UploadState.Queued => Loc.T("En attente"),
        UploadState.Uploading when IsStalled => Loc.F("{0:0} % • en attente…", Progress * 100),
        UploadState.Uploading when BytesPerSecond >= 1024 => $"{Progress * 100:0} % • {FormatSize(BytesPerSecond)}/s",
        UploadState.Uploading => $"{Progress * 100:0} %",
        UploadState.WaitingForNetwork => PauseReason ?? Loc.T("En pause – attente du réseau…"),
        UploadState.Failed => Error ?? Loc.T("Échec de l'envoi"),
        _ => DisplayUrl,
    };

    /// <summary>How long the link still works, the way a person would say it.</summary>
    public string ExpiryTextAt(DateTimeOffset now)
    {
        if (ExpiryDuration is null)
        {
            return Loc.F("via {0}", HostName);
        }

        if (ExpiresAt is not { } expires)
        {
            return Loc.F("Lien valable {0}", Expiry.Replace("h", " h", StringComparison.Ordinal));
        }

        var left = expires - now;
        if (left <= TimeSpan.Zero)
        {
            return Loc.T("Lien expiré");
        }

        return left < TimeSpan.FromHours(1)
            ? Loc.F("Expire dans {0} min", Math.Max(1, (int)left.TotalMinutes))
            : Loc.F("Expire dans {0} h", (int)Math.Ceiling(left.TotalHours));
    }

    internal void SetSize(long size) => Size = size;

    /// <summary>Sizes as people read them: "820 Ko", "12,4 Mo", "1,2 Go" in French; "820 KB", "12.4 MB", "1.2 GB" in English.</summary>
    public static string FormatSize(double bytes) => bytes switch
    {
        < 1024 => $"{bytes:0} {Unit("o", "B")}",
        < 1024 * 1024 => (bytes / 1024).ToString("0", Loc.Culture) + " " + Unit("Ko", "KB"),
        < 1024 * 1024 * 1024 => (bytes / (1024 * 1024)).ToString(bytes < 10 * 1024 * 1024 ? "0.0" : "0", Loc.Culture) + " " + Unit("Mo", "MB"),
        _ => (bytes / (1024 * 1024 * 1024)).ToString("0.0", Loc.Culture) + " " + Unit("Go", "GB"),
    };

    private static string Unit(string french, string english) => Loc.IsEnglish ? english : french;
}
