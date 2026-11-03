using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Qahwa.Core;

/// <summary>Settings for keeping attachments in an Azure Blob Storage container (Qahwa:Files:Azure).</summary>
public sealed class AzureBlobOptions
{
    /// <summary>The storage account name.</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>One of the account's access keys (base64, as the portal shows it).</summary>
    public string Key { get; set; } = string.Empty;

    public string Container { get; set; } = string.Empty;

    /// <summary>The address of the blob service; empty means https://&lt;account&gt;.blob.core.windows.net. Set it for Azurite or a sovereign cloud (it may hold the account as a path, as Azurite does).</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>A folder inside the container to keep the files in (letters, digits, - _ . /), or empty.</summary>
    public string Prefix { get; set; } = string.Empty;

    private static readonly Regex AccountName = new("^[a-z0-9]{3,24}$", RegexOptions.Compiled);
    private static readonly Regex ContainerName = new("^[a-z0-9]([a-z0-9]|-(?!-)){1,61}[a-z0-9]$", RegexOptions.Compiled);
    private static readonly Regex PrefixName = new("^[A-Za-z0-9._/-]{0,200}$", RegexOptions.Compiled);

    public Uri BaseUri => new((ServiceUrl.Length > 0 ? ServiceUrl : $"https://{Account}.blob.core.windows.net").TrimEnd('/') + "/");

    /// <exception cref="InvalidOperationException">What is wrong with the settings, so the server stops at start-up instead of failing on the first upload.</exception>
    public void Validate()
    {
        if (!AccountName.IsMatch(Account))
            throw new InvalidOperationException("Qahwa:Files:Azure:Account must be the storage account name (3 to 24 lower-case letters and digits).");
        try
        {
            if (Convert.FromBase64String(Key ?? string.Empty).Length == 0)
                throw new FormatException();
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Qahwa:Files:Azure:Key must be an account access key (base64).");
        }

        if (!ContainerName.IsMatch(Container))
            throw new InvalidOperationException("Qahwa:Files:Azure:Container must be a container name (3 to 63 lower-case letters, digits and single hyphens).");
        if (ServiceUrl.Length > 0)
        {
            if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.Query.Length > 0)
                throw new InvalidOperationException("Qahwa:Files:Azure:ServiceUrl must be the address of the blob service, such as https://myaccount.blob.core.windows.net.");
            if (uri.Scheme == "http" && !uri.IsLoopback)
                throw new InvalidOperationException("Qahwa:Files:Azure:ServiceUrl must use https (plain http is only allowed for localhost, for testing).");
        }

        if (!PrefixName.IsMatch(Prefix) || Prefix.Contains("..", StringComparison.Ordinal) || Prefix.StartsWith('/'))
            throw new InvalidOperationException("Qahwa:Files:Azure:Prefix may only hold letters, digits and - _ . / and must not start with / or contain ..");
    }
}

/// <summary>
/// Azure Storage "Shared Key" authorization for Blob requests, written out on System.Security.Cryptography so keeping files in Azure
/// needs no SDK. The string to sign is the documented one: the verb, the standard headers (empty when absent; the length is empty for
/// zero), the sorted <c>x-ms-</c> headers and the canonical resource (<c>/account/path</c>).
/// </summary>
public static class AzureSharedKey
{
    public const string Version = "2021-08-06";

    public static string StringToSign(string method, long contentLength, string contentType, IReadOnlyDictionary<string, string> msHeaders, string account, string path)
    {
        var canonicalHeaders = string.Concat(msHeaders.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key.ToLowerInvariant()}:{h.Value.Trim()}\n"));
        // verb, Content-Encoding, Content-Language, Content-Length, Content-MD5, Content-Type, Date (x-ms-date is used instead), If-*, Range
        return $"{method}\n\n\n{(contentLength > 0 ? contentLength.ToString(CultureInfo.InvariantCulture) : string.Empty)}\n\n{contentType}\n\n\n\n\n\n\n{canonicalHeaders}/{account}{path}";
    }

    public static string Authorization(string account, string base64Key, string stringToSign)
    {
        using var hmac = new HMACSHA256(Convert.FromBase64String(base64Key));
        return $"SharedKey {account}:{Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)))}";
    }

    public static string Date(DateTimeOffset moment) => moment.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// Attachments in an Azure Blob Storage container (<c>Qahwa:Files:Provider = azure</c>). Files are block blobs named
/// <c>[prefix/]ab/abcdef…</c> (the key is 32 hex characters and the first two make a folder, as on disk and in S3). A file is copied to
/// a temporary file first so its length is known, then sent as one Put Blob; reads stream the body.
/// </summary>
public sealed partial class AzureBlobStorage : IFileStorage
{
    private readonly AzureBlobOptions _options;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    public AzureBlobStorage(AzureBlobOptions options, HttpClient? http = null, TimeProvider? clock = null)
    {
        options.Validate();
        _options = options;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _clock = clock ?? TimeProvider.System;
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex KeyRegex();

    private HttpRequestMessage Signed(HttpMethod method, string key, HttpContent? content = null, long length = 0)
    {
        if (!KeyRegex().IsMatch(key))
            throw new ArgumentException("Not a file key.", nameof(key));
        var blob = (_options.Prefix.Length > 0 ? _options.Prefix.TrimEnd('/') + "/" : string.Empty) + key[..2] + "/" + key;
        var uri = new Uri(_options.BaseUri, _options.Container + "/" + blob);
        var headers = new Dictionary<string, string> { ["x-ms-date"] = AzureSharedKey.Date(_clock.GetUtcNow()), ["x-ms-version"] = AzureSharedKey.Version };
        if (method == HttpMethod.Put)
            headers["x-ms-blob-type"] = "BlockBlob";
        var request = new HttpRequestMessage(method, uri) { Content = content };
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        var toSign = AzureSharedKey.StringToSign(method.Method, length, string.Empty, headers, _options.Account, uri.AbsolutePath);
        request.Headers.TryAddWithoutValidation("Authorization", AzureSharedKey.Authorization(_options.Account, _options.Key, toSign));
        return request;
    }

    private static IOException Failed(string what, System.Net.HttpStatusCode status) =>
        new($"The file storage ({what}) answered {(int)status} {status}.");

    public async Task<string> SaveAsync(Stream content, CancellationToken ct = default)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var temp = Path.Combine(Path.GetTempPath(), "qahwa-azure-" + key);
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(file, ct);

            await using var upload = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            using var body = new StreamContent(upload);
            body.Headers.ContentLength = upload.Length;
            using var request = Signed(HttpMethod.Put, key, body, upload.Length);
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
        var request = Signed(HttpMethod.Get, key);
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

        return new S3FileStorage.OwnedStream(await response.Content.ReadAsStreamAsync(ct), response, request);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var request = Signed(HttpMethod.Delete, key);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw Failed("delete", response.StatusCode);
    }
}
