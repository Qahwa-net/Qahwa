using System.Text.Json;
using Qahwa.Core;
using Qahwa.Crm;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Crm.Tests;

public class CrmAppTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["CRM User", UserContext.AllRole]);
    private static readonly UserContext Bob = new("bob@example.com", ["CRM User", UserContext.AllRole]);
    private static readonly UserContext Boss = new("boss@example.com", ["CRM Manager", UserContext.AllRole]);
    private static readonly UserContext Outsider = new("eve@example.com", ["Other", UserContext.AllRole]);

    public CrmAppTests()
    {
        var app = new CrmApp();
        foreach (var file in Directory.GetFiles(app.DocTypesPath!, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            _engine.Registry.Add(DocTypeLoader.Parse(File.ReadAllText(file)));
        _engine.Registry.ValidateReferences();
        _engine.AppCatalog.Add(app);
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private Task<Dictionary<string, object?>> LeadAsync(UserContext by, string name = "Sara", string? company = "Acme") =>
        _engine.Documents.InsertAsync(CrmApp.Lead, Values(("lead_name", name), ("company", company)), by);

    private Task<Dictionary<string, object?>> OpportunityAsync(UserContext by, params (string Key, object? Value)[] values) =>
        _engine.Documents.InsertAsync(CrmApp.Opportunity, Values([("title", "Deal"), .. values]), by);

    [Fact]
    public void The_app_has_a_valid_name_and_loads_its_two_doctypes()
    {
        var app = new CrmApp();
        Assert.Matches("^[a-z][a-z0-9_]{1,30}$", app.Name);
        Assert.True(_engine.Registry.TryGet(CrmApp.Lead, out _));
        Assert.True(_engine.Registry.TryGet(CrmApp.Opportunity, out _));
    }

    [Fact]
    public async Task A_lead_starts_new_and_lowercases_its_email()
    {
        var lead = await _engine.Documents.InsertAsync(CrmApp.Lead, Values(("lead_name", "Sara"), ("email", "Sara@Example.COM")), Ann);

        Assert.Equal("New", lead["status"]);
        Assert.Equal("sara@example.com", lead["email"]);
        Assert.StartsWith("LEAD-", (string)lead["name"]!);
    }

    [Fact]
    public async Task A_lost_lead_or_opportunity_must_say_why()
    {
        var lead = await LeadAsync(Ann);
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync(CrmApp.Lead, (string)lead["name"]!, Values(("status", "Lost")), Ann));
        Assert.Contains(ex.Errors, e => e.Field == "lost_reason");
        await _engine.Documents.UpdateAsync(CrmApp.Lead, (string)lead["name"]!, Values(("status", "Lost"), ("lost_reason", "No budget")), Ann);

        var opportunity = await OpportunityAsync(Ann);
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync(CrmApp.Opportunity, (string)opportunity["name"]!, Values(("stage", "Lost")), Ann));
    }

    [Fact]
    public async Task A_stage_sets_its_probability_and_the_weighted_amount_follows()
    {
        var opportunity = await OpportunityAsync(Ann, ("amount", 1000m));
        var name = (string)opportunity["name"]!;
        Assert.Equal("Prospecting", opportunity["stage"]);
        Assert.Equal(10m, opportunity["probability"]);
        Assert.Equal(100m, opportunity["weighted_amount"]);

        var negotiating = await _engine.Documents.UpdateAsync(CrmApp.Opportunity, name, Values(("stage", "Negotiation")), Ann);
        Assert.Equal(70m, negotiating["probability"]);
        Assert.Equal(700m, negotiating["weighted_amount"]);

        var won = await _engine.Documents.UpdateAsync(CrmApp.Opportunity, name, Values(("stage", "Won")), Ann);
        Assert.Equal(100m, won["probability"]);
        Assert.Equal(1000m, won["weighted_amount"]);

        var lost = await _engine.Documents.UpdateAsync(CrmApp.Opportunity, name, Values(("stage", "Lost"), ("lost_reason", "Went with a competitor")), Ann);
        Assert.Equal(0m, lost["probability"]);
        Assert.Equal(0m, lost["weighted_amount"]);
    }

    [Fact]
    public async Task A_probability_the_person_sets_with_the_move_is_kept_but_the_weighted_amount_follows_it()
    {
        var opportunity = await OpportunityAsync(Ann, ("amount", 200m));

        var moved = await _engine.Documents.UpdateAsync(CrmApp.Opportunity, (string)opportunity["name"]!, Values(("stage", "Proposal"), ("probability", 55m)), Ann);

        Assert.Equal(55m, moved["probability"]);
        Assert.Equal(110m, moved["weighted_amount"]);
    }

    [Fact]
    public async Task A_probability_outside_0_to_100_is_refused()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => OpportunityAsync(Ann, ("probability", 140m)));
        Assert.Contains(ex.Errors, e => e.Field == "probability");
    }

    [Fact]
    public async Task Converting_a_lead_makes_an_opportunity_and_links_both_ways()
    {
        var lead = await LeadAsync(Ann, "Sara", "Acme");

        var result = await _engine.Methods.InvokeAsync("qahwa_crm.convert_lead", Args(new { lead = lead["name"], amount = 5000, expected_close = "2026-12-01" }), Ann, false, default);

        var opportunityName = (string)result!.GetType().GetProperty("opportunity")!.GetValue(result)!;
        var opportunity = await _engine.Documents.GetAsync(CrmApp.Opportunity, opportunityName, Ann);
        Assert.Equal("Acme opportunity", opportunity["title"]);
        Assert.Equal(lead["name"], opportunity["lead"]);
        Assert.Equal("Acme", opportunity["company"]); // fetched from the lead
        Assert.Equal(5000m, opportunity["amount"]);
        Assert.Equal(new DateOnly(2026, 12, 1), opportunity["expected_close"]);
        var converted = await _engine.Documents.GetAsync(CrmApp.Lead, (string)lead["name"]!, Ann);
        Assert.Equal("Converted", converted["status"]);
        Assert.Equal(opportunityName, converted["opportunity"]);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _engine.Methods.InvokeAsync("qahwa_crm.convert_lead", Args(new { lead = lead["name"] }), Ann, false, default));
    }

    [Fact]
    public async Task A_converted_lead_stays_converted_and_only_conversion_can_convert()
    {
        var lead = await LeadAsync(Ann);
        var name = (string)lead["name"]!;
        var direct = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync(CrmApp.Lead, name, Values(("status", "Converted")), Ann));
        Assert.Contains("Convert to opportunity", direct.Errors[0].Message);

        await _engine.Methods.InvokeAsync("qahwa_crm.convert_lead", Args(new { lead = name }), Ann, false, default);
        var reopen = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync(CrmApp.Lead, name, Values(("status", "Qualified")), Ann));
        Assert.Contains("cannot be reopened", reopen.Errors[0].Message);
    }

    [Fact]
    public async Task A_lost_lead_cannot_be_converted()
    {
        var lead = await LeadAsync(Ann);
        await _engine.Documents.UpdateAsync(CrmApp.Lead, (string)lead["name"]!, Values(("status", "Lost"), ("lost_reason", "Never replied")), Ann);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _engine.Methods.InvokeAsync("qahwa_crm.convert_lead", Args(new { lead = lead["name"] }), Ann, false, default));
    }

    [Fact]
    public async Task Salespeople_work_on_their_own_and_managers_see_everything_and_outsiders_nothing()
    {
        await LeadAsync(Ann, "Ann's lead");
        await LeadAsync(Bob, "Bob's lead");

        Assert.Equal(["Ann's lead"], (await _engine.Documents.ListAsync(CrmApp.Lead, new ListRequest { Fields = ["lead_name"], Limit = 10 }, Ann)).Data.Select(r => (string)r["lead_name"]!));
        Assert.Equal(2, (await _engine.Documents.ListAsync(CrmApp.Lead, new ListRequest { Fields = ["lead_name"], Limit = 10 }, Boss)).Data.Count);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => LeadAsync(Outsider));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.DeleteAsync(CrmApp.Lead, "LEAD-00001", Ann));
    }

    [Fact]
    public async Task The_pipeline_report_adds_up_each_stage()
    {
        await OpportunityAsync(Ann, ("amount", 1000m));                          // Prospecting 10 %
        await OpportunityAsync(Ann, ("amount", 3000m), ("stage", "Negotiation")); // 70 %
        await OpportunityAsync(Bob, ("amount", 500m), ("stage", "Won"));          // 100 %

        var result = await _engine.ScriptReports.RunAsync("qahwa_crm.pipeline", null, Boss);

        decimal Cell(string stage, string key) => Convert.ToDecimal(result.Rows.Single(r => (string)r["stage"]! == stage)[key]);
        Assert.Equal(["Prospecting", "Proposal", "Negotiation", "Won", "Lost"], result.Rows.Select(r => (string)r["stage"]!));
        Assert.Equal(1m, Cell("Prospecting", "count"));
        Assert.Equal(3000m, Cell("Negotiation", "amount"));
        Assert.Equal(2100m, Cell("Negotiation", "weighted"));
        Assert.Equal(500m, Cell("Won", "weighted"));
        Assert.Equal(0m, Cell("Lost", "count"));
    }
}
