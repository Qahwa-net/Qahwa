using Qahwa.Core;

namespace Qahwa.Crm;

/// <summary>
/// Qahwa CRM-lite: the smallest useful sales tracker, built the way any app is built (see docs/manual/developer/08-writing-an-app.md).
/// Leads come in, are contacted and qualified, and become opportunities that move through stages to Won or Lost. Everything else a
/// sales team needs is already in the framework: assignments, comments and mentions, reminders ("Remind me" on a lead), events, tags,
/// email, print formats, dashboards, and the board view that shows each DocType by status.
/// </summary>
public sealed class CrmApp : IQahwaApp
{
    public const string Lead = "Crm Lead";
    public const string Opportunity = "Crm Opportunity";

    /// <summary>The probability an opportunity gets when it is first moved into a stage, if the person has not set one.</summary>
    public static readonly IReadOnlyDictionary<string, int> StageProbability =
        new Dictionary<string, int> { ["Prospecting"] = 10, ["Proposal"] = 40, ["Negotiation"] = 70, ["Won"] = 100, ["Lost"] = 0 };

    public string Name => "qahwa_crm";

    public string Version => "0.1.0";

    public string Title => "CRM";

    public string? DocTypesPath => Path.Combine(AppContext.BaseDirectory, "DocTypes");

    public void Configure(IAppBuilder app)
    {
        // The roles the DocTypes' permission rules name.
        app.Fixture("Role", new Dictionary<string, object?> { ["role_name"] = "CRM User" });
        app.Fixture("Role", new Dictionary<string, object?> { ["role_name"] = "CRM Manager" });

        // A stage carries a typical probability: moving to Won or Lost always sets 100 or 0, and a new or moved opportunity gets its
        // stage's own unless the person set one in the same save. The weighted amount is worked out here too, because hooks run after the
        // form's formulas and would otherwise leave it one save behind.
        app.On(Opportunity, DocEvent.BeforeSave, ctx =>
        {
            if (ctx.Doc.GetValueOrDefault("stage") is not string stage || !StageProbability.TryGetValue(stage, out var typical))
                return Task.CompletedTask;
            var moved = ctx.Old?.GetValueOrDefault("stage") as string != stage;
            var chosen = ctx.Doc.GetValueOrDefault("probability");
            var unchanged = ctx.Old is null ? chosen is null : Equals(ctx.Old.GetValueOrDefault("probability"), chosen);
            if (stage is "Won" or "Lost" || (moved && unchanged))
                ctx.Doc["probability"] = (decimal)typical;
            var amount = ctx.Doc.GetValueOrDefault("amount") is { } a ? Convert.ToDecimal(a, System.Globalization.CultureInfo.InvariantCulture) : 0m;
            var probability = ctx.Doc.GetValueOrDefault("probability") is { } p ? Convert.ToDecimal(p, System.Globalization.CultureInfo.InvariantCulture) : 0m;
            ctx.Doc["weighted_amount"] = Math.Round(amount * probability / 100m, 2);
            return Task.CompletedTask;
        });

        // A converted lead is finished: it points at the opportunity it became, and cannot go back to being worked.
        app.On(Lead, DocEvent.BeforeSave, ctx =>
        {
            if (ctx.Old?.GetValueOrDefault("status") as string == "Converted" && ctx.Doc.GetValueOrDefault("status") as string != "Converted")
                ctx.Refuse("A converted lead cannot be reopened. Work on its opportunity instead.");
            if (ctx.Old?.GetValueOrDefault("status") as string != "Converted" && ctx.Doc.GetValueOrDefault("status") as string == "Converted"
                && string.IsNullOrEmpty(ctx.Doc.GetValueOrDefault("opportunity") as string))
                ctx.Refuse("Use Convert to opportunity to convert a lead.");
            return Task.CompletedTask;
        });

        // POST /api/method/qahwa_crm.convert_lead  { "lead": "LEAD-00001", "amount": 5000, "expected_close": "2026-12-01" }
        app.Method("convert_lead", async ctx =>
        {
            var leadName = ctx.RequiredText("lead");
            var lead = await ctx.Documents.GetAsync(Lead, leadName, ctx.User, ctx.Cancellation);
            if (lead["status"] as string is "Converted")
                throw new ConflictException("This lead is already converted.");
            if (lead["status"] as string is "Lost")
                throw new ConflictException("A lost lead cannot be converted. Set its status back first.");

            var values = new Dictionary<string, object?>
            {
                ["title"] = $"{(lead.GetValueOrDefault("company") as string is { Length: > 0 } company ? company : (string)lead["lead_name"]!)} opportunity",
                ["lead"] = leadName,
            };
            if (ctx.Value("amount") is { ValueKind: System.Text.Json.JsonValueKind.Number } amount)
                values["amount"] = amount.GetDecimal();
            if (ctx.Text("expected_close") is { Length: > 0 } close)
                values["expected_close"] = close;

            var opportunity = await ctx.Documents.InsertAsync(Opportunity, values, ctx.User, ctx.Cancellation);
            await ctx.Documents.UpdateAsync(Lead, leadName,
                new Dictionary<string, object?> { ["status"] = "Converted", ["opportunity"] = opportunity["name"] }, ctx.User, ctx.Cancellation);
            return new { opportunity = opportunity["name"], lead = leadName };
        }, new MethodOptions { PerMinute = 30 });

        // The pipeline: what is open, how much it is worth, and what it is worth weighted by probability, stage by stage.
        app.ScriptReport("pipeline", "CRM pipeline", async ctx =>
        {
            var rows = (await ctx.Documents.ListAsync(Opportunity, new ListRequest
            {
                Fields = ["stage", "amount", "probability"], Limit = 10_000,
            }, ctx.User, ctx.Cancellation)).Data;
            var result = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var stage in StageProbability.Keys)
            {
                var inStage = rows.Where(r => r["stage"] as string == stage).ToList();
                var total = inStage.Sum(r => r["amount"] is null ? 0m : Convert.ToDecimal(r["amount"]));
                var weighted = inStage.Sum(r => (r["amount"] is null ? 0m : Convert.ToDecimal(r["amount"])) * (r["probability"] is null ? 0m : Convert.ToDecimal(r["probability"])) / 100m);
                result.Add(new Dictionary<string, object?> { ["stage"] = stage, ["count"] = (long)inStage.Count, ["amount"] = total, ["weighted"] = Math.Round(weighted, 2) });
            }

            return new ScriptReportData(
                [new("stage", "Stage"), new("count", "Opportunities", "Int"), new("amount", "Amount", "Currency"), new("weighted", "Weighted amount", "Currency")],
                result);
        }, new ScriptReportOptions { Roles = ["CRM Manager", "CRM User"] });
    }
}
