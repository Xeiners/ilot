using System.Net;
using System.Text.Json;
using MyNotch.Localization;

namespace MyNotch.Modules.DropZone.Hosts;

/// <summary>
/// Gofile (gofile.io): anonymous uploads, no account, large files (capped here at 5 GB). Files are not given an
/// expiry: Gofile removes them once they are no longer downloaded. It answers in JSON, the link being
/// <c>data.downloadPage</c>.
/// </summary>
public sealed class GofileHost : MultipartUploadHost
{
    public override string Id => "gofile";

    public override string Name => "Gofile";

    public override long MaxSize => 5_000_000_000;

    public override string MaxSizeText => Loc.T("5 Go");

    public override bool HasExpiry => false;

    protected override Uri Endpoint { get; } = new("https://upload.gofile.io/uploadfile");

    protected override MultipartFormDataContent CreateBody(UploadItem item, HttpContent file) => new()
    {
        { file, "file", item.Name },
    };

    protected override string ReadLink(HttpStatusCode status, string answer)
    {
        string? error = null;
        try
        {
            using var json = JsonDocument.Parse(answer);
            var root = json.RootElement;
            if (root.TryGetProperty("status", out var state) && state.GetString() == "ok"
                && root.TryGetProperty("data", out var data)
                && data.TryGetProperty("downloadPage", out var page)
                && Uri.TryCreate(page.GetString(), UriKind.Absolute, out var link)
                && link.Scheme == Uri.UriSchemeHttps)
            {
                return link.AbsoluteUri;
            }

            error = root.TryGetProperty("status", out var reason) ? reason.GetString() : null;
        }
        catch (JsonException)
        {
            // Not JSON: an error page.
        }

        throw new UploadRejectedException(status == HttpStatusCode.OK
            ? Loc.T("Réponse inattendue de Gofile") + (error is null ? "" : $" ({error})")
            : Loc.F("Refusé par Gofile ({0})", (int)status));
    }
}
