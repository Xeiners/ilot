using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ilot.Localization;

namespace Ilot.Modules.DropZone.Hosts;

/// <summary>
/// Send (the open source successor of Firefox Send, here its author's instance send.vis.ee): the file is encrypted on
/// this computer, end to end. The key only travels in the link, after the <c>#</c>, which browsers never send to a
/// server: the host stores data it cannot read, and the recipient's browser decrypts it, nothing to install.
/// </summary>
/// <remarks>
/// The protocol of timvisee/send, byte for byte, so its web page can decrypt:
/// <list type="bullet">
/// <item>A random 16 byte secret. From it, with HKDF-SHA256 and no salt: the metadata key ("metadata", AES-128-GCM)
/// and the authentication key ("authentication", 64 bytes, given to the server to check later downloads).</item>
/// <item>The name, size and type, encrypted with the metadata key (zero nonce: the key is used once).</item>
/// <item>The file itself in "aes128gcm" content encoding (RFC 8188): a header (random salt, record size, no key id),
/// then 64 KiB records, each AES-128-GCM with a nonce derived from its number, padded with 0x01 (0x02 for the last).</item>
/// <item>A WebSocket to <c>/api/ws</c>: the JSON description first (the server answers with the download URL), the
/// encrypted bytes as binary frames, a single zero byte to end, then the server confirms.</item>
/// </list>
/// </remarks>
public sealed class SendHost : IUploadHost
{
    private const int SecretLength = 16;
    private const int KeyLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int RecordSize = 64 * 1024;

    // Each record carries its 16 byte tag and a 1 byte delimiter: what is left is plaintext.
    private const int RecordData = RecordSize - TagLength - 1;

    private static readonly Uri Socket = new("wss://send.vis.ee/api/ws");

    // The instance's limits (see its /config): 3 days at most, 20 downloads.
    private static readonly TimeSpan LongestLife = TimeSpan.FromDays(3);
    private const int Downloads = 20;

    public string Id => "send";

    public string Name => "Send";

    /// <summary>2.5 GiB, the instance's limit.</summary>
    public long MaxSize => 2_684_354_560;

    public string MaxSizeText => Loc.T("2,5 Go");

    public bool HasExpiry => true;

    public bool IsEncrypted => true;

