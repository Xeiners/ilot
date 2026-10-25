using System.Text.Json;

namespace Ilot.Updates;

/// <summary>
/// A release of Ilot on GitHub: its version (from its tag, « v1.0.1 »), its installer (« Ilot-Setup-1.0.1.exe »), the
/// installer's size and SHA-256 when GitHub gives it, and its page.
/// </summary>
public sealed record UpdateRelease(Version Version, string InstallerName, Uri InstallerUrl, long Size, string? Sha256, Uri Page)
{
    /// <summary>
    /// The latest release as GitHub's API describes it (<c>releases/latest</c>: never a draft nor a pre-release);
    /// <c>null</c> when its tag is not a version, or it has no installer to download from GitHub itself.
    /// </summary>
    public static UpdateRelease? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string tag = root.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)
            || !root.TryGetProperty("html_url", out var page) || !Uri.TryCreate(page.GetString(), UriKind.Absolute, out var pageUri)
            || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out var assetName) ? assetName.GetString() ?? "" : "";
            if (!name.StartsWith("Ilot-Setup", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || !asset.TryGetProperty("browser_download_url", out var url) || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var download)
                || download.Scheme != Uri.UriSchemeHttps || !download.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long size = asset.TryGetProperty("size", out var bytes) && bytes.TryGetInt64(out long length) ? length : 0;
            string? digest = asset.TryGetProperty("digest", out var hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString() : null;
            string? sha256 = digest is { Length: > 7 } value && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? value[7..].ToLowerInvariant()
                : null;
            return new UpdateRelease(Normalize(version), name, download, size, sha256, pageUri);
        }

        return null;
    }

    /// <summary>A version on three parts (1.0.1, as tags have it; the assembly's 1.0.1.0 too), so both compare.</summary>
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
