using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Qahwa.Api.Tests;

public class AzureBlobStorageTests
{
    // Azurite's published development account.
    private const string DevAccount = "devstoreaccount1";
    private const string DevKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Calls { get; } = [];

        public List<byte[]> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            Bodies.Add(request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return answer(request);
        }
    }

    private static AzureBlobOptions Options(Action<AzureBlobOptions>? change = null)
    {
        var options = new AzureBlobOptions { Account = "myaccount", Key = Convert.ToBase64String(Encoding.UTF8.GetBytes("not-a-real-key-just-bytes")), Container = "qahwa-files" };
        change?.Invoke(options);
        return options;
    }

    [Fact]
    public void The_string_to_sign_has_the_documented_layout()
    {
        var headers = new Dictionary<string, string> { ["x-ms-version"] = "2021-08-06", ["x-ms-date"] = "Fri, 26 Jun 2015 23:39:12 GMT", ["x-ms-blob-type"] = "BlockBlob" };

        var put = AzureSharedKey.StringToSign("PUT", 11, string.Empty, headers, "myaccount", "/files/ab/abcd");
        var get = AzureSharedKey.StringToSign("GET", 0, string.Empty, new Dictionary<string, string> { ["x-ms-date"] = "d", ["x-ms-version"] = "v" }, "myaccount", "/files/ab/abcd");

        // The verb, then 12 standard-header lines: encoding, language, length, MD5, type, date, four If-* and range (all empty here but the length); then the sorted x-ms headers and /account/path.
        Assert.Equal("PUT\n\n\n11\n\n\n\n\n\n\n\n\nx-ms-blob-type:BlockBlob\nx-ms-date:Fri, 26 Jun 2015 23:39:12 GMT\nx-ms-version:2021-08-06\n/myaccount/files/ab/abcd", put);
        Assert.Equal("GET\n\n\n\n\n\n\n\n\n\n\n\nx-ms-date:d\nx-ms-version:v\n/myaccount/files/ab/abcd", get); // a zero length is signed as empty
    }

    [Fact]
    public void The_authorization_is_an_hmac_of_the_string_with_the_decoded_key()
    {
        const string text = "GET\n\nx-ms-date:d\n/myaccount/c/b";
        var expected = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(DevKey), Encoding.UTF8.GetBytes(text)));

        Assert.Equal($"SharedKey myaccount:{expected}", AzureSharedKey.Authorization("myaccount", DevKey, text));
    }

    [Fact]
    public async Task A_save_puts_a_block_blob_under_the_prefix_and_returns_its_key()
    {
        var handler = new Recorder(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var storage = new AzureBlobStorage(Options(o => o.Prefix = "tests/"), new HttpClient(handler), new FixedClock());

        var key = await storage.SaveAsync(new MemoryStream("hello azure"u8.ToArray()));

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal($"https://myaccount.blob.core.windows.net/qahwa-files/tests/{key[..2]}/{key}", call.RequestUri!.ToString());
        Assert.Equal("BlockBlob", call.Headers.GetValues("x-ms-blob-type").Single());
        Assert.Equal(AzureSharedKey.Version, call.Headers.GetValues("x-ms-version").Single());
        Assert.StartsWith("SharedKey myaccount:", call.Headers.GetValues("Authorization").Single());
        Assert.Equal("hello azure"u8.ToArray(), handler.Bodies[0]);
        Assert.Matches("^[0-9a-f]{32}$", key);
    }

    [Fact]
    public async Task A_missing_file_is_not_found_a_failure_is_an_error_and_deleting_what_is_gone_is_fine()
    {
        var status = HttpStatusCode.NotFound;
        var storage = new AzureBlobStorage(Options(), new HttpClient(new Recorder(_ => new HttpResponseMessage(status))), new FixedClock());
        var key = new string('a', 32);

        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync(key));
        await storage.DeleteAsync(key);
        status = HttpStatusCode.Forbidden;
        await Assert.ThrowsAsync<IOException>(() => storage.OpenReadAsync(key));
        await Assert.ThrowsAsync<IOException>(() => storage.DeleteAsync(key));
        await Assert.ThrowsAsync<IOException>(() => storage.SaveAsync(new MemoryStream([1])));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync("../../etc/passwd"));
    }

    [Theory]
    [InlineData("account", "BAD")]
    [InlineData("key", "!!!")]
    [InlineData("container", "Bad_Name")]
    [InlineData("container", "a--b")]
    [InlineData("prefix", "../up")]
    [InlineData("prefix", "/abs")]
    [InlineData("url", "ftp://x")]
    [InlineData("url", "http://example.com")]
    public void Wrong_settings_stop_the_server_at_start_up(string what, string value)
    {
        var options = Options(o =>
        {
            switch (what)
            {
                case "account": o.Account = value; break;
                case "key": o.Key = value; break;
                case "container": o.Container = value; break;
                case "prefix": o.Prefix = value; break;
                default: o.ServiceUrl = value; break;
            }
        });

        Assert.Throws<InvalidOperationException>(() => new AzureBlobStorage(options));
    }

    [Fact]
    public void A_service_url_may_hold_the_account_as_a_path_as_azurite_does()
    {
        var options = Options(o => o.ServiceUrl = "http://127.0.0.1:10000/devstoreaccount1");

        options.Validate();
        Assert.Equal("http://127.0.0.1:10000/devstoreaccount1/", options.BaseUri.ToString());
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    }

    // ---- a real Azure Blob service or Azurite (set QAHWA_TEST_AZURE_URL; _ACCOUNT, _KEY and _CONTAINER default to Azurite's) ----

    [SkippableFact]
    public async Task A_real_service_accepts_the_signature_and_round_trips_a_file()
    {
        var url = Environment.GetEnvironmentVariable("QAHWA_TEST_AZURE_URL");
        Skip.If(string.IsNullOrEmpty(url), "Set QAHWA_TEST_AZURE_URL (for Azurite: http://127.0.0.1:10000/devstoreaccount1) to run against a real service.");
        var options = new AzureBlobOptions
        {
            ServiceUrl = url!,
            Account = Environment.GetEnvironmentVariable("QAHWA_TEST_AZURE_ACCOUNT") ?? DevAccount,
            Key = Environment.GetEnvironmentVariable("QAHWA_TEST_AZURE_KEY") ?? DevKey,
            Container = Environment.GetEnvironmentVariable("QAHWA_TEST_AZURE_CONTAINER") ?? "qahwa-test",
            Prefix = "tests",
        };
        using var http = new HttpClient();
        await EnsureContainerAsync(http, options);
        var storage = new AzureBlobStorage(options, http);
        var content = new byte[300_000];
        Random.Shared.NextBytes(content);

        var key = await storage.SaveAsync(new MemoryStream(content));
        await using (var read = await storage.OpenReadAsync(key))
        {
            var copy = new MemoryStream();
            await read.CopyToAsync(copy);
            Assert.Equal(content, copy.ToArray());
        }

        await storage.DeleteAsync(key);
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync(key));
        await storage.DeleteAsync(key);

        var wrongKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var wrong = new AzureBlobStorage(new AzureBlobOptions { ServiceUrl = options.ServiceUrl, Account = options.Account, Key = wrongKey, Container = options.Container }, http);
        await Assert.ThrowsAsync<IOException>(() => wrong.SaveAsync(new MemoryStream([1])));
    }

    private static async Task EnsureContainerAsync(HttpClient http, AzureBlobOptions options)
    {
        var uri = new Uri(options.BaseUri, options.Container + "?restype=container");
        var headers = new Dictionary<string, string> { ["x-ms-date"] = AzureSharedKey.Date(DateTimeOffset.UtcNow), ["x-ms-version"] = AzureSharedKey.Version };
        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        // The canonical resource carries the query parameters, one per line.
        var toSign = AzureSharedKey.StringToSign("PUT", 0, string.Empty, headers, options.Account, uri.AbsolutePath + "\nrestype:container");
        request.Headers.TryAddWithoutValidation("Authorization", AzureSharedKey.Authorization(options.Account, options.Key, toSign));
        request.Content = new ByteArrayContent([]);
        using var response = await http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict, $"Could not make the test container: {(int)response.StatusCode}.");
    }
}
