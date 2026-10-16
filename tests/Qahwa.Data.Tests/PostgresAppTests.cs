using System.Text.Json;
using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresAppTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private sealed class DemoApp : IQahwaApp
    {
        public string Name => "demo";
        public string Version => "2.0.0";

        public void Configure(IAppBuilder app)
        {
            app.On("Widget", DocEvent.BeforeSave, ctx =>
            {
                ctx.Doc["label"] = ((string?)ctx.Doc["label"] ?? string.Empty).ToUpperInvariant();
                return Task.CompletedTask;
            });
            app.On("Widget", DocEvent.AfterSave, async ctx => await ctx.Documents.InsertAsync("Widget Log", Values(("note", $"saved {ctx.Name}")), ctx.User, ctx.Cancellation));
            app.Fixture("Widget", Values(("label", "starter")));
            app.Method("count_widgets", async ctx => (await ctx.Documents.ListAsync("Widget", new ListRequest { Limit = 1 }, ctx.User, ctx.Cancellation)).Total);
        }
    }

    [SkippableFact]
    public async Task An_apps_hooks_fixtures_and_methods_work_on_the_real_database()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Widget", "autoname": "WD-.####", "fields": [ { "name": "label", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""));
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Widget Log", "autoname": "WL-.####", "fields": [ { "name": "note", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""));
        await db.MigrateAsync();
        db.AppCatalog.Add(new DemoApp());
        var clerk = new UserContext("c@example.com", ["Clerk"]);

        await db.AppInstaller.InstallAllAsync();
        await db.AppInstaller.InstallAllAsync();
        var made = await db.Documents.InsertAsync("Widget", Values(("label", "lower case")), clerk);

        Assert.Equal("LOWER CASE", made["label"]);
        var notes = (await db.Documents.ListAsync("Widget Log", new ListRequest { Fields = ["note"], Limit = 10 }, TestUsers.Admin)).Data.Select(r => (string)r["note"]!).ToList();
        Assert.Equal(2, notes.Count); // the fixture and the new document each made one note
        Assert.Contains($"saved {made["name"]}", notes);
        Assert.Equal(2L, await db.Methods.InvokeAsync("demo.count_widgets", JsonSerializer.SerializeToElement(new { }), clerk, false, default));
        Assert.Equal("demo", (await db.AppInstaller.ListAsync()).Single().Name);
    }
}
