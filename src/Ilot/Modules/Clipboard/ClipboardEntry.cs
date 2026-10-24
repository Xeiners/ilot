using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.UI;
using Ilot.Localization;

namespace Ilot.Modules.Clipboard;

public enum ClipboardKind
{
    Text,
    Link,
    Color,
    Image,
}

/// <summary>
/// Something that was copied. Its content never changes; only its pin and the last time it was copied do.
/// Text is classified once, when the entry is made: a web link, a hexadecimal colour, or plain text.
/// </summary>
public sealed class ClipboardEntry : ObservableObject
{
    private const int PreviewLength = 280;

    private bool _isPinned;
    private DateTimeOffset _copiedAt;

    private ClipboardEntry(Guid id, ClipboardKind kind, string signature, DateTimeOffset copiedAt)
    {
        Id = id;
        Kind = kind;
        Signature = signature;
        _copiedAt = copiedAt;
    }

    public Guid Id { get; }

    public ClipboardKind Kind { get; }

    /// <summary>Same content, same signature: copying it again brings the entry back up instead of duplicating it.</summary>
    public string Signature { get; }

    /// <summary>The copied text (text, link and colour entries).</summary>
    public string? Text { get; private init; }

    /// <summary>Where the image is kept (image entries).</summary>
    public string? ImagePath { get; private init; }

    public int ImageWidth { get; private init; }

    public int ImageHeight { get; private init; }

    /// <summary>Hash of the image pixels: an identical copy is recognised.</summary>
    public string? ImageHash { get; private init; }

    public Uri? Link { get; private init; }

    public Color Color { get; private init; }

    /// <summary>A pinned entry stays at the top of the list and is never pushed out by new copies.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    /// <summary>When it was pinned: the latest pin goes first.</summary>
    public DateTimeOffset? PinnedAt { get; set; }

    public DateTimeOffset CopiedAt
    {
        get => _copiedAt;
        set => SetProperty(ref _copiedAt, value);
    }

    public string KindLabel => Kind switch
    {
        ClipboardKind.Link => Loc.T("Lien"),
        ClipboardKind.Color => Loc.T("Couleur"),
        ClipboardKind.Image => Loc.T("Image"),
        _ => Loc.T("Texte"),
    };

    /// <summary>What the closed notch says when it is copied.</summary>
    public string CopiedMessage => Kind switch
    {
        ClipboardKind.Link => Loc.T("Lien copié"),
        ClipboardKind.Color => Loc.T("Couleur copiée"),
        ClipboardKind.Image => Loc.T("Image copiée"),
        _ => Loc.T("Texte copié"),
    };

    /// <summary>Segoe Fluent Icons glyph of the kind.</summary>
    public string KindGlyph => Kind switch
    {
        ClipboardKind.Link => "",
        ClipboardKind.Color => "",
        ClipboardKind.Image => "",
        _ => "",
    };

    /// <summary>Main line: the text itself, the link's domain, the colour code or the image size.</summary>
    public string Headline => Kind switch
    {
        ClipboardKind.Link => Domain,
        ClipboardKind.Color => Text!.Trim().ToUpperInvariant(),
        ClipboardKind.Image => $"{ImageWidth} × {ImageHeight}",
        _ => Preview(Text!),
    };

    /// <summary>Secondary line: the link's path, the colour in RGB, or the length of the text.</summary>
    public string Detail => Kind switch
    {
        ClipboardKind.Link => LinkPath,
        ClipboardKind.Color => Rgb,
        ClipboardKind.Image => "",
        _ => Loc.Count(Text!.Length, "caractère", "caractères"),
    };

    /// <summary>The link's host, without "www.".</summary>
    public string Domain => Link is null ? "" : Link.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? Link.Host[4..] : Link.Host;

    /// <summary>The colour in CSS functional notation: <c>rgb(255, 87, 51)</c>, or <c>rgba(…)</c> when translucent.</summary>
    public string Rgb => Color.A == 255
        ? $"rgb({Color.R}, {Color.G}, {Color.B})"
        : string.Create(CultureInfo.InvariantCulture, $"rgba({Color.R}, {Color.G}, {Color.B}, {Color.A / 255.0:0.##})");

    private string LinkPath
    {
        get
        {
            if (Link is null)
            {
                return "";
            }

            string path = Uri.UnescapeDataString(Link.PathAndQuery);
            return path == "/" ? "" : path;
        }
    }

    /// <summary>How long ago it was copied, the way a person would say it.</summary>
    public string AgeAt(DateTimeOffset now)
    {
        var age = now - CopiedAt;
        if (age < TimeSpan.FromMinutes(1))
        {
            return Loc.T("À l'instant");
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Loc.F("Il y a {0} min", (int)age.TotalMinutes);
        }

        if (age < TimeSpan.FromDays(1) && CopiedAt.LocalDateTime.Date == now.LocalDateTime.Date)
        {
            return Loc.F("Il y a {0} h", (int)age.TotalHours);
        }

        if (CopiedAt.LocalDateTime.Date == now.LocalDateTime.Date.AddDays(-1))
        {
            return Loc.T("Hier");
        }

        return CopiedAt.LocalDateTime.ToString(CopiedAt.Year == now.Year ? "d MMM" : "d MMM yyyy", Loc.Culture);
    }

    /// <summary>A text entry, classified: link, colour or plain text.</summary>
    public static ClipboardEntry FromText(string text, DateTimeOffset copiedAt, Guid? id = null)
    {
        var kind = ClipboardKind.Text;
        Uri? link = null;
        Color color = default;
        if (TryParseLink(text, out var uri))
        {
            kind = ClipboardKind.Link;
            link = uri;
        }
        else if (HexColor.TryParse(text, out var parsed))
        {
            kind = ClipboardKind.Color;
            color = parsed;
        }

        return new ClipboardEntry(id ?? Guid.NewGuid(), kind, TextSignature(text), copiedAt)
        {
            Text = text,
            Link = link,
            Color = color,
        };
    }

    public static ClipboardEntry FromImage(Guid id, string path, int width, int height, string hash, DateTimeOffset copiedAt) =>
        new(id, ClipboardKind.Image, ImageSignature(hash), copiedAt)
        {
            ImagePath = path,
            ImageWidth = width,
            ImageHeight = height,
            ImageHash = hash,
        };

    public static string TextSignature(string text) => "t:" + text;

    public static string ImageSignature(string hash) => "i:" + hash;

    /// <summary>A lone web address: <c>https://…</c>, <c>http://…</c> or <c>www.…</c>.</summary>
    private static bool TryParseLink(string text, out Uri uri)
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

    /// <summary>Spaces collapsed, blank lines dropped, cut short: enough to recognise it at a glance.</summary>
    private static string Preview(string text)
    {
        var preview = new StringBuilder(Math.Min(text.Length, PreviewLength));
        bool space = false;
        foreach (char c in text.AsSpan(0, Math.Min(text.Length, PreviewLength * 2)).Trim())
        {
            if (c is '\r')
            {
                continue;
            }

            if (c is '\n')
            {
                if (preview.Length > 0 && preview[^1] != '\n')
                {
                    TrimEnd(preview);
                    preview.Append('\n');
                }

                space = false;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                space = preview.Length > 0 && preview[^1] != '\n';
                continue;
            }

            if (space)
            {
                preview.Append(' ');
                space = false;
            }

            preview.Append(c);
            if (preview.Length >= PreviewLength)
            {
                break;
            }
        }

        return preview.ToString();

        static void TrimEnd(StringBuilder builder)
        {
            while (builder.Length > 0 && builder[^1] == ' ')
            {
                builder.Length--;
            }
        }
    }
}
