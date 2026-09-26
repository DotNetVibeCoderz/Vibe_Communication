using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoipNet.AI.Speech;

/// <summary>Settings for Amazon Polly.</summary>
public sealed class AmazonPollyOptions
{
    /// <summary>AWS access key id.</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>AWS secret access key.</summary>
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>Session token, when using temporary credentials.</summary>
    public string? SessionToken { get; set; }

    /// <summary>AWS region, for example <c>ap-southeast-1</c>.</summary>
    public string Region { get; set; } = "ap-southeast-1";

    /// <summary>Voice identifier, for example <c>Bianca</c> or the Indonesian voice <c>Rin</c>.</summary>
    public string VoiceId { get; set; } = "Joanna";

    /// <summary>Engine: <c>neural</c>, <c>generative</c> or <c>standard</c>.</summary>
    public string Engine { get; set; } = "neural";
}

/// <summary>
/// Speech synthesis with Amazon Polly. Requests are signed with Signature Version 4, so the AWS
/// SDK is not required. Polly returns 8 or 16 kHz PCM, which suits telephony directly.
/// </summary>
/// <param name="options">Provider settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class AmazonPollyTextToSpeech(AmazonPollyOptions options, HttpClient? httpClient = null) : ITextToSpeech
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public string Name => "Amazon Polly";

    /// <inheritdoc/>
    public int PreferredSampleRate => 16000;

    /// <inheritdoc/>
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SpeechSynthesisOptions? ttsOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var rate = (ttsOptions?.SampleRate ?? PreferredSampleRate) <= 8000 ? 8000 : 16000;
        var body = JsonSerializer.Serialize(new
        {
            Engine = ttsOptions?.Model ?? options.Engine,
            OutputFormat = "pcm",
            SampleRate = rate.ToString(CultureInfo.InvariantCulture),
            Text = text,
            VoiceId = ttsOptions?.Voice ?? options.VoiceId,
            LanguageCode = ttsOptions?.Language,
        });

        var endpoint = new Uri($"https://polly.{options.Region}.amazonaws.com/v1/speech");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        AwsSignatureV4.Sign(request, body, "polly", options.Region, options.AccessKeyId, options.SecretAccessKey, options.SessionToken);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await response.EnsureSuccessAsync("Amazon", cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var chunk in PcmStream.ReadChunksAsync(stream, rate, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }
}

/// <summary>Signs HTTP requests with AWS Signature Version 4.</summary>
public static class AwsSignatureV4
{
    /// <summary>Adds the Authorization and x-amz-* headers to a request.</summary>
    /// <param name="request">Request to sign. Its URI and method must already be set.</param>
    /// <param name="payload">Request body as sent.</param>
    /// <param name="service">AWS service name, for example <c>polly</c>.</param>
    /// <param name="region">AWS region.</param>
    /// <param name="accessKeyId">Access key id.</param>
    /// <param name="secretAccessKey">Secret access key.</param>
    /// <param name="sessionToken">Optional session token.</param>
    public static void Sign(
        HttpRequestMessage request,
        string payload,
        string service,
        string region,
        string accessKeyId,
        string secretAccessKey,
        string? sessionToken = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri ?? throw new ArgumentException("The request has no URI.", nameof(request));
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (sessionToken is { Length: > 0 })
        {
            request.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken);
        }

        var signedHeaders = sessionToken is { Length: > 0 }
            ? "content-type;host;x-amz-content-sha256;x-amz-date;x-amz-security-token"
            : "content-type;host;x-amz-content-sha256;x-amz-date";

        var canonicalHeaders = new StringBuilder()
            .Append("content-type:application/json\n")
            .Append("host:").Append(uri.Host).Append('\n')
            .Append("x-amz-content-sha256:").Append(payloadHash).Append('\n')
            .Append("x-amz-date:").Append(amzDate).Append('\n');
        if (sessionToken is { Length: > 0 })
        {
            canonicalHeaders.Append("x-amz-security-token:").Append(sessionToken).Append('\n');
        }

        var canonicalRequest = string.Join('\n',
            request.Method.Method,
            uri.AbsolutePath,
            uri.Query.TrimStart('?'),
            canonicalHeaders.ToString(),
            signedHeaders,
            payloadHash);

        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = HmacChain($"AWS4{secretAccessKey}", dateStamp, region, service, "aws4_request");
        var signature = Hex(HmacSHA256(signingKey, stringToSign));

        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    /// <summary>
    /// Signs a WebSocket URL by putting the credentials in its query, which is how the streaming
    /// services are opened: a socket has no request to add headers to.
    /// </summary>
    /// <param name="endpoint">The endpoint to open, scheme and path included.</param>
    /// <param name="query">Query parameters the service itself needs.</param>
    /// <param name="service">AWS service name, for example <c>transcribe</c>.</param>
    /// <param name="region">AWS region.</param>
    /// <param name="accessKeyId">Access key id.</param>
    /// <param name="secretAccessKey">Secret access key.</param>
    /// <param name="sessionToken">Optional session token.</param>
    /// <param name="lifetime">How long the signature stays valid.</param>
    /// <param name="now">The time to sign for; the default is now.</param>
    public static Uri PresignWebSocket(
        Uri endpoint,
        IReadOnlyList<(string Key, string Value)> query,
        string service,
        string region,
        string accessKeyId,
        string secretAccessKey,
        string? sessionToken = null,
        TimeSpan? lifetime = null,
        DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(query);
        var host = endpoint.IsDefaultPort ? endpoint.Host : endpoint.Authority;
        var path = endpoint.AbsolutePath;
        var at = now ?? DateTime.UtcNow;
        var amzDate = at.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = at.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var seconds = (int)Math.Clamp((lifetime ?? TimeSpan.FromMinutes(5)).TotalSeconds, 1, 604800);

        var parameters = new List<(string Key, string Value)>
        {
            ("X-Amz-Algorithm", "AWS4-HMAC-SHA256"),
            ("X-Amz-Credential", $"{accessKeyId}/{credentialScope}"),
            ("X-Amz-Date", amzDate),
            ("X-Amz-Expires", seconds.ToString(CultureInfo.InvariantCulture)),
            ("X-Amz-SignedHeaders", "host"),
        };
        if (sessionToken is { Length: > 0 })
        {
            parameters.Add(("X-Amz-Security-Token", sessionToken));
        }

        parameters.AddRange(query);

        // The canonical query is sorted by the encoded name, and every value is encoded the same way.
        var canonicalQuery = string.Join(
            '&',
            parameters
                .Select(p => (Key: Escape(p.Key), Value: Escape(p.Value)))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={p.Value}"));

        // A socket carries no body, so the payload hash is the hash of nothing at all.
        var emptyPayload = Hex(SHA256.HashData([]));
        var canonicalRequest = string.Join('\n', "GET", path, canonicalQuery, $"host:{host}\n", "host", emptyPayload);
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            amzDate,
            credentialScope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        var signature = Hex(HmacSHA256(HmacChain($"AWS4{secretAccessKey}", dateStamp, region, service, "aws4_request"), stringToSign));
        return new Uri($"{endpoint.Scheme}://{endpoint.Authority}{path}?{canonicalQuery}&X-Amz-Signature={signature}");
    }

    private static string Escape(string value) => Uri.EscapeDataString(value).Replace("%7E", "~", StringComparison.Ordinal);

    private static byte[] HmacChain(string key, params string[] parts)
    {
        var current = Encoding.UTF8.GetBytes(key);
        foreach (var part in parts)
        {
            current = HmacSHA256(current, part);
        }

        return current;
    }

    private static byte[] HmacSHA256(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
