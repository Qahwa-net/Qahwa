using System.Net;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class ApiVersionTests : IAsyncDisposable
{
    private readonly QahwaFactory factory = new();
    public ValueTask DisposeAsync() => factory.DisposeAsync();

    [Fact]
    public async Task The_versioned_address_is_the_same_api_and_answers_say_the_version()
    {
        var admin = factory.ClientFor("Administrator");
        var plain = await admin.GetAsync("/api/resource/Role");
        var versioned = await admin.GetAsync("/api/v1/resource/Role");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(HttpStatusCode.OK, versioned.StatusCode);
        Assert.Equal(await plain.Content.ReadAsStringAsync(), await versioned.Content.ReadAsStringAsync());
        Assert.Equal("1", versioned.Headers.GetValues("X-Qahwa-Api-Version").Single());
        Assert.Equal("1", plain.Headers.GetValues("X-Qahwa-Api-Version").Single());
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/v1/health")).StatusCode);
    }

    [Fact]
    public async Task Versioning_does_not_bypass_sign_in_or_open_other_versions()
    {
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/resource/Role")).StatusCode);
        Assert.False((await anonymous.GetAsync("/api/v2/health")).IsSuccessStatusCode);
        Assert.False((await anonymous.GetAsync("/api/v1health")).IsSuccessStatusCode);
        Assert.False((await anonymous.GetAsync("/api/v1/../v1/resource/Role")).IsSuccessStatusCode);
    }
}