    public async Task<string> UploadAsync(UploadItem item, Stream file, Action<long> sent, CancellationToken cancellation)
    {
        byte[] secret = RandomNumberGenerator.GetBytes(SecretLength);
        byte[] metadataKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeyLength, [], Encoding.UTF8.GetBytes("metadata"));
        byte[] authKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, [], Encoding.UTF8.GetBytes("authentication"));

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", "Ilot/1.0");
        await socket.ConnectAsync(Socket, cancellation);

        // The description: encrypted metadata, the key that will authorize downloads, and the limits.
        var lifetime = item.ExpiryDuration is { } chosen && chosen < LongestLife ? chosen : LongestLife;
        string description = JsonSerializer.Serialize(new
        {
            fileMetadata = Base64Url(EncryptMetadata(item, metadataKey)),
            authorization = $"send-v1 {Base64Url(authKey)}",
            bearer = (string?)null,
            timeLimit = (int)lifetime.TotalSeconds,
            dlimit = Downloads,
        });
        await socket.SendAsync(Encoding.UTF8.GetBytes(description), WebSocketMessageType.Text, true, cancellation);
        using var info = await ReceiveAsync(socket, cancellation);
        string url = info.RootElement.GetProperty("url").GetString()
            ?? throw new UploadRejectedException(Loc.T("Réponse inattendue de Send"));

        await SendEncryptedAsync(socket, file, secret, sent, cancellation);
        await socket.SendAsync(new byte[] { 0 }, WebSocketMessageType.Binary, true, cancellation); // End of file.
        using var done = await ReceiveAsync(socket, cancellation);
        if (!done.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            throw new UploadRejectedException(Loc.T("Send n'a pas confirmé l'envoi"));
        }

        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellation);
        }
        catch (WebSocketException)
        {
            // Sent and confirmed: how the socket closes does not matter.
        }

        // The key goes after the '#': never sent to any server, read by the recipient's browser to decrypt.
        return $"{url}#{Base64Url(secret)}";
    }

    /// <summary>
    /// The name, size and type, as the web page expects them, AES-128-GCM with a zero nonce. The real type (the page saves
    /// the file with it: a phone's browser may name a file of no known type « .txt »).
    /// </summary>
    private static byte[] EncryptMetadata(UploadItem item, byte[] key)
    {
        string Type = Local.LocalMessage.ContentTypeFor(item.Name);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = item.Name,
            size = item.Size,
            type = Type,
            manifest = new { files = new[] { new { name = item.Name, size = item.Size, type = Type } } },
        });

        var sealedData = new byte[plain.Length + TagLength];
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(new byte[NonceLength], plain, sealedData.AsSpan(0, plain.Length), sealedData.AsSpan(plain.Length));
        return sealedData; // Ciphertext then tag, like WebCrypto.
    }

    /// <summary>The file in aes128gcm content encoding (RFC 8188), one record per frame, streamed as it is read.</summary>
    private static async Task SendEncryptedAsync(ClientWebSocket socket, Stream file, byte[] secret, Action<long> sent, CancellationToken cancellation)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] contentKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeyLength, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        byte[] nonceBase = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, NonceLength, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        // Header: salt, record size (big endian), key id length 0.
        var header = new byte[21];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        await socket.SendAsync(header, WebSocketMessageType.Binary, true, cancellation);

        using var aes = new AesGcm(contentKey, TagLength);
        var current = new byte[RecordData];
        var next = new byte[RecordData];
        var plain = new byte[RecordSize - TagLength];
        var record = new byte[RecordSize];
        var nonce = new byte[NonceLength];
        int length = await file.ReadAtLeastAsync(current, RecordData, throwOnEndOfStream: false, cancellation);
        long total = 0;
        for (uint sequence = 0; ; sequence++)
        {
            // Read one record ahead: the last one is padded differently.
            int nextLength = length < RecordData ? 0 : await file.ReadAtLeastAsync(next, RecordData, throwOnEndOfStream: false, cancellation);
            bool last = nextLength == 0;

            current.AsSpan(0, length).CopyTo(plain);
            int padded;
            if (last)
            {
                plain[length] = 2;
                padded = length + 1;
            }
            else
            {
                plain.AsSpan(length).Clear();
                plain[length] = 1;
                padded = plain.Length;
            }

            nonceBase.CopyTo(nonce, 0);
            uint counter = BinaryPrimitives.ReadUInt32BigEndian(nonce.AsSpan(NonceLength - 4)) ^ sequence;
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NonceLength - 4), counter);
            aes.Encrypt(nonce, plain.AsSpan(0, padded), record.AsSpan(0, padded), record.AsSpan(padded, TagLength));
            await socket.SendAsync(record.AsMemory(0, padded + TagLength), WebSocketMessageType.Binary, true, cancellation);

            total += length;
            sent(total);
            if (last)
            {
                return;
            }

            (current, next) = (next, current);
            length = nextLength;
        }
    }

    /// <summary>The server's JSON answer; one with an "error" is a refusal.</summary>
    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellation);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "Send a fermé la connexion");
            }

            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        message.Position = 0;
        var json = JsonDocument.Parse(message);
        Debug.WriteLine($"[Uploads] Send: {json.RootElement.GetRawText()}");
        if (json.RootElement.TryGetProperty("error", out var error))
        {
            int code = error.ValueKind == JsonValueKind.Number ? error.GetInt32() : 0;
            json.Dispose();
            throw code >= 500
                ? new HttpRequestException($"Send: {code}", null, (System.Net.HttpStatusCode)code)
                : new UploadRejectedException(Loc.F("Refusé par Send ({0})", error));
        }

        return json;
    }

    /// <summary>Base64 for URLs, without padding, as the web page writes it.</summary>
    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
