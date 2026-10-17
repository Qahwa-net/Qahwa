using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ScriptReportTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Vic = new("vic@example.com", ["Viewer"]);

    public ScriptReportTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Sale", "autoname": "field:title", "fields": [ { "name": "title", "type": "Data" }, { "name": "amount", "type": "Currency" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private sealed class ReportApp(Action<IAppBuilder> configure) : IQahwaApp
    {
        public string Name => "shop";
        public string Version => "1.0.0";
        public void Configure(IAppBuilder app) => configure(app);
    }

    private void Install(Action<IAppBuilder> configure) => _engine.AppCatalog.Add(new ReportApp(configure));

    private static ScriptReportData Table(params (string Key, object? Value)[][] rows) =>
        new([new ScriptReportColumn("title", "Title"), new ScriptReportColumn("amount", "Amount", "Currency")],
            rows.Select(r => (IReadOnlyDictionary<string, object?>)r.ToDictionary(c => c.Key, c => c.Value)).ToList());

    [Fact]
    public async Task A_report_written_by_an_app_reads_documents_as_the_person_and_returns_a_table()
    {
        await _engine.Documents.InsertAsync("Sale", Values(("title", "a"), ("amount", 10m)), Ann);
        Install(a => a.ScriptReport("totals", "Sales totals", async ctx =>
        {
            var rows = (await ctx.Documents.ListAsync("Sale", new ListRequest { Fields = ["title", "amount"], Limit = 100 }, ctx.User)).Data;
            return new ScriptReportData([new("title", "Title"), new("amount", "Amount", "Currency")], rows.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList());
        }));

        var result = await _engine.ScriptReports.RunAsync("shop.totals", null, Ann);

        Assert.Equal("Sales totals", result.Title);
        Assert.Equal(["Title", "Amount"], result.Columns.Select(c => c.Label));
        Assert.Equal(10m, result.Rows.Single()["amount"]);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.ScriptReports.RunAsync("shop.totals", null, Vic)); // the person's own rights decide
    }

    [Fact]
    public async Task Only_declared_filters_reach_the_report_and_roles_decide_who_sees_it()
    {
        IReadOnlyDictionary<string, string>? seen = null;
        Install(a => a.ScriptReport("secret", "Secret", ctx => { seen = ctx.Filters; return Task.FromResult(Table()); },
            new ScriptReportOptions { Roles = ["Clerk"], Filters = ["region"] }));

        await _engine.ScriptReports.RunAsync("shop.secret", new Dictionary<string, string> { ["region"] = new string('x', 500), ["evil"] = "1" }, Ann);

        Assert.Equal(["region"], seen!.Keys);
        Assert.Equal(ScriptReportService.MaxFilterLength, seen["region"].Length);
        Assert.Single(_engine.ScriptReports.List(Ann));
        Assert.Empty(_engine.ScriptReports.List(Vic));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ScriptReports.RunAsync("shop.secret", null, Vic)); // looks like it does not exist
        Assert.Single(_engine.ScriptReports.List(TestUsers.Admin));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.ScriptReports.RunAsync("shop.secret", null, UserContext.Anonymous));
    }

    [Fact]
    public async Task A_failing_report_is_recorded_and_the_person_sees_only_a_plain_message()
    {
        Install(a => a.ScriptReport("broken", "Broken", _ => throw new InvalidOperationException("password=hunter2 at db-host-7")));

        var error = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.ScriptReports.RunAsync("shop.broken", null, Ann));

        Assert.DoesNotContain("hunter2", string.Join(" ", error.Errors.Select(e => e.Message)));
        Assert.Single(await _engine.Repository.ListAsync(_engine.Registry.Get("Error Log"), new ListQuery { Limit = 5 }));
    }

    [Fact]
    public async Task Size_limits_hold_and_odd_cells_become_plain_data()
    {
        Install(a => a.ScriptReport("big", "Big", _ => Task.FromResult(new ScriptReportData(
            [new("n", "N"), new("when", "When"), new("blob", "Blob")],
            Enumerable.Range(0, ScriptReportService.MaxRows + 5).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            { ["n"] = i, ["when"] = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), ["blob"] = new string('z', 5000), ["extra"] = "dropped" }).ToList()))));

        var result = await _engine.ScriptReports.RunAsync("shop.big", null, Ann);

        Assert.Equal(ScriptReportService.MaxRows, result.Rows.Count);
        Assert.True(result.Truncated);
        Assert.Equal("2026-01-02T03:04:05Z", result.Rows[0]["when"]);
        Assert.Equal(ScriptReportService.MaxCell, ((string)result.Rows[0]["blob"]!).Length);
        Assert.False(result.Rows[0].ContainsKey("extra"));
    }

    [Fact]
    public async Task Bad_definitions_and_bad_results_are_refused_and_each_run_is_audited()
    {
        Assert.Throws<InvalidOperationException>(() => new TestEngine().AppCatalog.Add(new ReportApp(a => a.ScriptReport("Bad Name", "t", _ => Task.FromResult(Table()))))); // a lower-case name is required
        Install(a => a.ScriptReport("twocols", "Two", _ => Task.FromResult(new ScriptReportData([new("a", "A"), new("a", "Again")], []))).ScriptReport("ok", "Fine", _ => Task.FromResult(Table([("title", "x"), ("amount", 1)]))));

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.ScriptReports.RunAsync("shop.twocols", null, Ann));
        await _engine.ScriptReports.RunAsync("shop.ok", null, Ann);

        var log = await _engine.Repository.ListAsync(_engine.Registry.Get("Access Log"), new ListQuery { Limit = 5 });
        Assert.Equal("Report shop.ok", Assert.Single(log)["ref_doctype"]);
    }
}
