using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace MyNotch.Modules.DropZone.Hosts;

/// <summary>A host that takes the file as a form post and answers with the link (Gofile…).</summary>
public abstract class MultipartUploadHost : IUploadHost
{
    private static readonly HttpClient Http = CreateClient();

    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract long MaxSize { get; }

    public abstract string MaxSizeText { get; }

    public abstract bool HasExpiry { get; }

    public bool IsEncrypted => false;

    protected abstract Uri Endpoint { get; }

    /// <summary>The form to post: <paramref name="file"/> (already streaming, with its progress) plus the host's fields.</summary>
    protected abstract MultipartFormDataContent CreateBody(UploadItem item, HttpContent file);

    /// <summary>The link out of the host's answer; throws <see cref="UploadRejectedException"/> when it refused.</summary>
    protected abstract string ReadLink(HttpStatusCode status, string answer);

    public async Task<string> UploadAsync(UploadItem item, Stream file, Action<long> sent, CancellationToken cancellation)
    {
        var content = new ProgressStreamContent(file, sent);
        content.Headers.ContentType = new MediaTypeHeaderValue(Local.LocalMessage.ContentTypeFor(item.Name)); // Its real type, served with it.
        using var body = CreateBody(item, content);
        using var response = await Http.PostAsync(Endpoint, body, cancellation);
        string answer = await response.Content.ReadAsStringAsync(cancellation);
        Debug.WriteLine($"[Uploads] {Name}: {(int)response.StatusCode} {(answer.Length < 160 ? answer : answer[..160])}");

        // Busy or failing: worth a few more tries. Anything else is the host's final word.
        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
        {
            throw new HttpRequestException($"{Name}: {(int)response.StatusCode}", null, response.StatusCode);
        }

        return ReadLink(response.StatusCode, answer);
    }

    private static HttpClient CreateClient()
    {
        // No overall timeout: a big file takes as long as it takes. Stalls are caught by the upload service.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyNotch/1.0");
        return client;
    }
}
