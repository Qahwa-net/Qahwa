using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class PreparedReportTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk", UserContext.AllRole]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk", UserContext.AllRole]);

    public PreparedReportTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Sale", "fields": [
                { "name": "customer", "type": "Data", "label": "Customer" }, { "name": "amount", "type": "Currency", "label": "Amount" },
                { "name": "margin", "type": "Currency", "label": "Margin", "permLevel": 1 } ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true, "report": true, "export": true, "ifOwner": true } ] }
            """));
    }

    private async Task PersonAsync(UserContext person)
    {
        if (!await _engine.Repository.ExistsAsync(_engine.Registry.Get("Role"), "Clerk"))
            await _engine.Documents.InsertAsync("Role", Values(("role_name", "Clerk")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", person.User), ("enabled", true)), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Has Role", Values(("user", person.User), ("role", "Clerk")), TestUsers.Admin);
    }

    private async Task<string> ReportAsync(UserContext by, bool isPublic = false)
    {
        await _engine.Documents.InsertAsync("Sale", Values(("customer", "Acme"), ("amount", 100m)), Ann);
        _engine.Clock.Advance(TimeSpan.FromSeconds(1)); // rows sort by creation, so give each its own moment
        await _engine.Documents.InsertAsync("Sale", Values(("customer", "Globex"), ("amount", 300m)), Ann);
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Documents.InsertAsync("Sale", Values(("customer", "Bob's own"), ("amount", 7m)), Bob);
        return (await _engine.Reports.CreateAsync(new ReportRequest("Sales", isPublic,
            new ReportDefinition { DocType = "Sale", Columns = ["customer", "amount"], Limit = 100, OrderBy = "creation", Descending = false }), by)).Id;
    }

    [Fact]
    public async Task A_report_is_built_in_the_background_and_can_be_opened_later()
    {
        await PersonAsync(Ann);
        var report = await ReportAsync(Ann);

        var queued = await _engine.PreparedReports.RequestAsync(report, Ann);
        Assert.Equal("Queued", queued.Status);

        Assert.Equal((1, 0), await _engine.PreparedReports.RunDueAsync());

        var result = await _engine.PreparedReports.GetAsync(queued.Id, Ann);
        Assert.Equal("Done", result.Info.Status);
        Assert.Equal(2, result.Info.Rows); // Ann sees her own two sales, not Bob's
        Assert.Equal(["Customer", "Amount"], result.Columns);
        Assert.Equal(["Acme", "Globex"], result.Rows.Select(r => (string)r["Customer"]!));
        Assert.Equal("Done", Assert.Single(await _engine.PreparedReports.ListAsync(report, Ann)).Status);
    }

    [Fact]
    public async Task It_is_built_with_the_permissions_of_the_person_who_asked()
    {
        await PersonAsync(Ann);
        await PersonAsync(Bob);
        var report = await ReportAsync(Ann, isPublic: true);

        var forBob = await _engine.PreparedReports.RequestAsync(report, Bob);
        await _engine.PreparedReports.RunDueAsync();

        var result = await _engine.PreparedReports.GetAsync(forBob.Id, Bob);
        Assert.Equal(["Bob's own"], result.Rows.Select(r => (string)r["Customer"]!));
    }

    [Fact]
    public async Task Asking_again_while_one_is_waiting_gives_that_one()
    {
        await PersonAsync(Ann);
        var report = await ReportAsync(Ann);

        var first = await _engine.PreparedReports.RequestAsync(report, Ann);
        var second = await _engine.PreparedReports.RequestAsync(report, Ann);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task A_person_can_have_only_a_few_waiting_at_once()
    {
        await PersonAsync(Ann);
        var ids = new List<string>();
        for (var i = 0; i < PreparedReportService.MaxActivePerUser; i++)
            ids.Add(await ReportAsync(Ann));
        foreach (var id in ids)
            await _engine.PreparedReports.RequestAsync(id, Ann);

        await Assert.ThrowsAsync<ConflictException>(async () => await _engine.PreparedReports.RequestAsync(await ReportAsync(Ann), Ann));
    }

    [Fact]
    public async Task Nobody_else_can_open_or_delete_it_and_a_report_you_cannot_open_cannot_be_prepared()
    {
        await PersonAsync(Ann);
        await PersonAsync(Bob);
        var report = await ReportAsync(Ann); // private to Ann
        var mine = await _engine.PreparedReports.RequestAsync(report, Ann);

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.PreparedReports.GetAsync(mine.Id, Bob));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.PreparedReports.DeleteAsync(mine.Id, Bob));
        await Assert.ThrowsAnyAsync<QahwaException>(() => _engine.PreparedReports.RequestAsync(report, Bob));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.PreparedReports.RequestAsync(report, UserContext.Anonymous));
    }

    [Fact]
    public async Task A_failure_is_kept_with_its_reason_and_the_person_can_delete_a_finished_one()
    {
        await PersonAsync(Ann);
        var report = await ReportAsync(Ann);
        var queued = await _engine.PreparedReports.RequestAsync(report, Ann);
        await _engine.Reports.DeleteAsync(report, Ann); // the report is gone by the time it runs

        Assert.Equal((0, 1), await _engine.PreparedReports.RunDueAsync());

        var failed = (await _engine.PreparedReports.GetAsync(queued.Id, Ann)).Info;
        Assert.Equal("Failed", failed.Status);
        Assert.False(string.IsNullOrEmpty(failed.Message));
        await _engine.PreparedReports.DeleteAsync(queued.Id, Ann);
        Assert.Empty(await _engine.PreparedReports.ListAsync(report, Ann));
    }

    [Fact]
    public async Task A_run_that_stopped_reporting_is_marked_failed_and_old_results_are_deleted()
    {
        await PersonAsync(Ann);
        var report = await ReportAsync(Ann);
        var queued = await _engine.PreparedReports.RequestAsync(report, Ann);
        var meta = _engine.Registry.Get("Prepared Report");
        var row = (await _engine.Repository.GetAsync(meta, queued.Id))!;
        await _engine.Repository.UpdateAsync(meta, queued.Id, new Dictionary<string, object?> { ["status"] = "Running", ["modified"] = _engine.Clock.GetUtcNow().UtcDateTime }, (DateTime)row["modified"]!, null);

        _engine.Clock.Advance(PreparedReportService.StaleAfter + TimeSpan.FromMinutes(1));
        Assert.Equal((0, 1), await _engine.PreparedReports.RunDueAsync());
        Assert.Equal("Failed", (await _engine.PreparedReports.GetAsync(queued.Id, Ann)).Info.Status);

        _engine.Clock.Advance(PreparedReportService.KeepFor + TimeSpan.FromDays(1));
        await _engine.PreparedReports.RunDueAsync();
        Assert.Empty(await _engine.PreparedReports.ListAsync(report, Ann));
    }

    [Fact]
    public async Task A_list_report_needs_the_export_right_like_its_download()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Memo Line", "fields": [ { "name": "text", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "report": true } ] }
            """));
        await PersonAsync(Ann);
        await _engine.Documents.InsertAsync("Memo Line", Values(("text", "one")), Ann);
        var report = (await _engine.Reports.CreateAsync(new ReportRequest("Memos", false, new ReportDefinition { DocType = "Memo Line", Columns = ["text"], Limit = 10 }), Ann)).Id;
        var queued = await _engine.PreparedReports.RequestAsync(report, Ann);

        Assert.Equal((0, 1), await _engine.PreparedReports.RunDueAsync());

        var failed = (await _engine.PreparedReports.GetAsync(queued.Id, Ann)).Info;
        Assert.Equal("Failed", failed.Status);
        Assert.Contains("export permission", failed.Message);
    }
}
