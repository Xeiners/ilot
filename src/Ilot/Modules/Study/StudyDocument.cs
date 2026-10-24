using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ilot.Localization;
using Ilot.Modules.Assistant;

namespace Ilot.Modules.Study;

/// <summary>
/// A course as Gemini reads it. A PDF or a picture (a photo of handwritten notes, a scanned page) goes as it is: Gemini
/// reads the text, the figures and the scans itself. Word and PowerPoint (.docx, .pptx: zip files of XML) are read here,
/// their text sent with its slides numbered; a text file as it is. Nothing to install for any of them.
/// </summary>
public sealed partial class StudyDocument
{
    /// <summary>
    /// Above this, a document goes through Google's File API rather than with the question (a request is at most 20 MB,
    /// a third more once in base64).
    /// </summary>
    private const int LargestInline = 14 * 1024 * 1024;

    /// <summary>Google keeps an uploaded document 48 hours: it is sent again a little before.</summary>
    private static readonly TimeSpan UploadKept = TimeSpan.FromHours(46);

    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".docx"] = "text/plain",
        [".pptx"] = "text/plain",
        [".txt"] = "text/plain",
        [".md"] = "text/plain",
    };

    // Documents sent through the File API in this run: their address, until Google forgets them.
    private static readonly Dictionary<string, (string Uri, DateTime Until)> Uploaded = [];

    private readonly byte[]? _data;
    private readonly string? _text;

    private StudyDocument(string path, string mimeType, byte[]? data, string? text, int pages, bool slides)
    {
        Path = path;
        MimeType = mimeType;
        _data = data;
        _text = text;
        Pages = pages;
        IsSlides = slides;
    }

    public string Path { get; }

    public string MimeType { get; }

    /// <summary>Its pages, or its slides; 0 when it has none (a text).</summary>
    public int Pages { get; }

    /// <summary>A presentation: its parts are slides, not pages.</summary>
    public bool IsSlides { get; }

    /// <summary>The kinds of documents that can be studied: PDF, pictures, Word, PowerPoint, text.</summary>
    public static bool IsSupported(string path) => Kinds.ContainsKey(System.IO.Path.GetExtension(path));

    /// <summary>What a file picker offers.</summary>
    public static IEnumerable<string> Extensions => Kinds.Keys;

    /// <summary>
    /// Reads the document at <paramref name="path"/> (off the UI thread). Throws <see cref="GeminiException"/> with a
    /// message for the user when it cannot be read (or holds no text).
    /// </summary>
    public static async Task<StudyDocument> ReadAsync(string path)
    {
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (!Kinds.TryGetValue(extension, out string? mime))
        {
            throw new GeminiException(Loc.T("Ce format n'est pas pris en charge : enregistrez le cours en PDF."));
        }

        try
        {
            switch (extension)
            {
                case ".pdf":
                    return new StudyDocument(path, mime, await File.ReadAllBytesAsync(path), null, await PdfPagesAsync(path), slides: false);

                case ".docx":
                    return Text(path, await Task.Run(() => WordText(path)), pages: 0, slides: false);

                case ".pptx":
                    var (slides, count) = await Task.Run(() => SlidesText(path));
                    return Text(path, slides, count, slides: true);

                case ".txt":
                case ".md":
                    return Text(path, await File.ReadAllTextAsync(path), pages: 0, slides: false);

                default:
                    return new StudyDocument(path, mime, await File.ReadAllBytesAsync(path), null, pages: 1, slides: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            throw new GeminiException(Loc.F("Le document n'a pas pu être lu : {0}", ex.Message));
        }

        static StudyDocument Text(string path, string text, int pages, bool slides) => text.Trim().Length == 0
            ? throw new GeminiException(Loc.T("Ce document ne contient pas de texte à réviser."))
            : new StudyDocument(path, "text/plain", null, text, pages, slides);
    }

    /// <summary>
    /// The document as a part of a request: its text; or the file itself, with the question (inline) or, too large for
    /// that, sent first to Google (kept there 48 hours: sent once for every request of that time).
    /// </summary>
    public async Task<JsonObject> PartAsync(GeminiClient gemini, string key, CancellationToken cancellation)
    {
        if (_text is not null)
        {
            return new JsonObject { ["text"] = $"<document>\n{_text}\n</document>" };
        }

        if (_data!.Length <= LargestInline)
        {
            return new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = MimeType, ["data"] = Convert.ToBase64String(_data) } };
        }

        if (!Uploaded.TryGetValue(Path, out var sent) || sent.Until < DateTime.UtcNow)
        {
            string uri = await gemini.UploadAsync(key, _data, MimeType, System.IO.Path.GetFileName(Path), cancellation);
            sent = (uri, DateTime.UtcNow + UploadKept);
            Uploaded[Path] = sent;
        }

        return new JsonObject { ["fileData"] = new JsonObject { ["mimeType"] = MimeType, ["fileUri"] = sent.Uri } };
    }

    /// <summary>A PDF's page count, as Windows reads it; 0 when it cannot (protected…).</summary>
    private static async Task<int> PdfPagesAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            return (int)pdf.PageCount;
        }
        catch (Exception)
        {
            return 0; // Gemini reads it all the same.
        }
    }

    /// <summary>A Word document's text, paragraph by paragraph (its headings as they are).</summary>
    private static string WordText(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException(Loc.T("ce n'est pas un document Word."));
        using var stream = entry.Open();
        var xml = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var text = new StringBuilder();
        foreach (var paragraph in xml.Descendants(w + "p"))
        {
            string line = string.Concat(paragraph.Descendants().Where(node => node.Name == w + "t" || node.Name == w + "tab")
                .Select(node => node.Name == w + "tab" ? "\t" : node.Value));
            if (line.Trim().Length > 0)
            {
                text.AppendLine(line);
            }
        }

        return text.ToString();
    }

    /// <summary>A presentation's text, slide by slide (« Diapositive 3 »), its speaker notes with it.</summary>
    private static (string Text, int Count) SlidesText(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var slides = zip.Entries
            .Select(entry => (entry, match: SlideName().Match(entry.FullName)))
            .Where(slide => slide.match.Success)
            .OrderBy(slide => int.Parse(slide.match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        var text = new StringBuilder();
        foreach (var (entry, match) in slides)
        {
            string number = match.Groups[1].Value;
            text.AppendLine($"--- Diapositive {number} ---"); // For Gemini, which cites « diapo 4 »: not translated.
            AppendParagraphs(entry);
            if (zip.GetEntry($"ppt/notesSlides/notesSlide{number}.xml") is { } notes)
            {
                text.AppendLine("(Notes de l'orateur)");
                AppendParagraphs(notes);
            }
        }

        return (text.ToString(), slides.Count);

        void AppendParagraphs(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            foreach (var paragraph in XDocument.Load(stream).Descendants(a + "p"))
            {
                string line = string.Concat(paragraph.Descendants(a + "t").Select(node => node.Value));
                if (line.Trim().Length > 0)
                {
                    text.AppendLine(line);
                }
            }
        }
    }

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$")]
    private static partial Regex SlideName();
}
