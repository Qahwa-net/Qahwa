using Qahwa.Core;

namespace MyApp;

/// <summary>
/// The app. Register it in the host with <c>builder.Services.AddQahwaApp&lt;QahwaAppModule&gt;();</c>. See docs/manual/developer/08-writing-an-app.md
/// in the Qahwa repository for everything an app can add.
/// </summary>
public sealed class QahwaAppModule : IQahwaApp
{
    public string Name => "qahwa_app_id";

    public string Version => "0.1.0";

    public string Title => "DOCTYPE_PREFIX";

    public string? DocTypesPath => Path.Combine(AppContext.BaseDirectory, "DocTypes");

    public void Configure(IAppBuilder app)
    {
        // Runs before an item is saved; what it changes is checked like any other input.
        app.On("DOCTYPE_PREFIX Item", DocEvent.BeforeSave, ctx =>
        {
            if (ctx.Doc.GetValueOrDefault("title") is string title && title.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
                ctx.Refuse("That title is not allowed.");
            return Task.CompletedTask;
        });

        // POST /api/method/qahwa_app_id.item_count   (a signed-in person; Administrators always pass)
        app.Method("item_count", async ctx =>
        {
            var page = await ctx.Documents.ListAsync("DOCTYPE_PREFIX Item", new ListRequest { Fields = ["name"], Limit = 1 }, ctx.User, ctx.Cancellation);
            return new { count = page.Total };
        }, new MethodOptions { PerMinute = 30 });
    }
}
