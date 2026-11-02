using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Qahwa.Core;

/// <summary>Settings for keeping attachments in an S3-compatible bucket (Qahwa:Files:S3).</summary>
public sealed class S3Options
{
    /// <summary>The address of the service, e.g. https://s3.eu-central-1.amazonaws.com or https://eu2.contabostorage.com.</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    /// <summary>True (the default) puts the bucket in the path (service/bucket/key), which every S3-compatible service accepts; false puts it in the host name (bucket.service/key).</summary>
    public bool PathStyle { get; set; } = true;

    /// <summary>A folder inside the bucket to keep the files in (letters, digits, - _ . /), or empty.</summary>
    public string Prefix { get; set; } = string.Empty;

    private static readonly Regex BucketName = new("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.Compiled);
    private static readonly Regex PrefixName = new("^[A-Za-z0-9._/-]{0,200}$", RegexOptions.Compiled);

    /// <exception cref="InvalidOperationException">What is wrong with the settings, so the server stops at start-up instead of failing on the first upload.</exception>
    public void Validate()
    {
        if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.PathAndQuery.Length > 1)
            throw new InvalidOperationException("Qahwa:Files:S3:ServiceUrl must be the address of the service, such as https://s3.amazonaws.com (no path).");
        if (uri.Scheme == "http" && !uri.IsLoopback)
            throw new InvalidOperationException("Qahwa:Files:S3:ServiceUrl must use https (plain http is only allowed for localhost, for testing).");
        if (!BucketName.IsMatch(Bucket))
            throw new InvalidOperationException("Qahwa:Files:S3:Bucket must be a bucket name (3 to 63 lower-case letters, digits, dots and hyphens).");
        if (string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey))
            throw new InvalidOperationException("Qahwa:Files:S3:AccessKey and SecretKey are required.");
        if (string.IsNullOrWhiteSpace(Region))
            throw new InvalidOperationException("Qahwa:Files:S3:Region is required (use us-east-1 when the service has no regions).");
        if (!PrefixName.IsMatch(Prefix) || Prefix.Contains("..", StringComparison.Ordinal) || Prefix.StartsWith('/'))
            throw new InvalidOperationException("Qahwa:Files:S3:Prefix may only hold letters, digits and - _ . / and must not start with / or contain ..");
    }
}

/// <summary>
/// AWS Signature Version 4 for S3 requests (the "Authorization header" form). It is written out here, on System.Security.Cryptography,
/// so keeping files in a bucket needs no SDK. the tests reproduce the signatures AWS publishes in its documentation.
/// </summary>
public static class SigV4
{
    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string AmzDate(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Each path segment percent-encoded as S3 wants it (unreserved characters stay, / separates segments).</summary>
    public static string EncodePath(string path)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '/')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <param name="headers">The headers to sign, by lower-case name, with their values (must include host, x-amz-content-sha256 and x-amz-date).</param>
    /// <returns>The value of the Authorization header.</returns>
    public static string Authorization(
        string method, string encodedPath, string query, IReadOnlyDictionary<string, string> headers, string payloadHash,
        string amzDate, string accessKey, string secretKey, string region)
    {
        var sorted = headers.OrderBy(h => h.Key, StringComparer.Ordinal).ToList();
        var canonicalHeaders = string.Concat(sorted.Select(h => $"{h.Key}:{h.Value.Trim()}\n"));
        var signedHeaders = string.Join(';', sorted.Select(h => h.Key));
        var canonicalRequest = $"{method}\n{encodedPath}\n{query}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        var date = amzDate[..8];
        var scope = $"{date}/{region}/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest))}";

        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date), region), "s3"), "aws4_request");
        var signature = Convert.ToHexString(Hmac(key, toSign)).ToLowerInvariant();
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }
}

/// <summary>
/// Keeps files in an S3-compatible bucket (Amazon S3, MinIO, Cloudflare R2, Contabo, Wasabi and the like). Like the disk storage, a key is
/// 32 random hex digits that is checked on every use, so a name given by a person can never address another object. A file is copied to a
/// temporary file first so its hash can be signed (and nothing big is held in memory), then sent in one request.
/// </summary>
public sealed partial class S3FileStorage : IFileStorage
{
    private readonly S3Options _options;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    public S3FileStorage(S3Options options, HttpClient? http = null, TimeProvider? clock = null)
    {
        options.Validate();
        _options = options;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _clock = clock ?? TimeProvider.System;
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex KeyRegex();

    private (Uri Uri, string Host, string EncodedPath) Locate(string key)
    {
        if (!KeyRegex().IsMatch(key))
            throw new ArgumentException("Not a file key.", nameof(key));
        var service = new Uri(_options.ServiceUrl);
        var objectPath = (_options.Prefix.Length > 0 ? _options.Prefix.TrimEnd('/') + "/" : string.Empty) + key[..2] + "/" + key;
        var host = _options.PathStyle ? service.Authority : $"{_options.Bucket}.{service.Authority}";
        var path = "/" + (_options.PathStyle ? _options.Bucket + "/" : string.Empty) + objectPath;
        var encoded = SigV4.EncodePath(path);
        return (new UriBuilder(service.Scheme, host.Split(':')[0], service.IsDefaultPort ? -1 : service.Port) { Path = encoded }.Uri, host, encoded);
    }

    private HttpRequestMessage Signed(HttpMethod method, string key, string payloadHash, HttpContent? content = null)
    {
        var (uri, host, encoded) = Locate(key);
        var amzDate = SigV4.AmzDate(_clock.GetUtcNow());
        var headers = new Dictionary<string, string> { ["host"] = host, ["x-amz-content-sha256"] = payloadHash, ["x-amz-date"] = amzDate };
        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("Authorization",
            SigV4.Authorization(method.Method, encoded, string.Empty, headers, payloadHash, amzDate, _options.AccessKey, _options.SecretKey, _options.Region));
        return request;
    }

    private static IOException Failed(string what, System.Net.HttpStatusCode status) =>
        new($"The file storage ({what}) answered {(int)status} {status}.");

    public async Task<string> SaveAsync(Stream content, CancellationToken ct = default)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var temp = Path.Combine(Path.GetTempPath(), "qahwa-s3-" + key);
        try
        {
            string hash;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }

            await using var upload = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var body = new StreamContent(upload);
            using var request = Signed(HttpMethod.Put, key, hash, body);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw Failed("save", response.StatusCode);
            return key;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var request = Signed(HttpMethod.Get, key, SigV4.Sha256Hex([]));
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            response.Dispose();
            request.Dispose();
            throw new FileNotFoundException("The file is not in the storage.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            request.Dispose();
            throw Failed("read", status);
        }

        return new OwnedStream(await response.Content.ReadAsStreamAsync(ct), response, request);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var request = Signed(HttpMethod.Delete, key, SigV4.Sha256Hex([]));
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw Failed("delete", response.StatusCode);
    }

    /// <summary>A response body that lets go of the response (and request) when the reader is done with it.</summary>
    internal sealed class OwnedStream(Stream inner, IDisposable response, IDisposable request) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                request.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
