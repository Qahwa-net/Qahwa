using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class BootHookTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    private sealed class BootApp(string name, BootHook hook) : IQahwaApp
    {
        public string Name => name;
        public string Version => "1.0.0";
        public void Configure(IAppBuilder app) => app.Boot(hook);
    }

    private void Install(string name, BootHook hook) => _engine.AppCatalog.Add(new BootApp(name, hook));

    [Fact]
    public async Task Each_app_hands_the_desk_its_own_object_for_the_person_starting_it()
    {
        Install("shop", ctx => Task.FromResult<object?>(new { greeting = "Hi " + ctx.User.User, currency = "SAR" }));
        Install("hr", _ => Task.FromResult<object?>(new[] { 1, 2, 3 }));

        var boot = await _engine.Boot.CollectAsync(Ann);

        Assert.Equal("Hi ann@example.com", boot["shop"].GetProperty("greeting").GetString());
        Assert.Equal(3, boot["hr"].GetArrayLength());
    }

    [Fact]
    public async Task A_hook_that_fails_or_answers_too_much_is_left_out_and_logged_without_stopping_the_others()
    {
        Install("bad", _ => throw new InvalidOperationException("boom"));
        Install("huge", _ => Task.FromResult<object?>(new string('x', BootService.MaxBytesPerApp + 1)));
        Install("good", _ => Task.FromResult<object?>(new { ok = true }));

        var boot = await _engine.Boot.CollectAsync(Ann);

        Assert.Equal(["good"], boot.Keys);
        var errors = await _engine.Repository.ListAsync(_engine.Registry.Get("Error Log"), new ListQuery { Limit = 10 });
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public async Task Only_signed_in_people_get_boot_data_and_an_app_may_have_one_hook()
    {
        Install("shop", _ => Task.FromResult<object?>(1));

        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Boot.CollectAsync(UserContext.Anonymous));
        Assert.Throws<InvalidOperationException>(() => _engine.AppCatalog.Add(new DoubleApp()));
    }

    private sealed class DoubleApp : IQahwaApp
    {
        public string Name => "twice";
        public string Version => "1.0.0";
        public void Configure(IAppBuilder app) => app.Boot(_ => Task.FromResult<object?>(1)).Boot(_ => Task.FromResult<object?>(2));
    }
}
