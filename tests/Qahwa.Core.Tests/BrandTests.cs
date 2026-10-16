using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class BrandTests
{
    private readonly TestEngine _engine = new();

    // A 1x1 PNG.
    private const string Png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static string Svg(string body) => "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(body));

    private Task SaveAsync(params (string, object?)[] values) =>
        _engine.Documents.UpdateAsync("Brand Settings", "Brand Settings", Values(values), TestUsers.SystemManager);

    private Task<Brand> BrandAsync() => BrandService.GetAsync(_engine.Registry, _engine.Repository);

    [Fact]
    public async Task Without_settings_the_product_looks_as_it_always_did()
    {
        var brand = await BrandAsync();

        Assert.Equal("Qahwa", brand.Name);
        Assert.Null(brand.Logo);
        Assert.Null(brand.Color);
        Assert.Equal("auto", brand.Theme);
    }

    [Fact]
    public async Task A_name_logo_colours_theme_and_message_are_kept()
    {
        await SaveAsync(("brand_name", "Acme Coffee"), ("brand_logo", Png), ("brand_color", "#A11D33"), ("brand_color_dark", "#FF8899"), ("default_theme", "Light"), ("login_message", "Welcome back.\nMind the cat."));

        var brand = await BrandAsync();

        Assert.Equal("Acme Coffee", brand.Name);
        Assert.Equal(Png, brand.Logo);
        Assert.Equal("#a11d33", brand.Color);
        Assert.Equal("#ff8899", brand.ColorDark);
        Assert.Equal("light", brand.Theme);
        Assert.Equal("Welcome back.\nMind the cat.", brand.LoginMessage);
    }

    [Theory]
    [InlineData("brand_name", "<script>alert(1)</script>")]
    [InlineData("brand_name", "Line\nBreak")]
    [InlineData("login_message", "Click <a href=\"https://evil.example\">here</a>")]
    [InlineData("brand_logo", "https://evil.example/logo.png")]
    [InlineData("brand_logo", "data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("brand_logo", "javascript:alert(1)")]
    [InlineData("brand_color", "red")]
    [InlineData("brand_color", "#12345")]
    [InlineData("brand_color", "url(https://evil.example)")]
    public async Task Hostile_or_malformed_values_are_refused_when_saved(string field, string value)
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync((field, value)));

        Assert.Equal("Qahwa", (await BrandAsync()).Name); // nothing was stored
    }

    [Fact]
    public async Task The_name_and_message_have_limits()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(("brand_name", new string('a', 61))));
        await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(("login_message", new string('a', 301))));
        await SaveAsync(("brand_name", new string('a', 60)), ("login_message", new string('a', 300)));
    }

    [Fact]
    public void A_picture_must_really_be_what_it_says()
    {
        Assert.Null(BrandService.LogoProblem(Png));
        Assert.NotNull(BrandService.LogoProblem("data:image/jpeg;base64," + Png["data:image/png;base64,".Length..])); // a PNG called a JPEG
        Assert.NotNull(BrandService.LogoProblem("data:image/png;base64," + Convert.ToBase64String("<html></html>"u8.ToArray())));
        Assert.NotNull(BrandService.LogoProblem("data:image/png;base64,!!!not base64!!!"));
        Assert.NotNull(BrandService.LogoProblem("data:image/png;base64,"));
    }

    [Fact]
    public void A_large_picture_is_refused()
    {
        var big = new byte[BrandService.MaxLogoBytes + 1];
        big[0] = 0x89; big[1] = 0x50; big[2] = 0x4E; big[3] = 0x47;

        Assert.Contains("too large", BrandService.LogoProblem("data:image/png;base64," + Convert.ToBase64String(big)));
        Assert.Contains("too large", BrandService.LogoProblem("data:image/png;base64," + new string('A', 500_000)));
    }

    [Fact]
    public void A_plain_svg_is_welcome_and_an_active_one_is_not()
    {
        Assert.Null(BrandService.LogoProblem(Svg("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><circle cx=\"5\" cy=\"5\" r=\"4\" fill=\"#6f4e37\"/></svg>")));
        Assert.Null(BrandService.LogoProblem(Svg("<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"1\" height=\"1\"/></svg>")));

        foreach (var evil in new[]
                 {
                     "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>",
                     "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>",
                     "<svg xmlns=\"http://www.w3.org/2000/svg\"><a href=\"https://evil.example\"><rect/></a></svg>",
                     "<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://evil.example/x.png\"/></svg>",
                     "<svg xmlns=\"http://www.w3.org/2000/svg\"><foreignObject><div/></foreignObject></svg>",
                     "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect style=\"fill:url(https://evil.example/x)\"/></svg>",
                     "<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg xmlns=\"http://www.w3.org/2000/svg\">&x;</svg>",
                     "<html><svg/></html>",
                     "not an svg",
                 })
            Assert.NotNull(BrandService.LogoProblem(Svg(evil)));
    }

    [Fact]
    public void Unusable_stored_values_fall_back_so_the_sign_in_page_never_breaks()
    {
        var brand = BrandService.From(new Dictionary<string, object?>
        {
            ["brand_name"] = "<b>x</b>", ["brand_logo"] = "http://evil.example/x.png", ["brand_color"] = "red", ["brand_color_dark"] = "#zzzzzz", ["default_theme"] = "Neon", ["login_message"] = "<script>",
        });

        Assert.Equal(BrandService.Default, brand);
    }

    [Fact]
    public async Task Only_a_system_manager_can_change_it()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.UpdateAsync("Brand Settings", "Brand Settings", Values(("brand_name", "Mine")), new UserContext("ann@example.com", ["Clerk"])));
    }
}
