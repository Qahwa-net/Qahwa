using System.Net;
using System.Text;

namespace Qahwa.Api.Tests;

public class S3FileStorageTests
{
    private sealed class Recorder(Func<HttpRequestMessage, byte[], HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Calls.Add((request, body));
            return answer(request, body);
        }
    }

    private static S3Options Options(Action<S3Options>? change = null)
    {
        var options = new S3Options { ServiceUrl = "https://s3.example.com", Bucket = "qahwa-files", Region = "eu-1", AccessKey = "AKIAEXAMPLE", SecretKey = "super-secret-value" };
        change?.Invoke(options);
        return options;
    }

    private static string Header(HttpRequestMessage request, string name) => request.Headers.GetValues(name).Single();

    // ---- the signature itself: AWS's published examples ----

    [Fact]
    public void The_signature_matches_the_AWS_get_object_example()
    {
        const string empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var authorization = SigV4.Authorization("GET", "/test.txt", string.Empty,
            new Dictionary<string, string>
            {
                ["host"] = "examplebucket.s3.amazonaws.com", ["range"] = "bytes=0-9", ["x-amz-content-sha256"] = empty, ["x-amz-date"] = "20130524T000000Z",
            },
            empty, "20130524T000000Z", "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1");

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, " +
            "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            authorization);
    }

    [Fact]
    public void The_signature_matches_the_AWS_put_object_example_including_an_encoded_name()
    {
        var hash = SigV4.Sha256Hex(Encoding.UTF8.GetBytes("Welcome to Amazon S3."));
        Assert.Equal("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072", hash);
        Assert.Equal("/test%24file.text", SigV4.EncodePath("/test$file.text"));

        var authorization = SigV4.Authorization("PUT", SigV4.EncodePath("/test$file.text"), string.Empty,
            new Dictionary<string, string>
            {
                ["date"] = "Fri, 24 May 2013 00:00:00 GMT", ["host"] = "examplebucket.s3.amazonaws.com", ["x-amz-content-sha256"] = hash,
                ["x-amz-date"] = "20130524T000000Z", ["x-amz-storage-class"] = "REDUCED_REDUNDANCY",
            },
            hash, "20130524T000000Z", "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1");

        Assert.EndsWith("Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd", authorization);
    }

    // ---- what is sent ----

    [Fact]
    public async Task A_file_is_sent_in_one_signed_request_and_its_key_is_returned()
    {
        var handler = new Recorder((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var storage = new S3FileStorage(Options(), new HttpClient(handler));
        var content = Encoding.UTF8.GetBytes("hello bucket");

        var key = await storage.SaveAsync(new MemoryStream(content));

        Assert.Matches("^[0-9a-f]{32}$", key);
        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal($"https://s3.example.com/qahwa-files/{key[..2]}/{key}", request.RequestUri!.ToString());
        Assert.Equal(content, body);
        Assert.Equal(SigV4.Sha256Hex(content), Header(request, "x-amz-content-sha256"));
        var authorization = Header(request, "Authorization");
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAEXAMPLE/", authorization);
        Assert.Contains("/eu-1/s3/aws4_request", authorization);
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", authorization);
        Assert.DoesNotContain("super-secret-value", authorization);
    }

    [Fact]
    public async Task A_prefix_and_virtual_host_style_change_the_address()
    {
        var handler = new Recorder((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var storage = new S3FileStorage(Options(o => { o.PathStyle = false; o.Prefix = "prod/files/"; }), new HttpClient(handler));

        var key = await storage.SaveAsync(new MemoryStream([1, 2, 3]));

        Assert.Equal($"https://qahwa-files.s3.example.com/prod/files/{key[..2]}/{key}", handler.Calls[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_file_is_read_and_deleted_by_key()
    {
        var handler = new Recorder((request, _) => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("stored")) }
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var storage = new S3FileStorage(Options(), new HttpClient(handler));
        const string key = "0123456789abcdef0123456789abcdef";

        await using (var stream = await storage.OpenReadAsync(key))
        using (var reader = new StreamReader(stream))
            Assert.Equal("stored", await reader.ReadToEndAsync());
        await storage.DeleteAsync(key);

        Assert.Equal([HttpMethod.Get, HttpMethod.Delete], handler.Calls.Select(c => c.Request.Method));
        Assert.All(handler.Calls, c => Assert.EndsWith($"/qahwa-files/01/{key}", c.Request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task A_missing_file_is_not_found_and_deleting_it_is_fine_but_other_failures_are_errors_without_secrets()
    {
        var status = HttpStatusCode.NotFound;
        var handler = new Recorder((_, _) => new HttpResponseMessage(status));
        var storage = new S3FileStorage(Options(), new HttpClient(handler));
        const string key = "0123456789abcdef0123456789abcdef";

        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync(key));
        await storage.DeleteAsync(key);

        status = HttpStatusCode.Forbidden;
        var ex = await Assert.ThrowsAsync<IOException>(() => storage.SaveAsync(new MemoryStream([1])));
        Assert.Contains("403", ex.Message);
        Assert.DoesNotContain("super-secret-value", ex.Message);
        Assert.DoesNotContain("AKIAEXAMPLE", ex.Message);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("short")]
    [InlineData("0123456789abcdef0123456789abcdef/../x")]
    public async Task Only_keys_the_storage_made_are_accepted(string key)
    {
        var handler = new Recorder((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var storage = new S3FileStorage(Options(), new HttpClient(handler));

        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.DeleteAsync(key));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("ServiceUrl", "not a url")]
    [InlineData("ServiceUrl", "http://s3.example.com")]
    [InlineData("ServiceUrl", "https://s3.example.com/some/path")]
    [InlineData("Bucket", "Bad_Bucket")]
    [InlineData("AccessKey", "")]
    [InlineData("SecretKey", " ")]
    [InlineData("Region", "")]
    [InlineData("Prefix", "../up")]
    [InlineData("Prefix", "/absolute")]
    public void Bad_settings_stop_the_server_at_start_up(string setting, string value)
    {
        var options = Options();
        typeof(S3Options).GetProperty(setting)!.SetValue(options, value);

        Assert.Throws<InvalidOperationException>(() => new S3FileStorage(options));
    }

    [Fact]
    public void Plain_http_is_allowed_only_for_localhost()
    {
        _ = new S3FileStorage(Options(o => o.ServiceUrl = "http://localhost:9000"));
        Assert.Throws<InvalidOperationException>(() => new S3FileStorage(Options(o => o.ServiceUrl = "http://192.168.1.5:9000")));
    }

    // ---- a real S3-compatible server (set QAHWA_TEST_S3_URL, _BUCKET, _ACCESS_KEY and _SECRET_KEY to run; skipped otherwise) ----

    [SkippableFact]
    public async Task A_real_server_accepts_the_signature_and_round_trips_a_file()
    {
        var url = Environment.GetEnvironmentVariable("QAHWA_TEST_S3_URL");
        Skip.If(string.IsNullOrEmpty(url), "Set QAHWA_TEST_S3_URL (and _BUCKET, _ACCESS_KEY, _SECRET_KEY) to run against a real S3-compatible server.");
        var options = new S3Options
        {
            ServiceUrl = url!,
            Bucket = Environment.GetEnvironmentVariable("QAHWA_TEST_S3_BUCKET")!,
            AccessKey = Environment.GetEnvironmentVariable("QAHWA_TEST_S3_ACCESS_KEY")!,
            SecretKey = Environment.GetEnvironmentVariable("QAHWA_TEST_S3_SECRET_KEY")!,
            Prefix = "tests",
        };
        using var http = new HttpClient();
        await EnsureBucketAsync(http, options);
        var storage = new S3FileStorage(options, http);
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
        await storage.DeleteAsync(key); // deleting what is gone is not an error

        var wrong = new S3FileStorage(new S3Options { ServiceUrl = options.ServiceUrl, Bucket = options.Bucket, AccessKey = options.AccessKey, SecretKey = "wrong-secret" }, http);
        await Assert.ThrowsAsync<IOException>(() => wrong.SaveAsync(new MemoryStream([1])));
    }

    private static async Task EnsureBucketAsync(HttpClient http, S3Options options)
    {
        var service = new Uri(options.ServiceUrl);
        var empty = SigV4.Sha256Hex([]);
        var date = SigV4.AmzDate(DateTimeOffset.UtcNow);
        var path = "/" + options.Bucket;
        var authorization = SigV4.Authorization("PUT", path, string.Empty,
            new Dictionary<string, string> { ["host"] = service.Authority, ["x-amz-content-sha256"] = empty, ["x-amz-date"] = date },
            empty, date, options.AccessKey, options.SecretKey, options.Region);
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(service, path));
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", empty);
        request.Headers.TryAddWithoutValidation("x-amz-date", date);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var response = await http.SendAsync(request);
        // 200 when made, 409 when it is already there.
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict, $"Could not make the test bucket: {(int)response.StatusCode}.");
    }
}
