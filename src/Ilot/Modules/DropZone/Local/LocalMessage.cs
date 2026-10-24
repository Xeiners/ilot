namespace Ilot.Modules.DropZone.Local;

public enum LocalMessageKind
{
    Text,
    Link,
    Image,
    File,
}

/// <summary>
/// One item exchanged in the Wi-Fi local chat between the PC and another device on <c>mynotch.local</c>: a text
/// snippet, a web link, a pasted or sent image, or a file (or folder).
/// </summary>
public sealed class LocalMessage
{
    public LocalMessage(
        string id,
        bool fromPc,
        LocalMessageKind kind,
        DateTimeOffset sentAt)
    {
        Id = id;
        FromPc = fromPc;
        Kind = kind;
        SentAt = sentAt;
    }

    public string Id { get; }

    /// <summary>
    /// Monotonic version assigned by <see cref="LocalTransferService"/> when added or updated (e.g. once an incoming
    /// file is accepted or refused), so the web page's long-poll only fetches what changed since its last sequence.
    /// </summary>
    public long Sequence { get; internal set; }

    /// <summary><c>true</c> when sent from this PC; <c>false</c> when sent from the other device on the network.</summary>
    public bool FromPc { get; }

    public LocalMessageKind Kind { get; }

    public DateTimeOffset SentAt { get; }

    public string? Text { get; init; }

    public Uri? Link { get; init; }

    public string? FilePath { get; internal set; }

    public string? FileName { get; init; }

    /// <summary>In bytes, for images and files.</summary>
    public long FileSize { get; init; }

    public bool IsFolder { get; init; }

    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    /// <summary>When a file sent from the other device awaits (or got) the user's decision on the PC.</summary>
    public IncomingFile? Incoming { get; init; }

    public string TimeText => SentAt.LocalDateTime.ToString("HH:mm");

    /// <summary>The link's host, without "www.".</summary>
    public string Domain => Link is null
        ? ""
        : Link.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? Link.Host[4..] : Link.Host;

    /// <summary>State of the file for the chat UI: "pending", "accepted", "refused", or "shared".</summary>
    public string FileState => Incoming is null
        ? "shared"
        : !Incoming.IsDecided
            ? "pending"
            : Incoming.Decision.Result is not null ? "accepted" : "refused";

    /// <summary>A lone web address: <c>https://…</c>, <c>http://…</c> or <c>www.…</c>.</summary>
    public static bool TryParseLink(string text, out Uri uri)
    {
        uri = null!;
        string candidate = text.Trim();
        if (candidate.Length is 0 or > 2048 || candidate.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public static bool IsImageFile(string name) => Path.GetExtension(name).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".jfif" or ".webp" or ".gif" or ".bmp" or ".avif" or ".heic" or ".heif" or ".svg";

    /// <summary>
    /// A file's type from its name, as the phone is told it: its browser names the download by it (Chrome on Android,
    /// given no type, took a PDF for text and saved « cours.pdf.txt »). Unknown: plain bytes.
    /// </summary>
    public static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".odt" => "application/vnd.oasis.opendocument.text",
        ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
        ".odp" => "application/vnd.oasis.opendocument.presentation",
        ".rtf" => "application/rtf",
        ".txt" or ".log" or ".md" => "text/plain",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".epub" => "application/epub+zip",
        ".zip" => "application/zip",
        ".rar" => "application/vnd.rar",
        ".7z" => "application/x-7z-compressed",
        ".apk" => "application/vnd.android.package-archive",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        ".ogg" => "audio/ogg",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".png" => "image/png",
        ".jpg" or ".jpeg" or ".jfif" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".avif" => "image/avif",
        ".heic" => "image/heic",
        ".heif" => "image/heif",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}

