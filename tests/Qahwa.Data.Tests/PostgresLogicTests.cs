using Qahwa.Abstractions;
using Qahwa.Core;
using Qahwa.Core.Designer;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

/// <summary>Field logic (formulas, rules, fetch) with rows and numbers going through real PostgreSQL columns.</summary>
public class PostgresLogicTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly UserContext Admin = TestUsers.Admin;

    private async Task<PgStack> StackAsync()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        var db = await postgres.CreateStackAsync();

        await db.Designer.CreateAsync(new DocTypeMeta
        {
            Name = "Invoice Line",
            IsChildTable = true,
            Fields =
            [
                new DocFieldMeta { Name = "item", Type = FieldType.Data, Required = true },
                new DocFieldMeta { Name = "qty", Type = FieldType.Int },
                new DocFieldMeta { Name = "rate", Type = FieldType.Currency },
                new DocFieldMeta { Name = "amount", Type = FieldType.Currency, Formula = "qty * rate" },
                new DocFieldMeta { Name = "tax", Type = FieldType.Currency, Formula = "round(amount * parent.tax_rate / 100, 2)" },
            ],
            Validations = [new ValidationRule { Expression = "empty(qty) || qty > 0", Message = "Quantity must be positive", Field = "qty" }],
        }, Admin);

        await db.Designer.CreateAsync(new DocTypeMeta
        {
            Name = "Invoice",
            Autoname = "INV-.####",
            Fields =
            [
                new DocFieldMeta { Name = "customer", Type = FieldType.Link, Options = "Customer" },
                new DocFieldMeta { Name = "customer_name", Type = FieldType.Data, FetchFrom = "customer.customer_name" },
                new DocFieldMeta { Name = "issued", Type = FieldType.Date, Default = "=today()" },
                new DocFieldMeta { Name = "due", Type = FieldType.Date, Formula = "addDays(issued, 14)" },
                new DocFieldMeta { Name = "tax_rate", Type = FieldType.Percent },
                new DocFieldMeta { Name = "lines", Type = FieldType.Table, Options = "Invoice Line" },
                new DocFieldMeta { Name = "subtotal", Type = FieldType.Currency, Formula = "sum(lines, 'amount')" },
                new DocFieldMeta { Name = "tax_total", Type = FieldType.Currency, Formula = "sum(lines, 'tax')" },
                new DocFieldMeta { Name = "grand_total", Type = FieldType.Currency, Formula = "subtotal + tax_total" },
                new DocFieldMeta { Name = "reason", Type = FieldType.Data, MandatoryDependsOn = "grand_total > 1000" },
            ],
            Validations = [new ValidationRule { Expression = "count(lines) > 0", Message = "Add at least one line" }],
        }, Admin);
        return db;
    }

    private static Dictionary<string, object?> Line(string item, int qty, string rate) => Values(("item", item), ("qty", qty), ("rate", rate));

    private static List<Dictionary<string, object?>> Lines(Dictionary<string, object?> doc) =>
        (List<Dictionary<string, object?>>)doc["lines"]!;

    [SkippableFact]
    public async Task Totals_and_taxes_are_calculated_and_stored_in_the_database()
    {
        await using var db = await StackAsync();

        var doc = await db.Documents.InsertAsync("Invoice", Values(
            ("tax_rate", 15), ("lines", new[] { Line("A", 2, "10.00"), Line("B", 3, "19.99") })), Admin);

        Assert.Equal([20m, 59.97m], Lines(doc).Select(l => (decimal)l["amount"]!));
        Assert.Equal([3m, 9m], Lines(doc).Select(l => (decimal)l["tax"]!)); // 8.9955 rounds to 9.00
        Assert.Equal(79.97m, doc["subtotal"]);
        Assert.Equal(12m, doc["tax_total"]);
        Assert.Equal(91.97m, doc["grand_total"]);
        Assert.Equal(new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day), doc["issued"]);
        Assert.Equal(79.97m, (decimal)await ScalarDecimalAsync(db, "SELECT \"subtotal\" FROM \"tabInvoice\""));
        Assert.Equal(20m, (decimal)await ScalarDecimalAsync(db, "SELECT \"amount\" FROM \"tabInvoice Line\" WHERE \"item\" = 'A'"));
    }

    private static async Task<object> ScalarDecimalAsync(PgStack db, string sql)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        return (await command.ExecuteScalarAsync())!;
    }

    [SkippableFact]
    public async Task Changing_the_document_recalculates_saved_rows_that_the_client_did_not_resend()
    {
        await using var db = await StackAsync();
        await db.Documents.InsertAsync("Invoice", Values(("tax_rate", 10), ("lines", new[] { Line("A", 1, "100") })), Admin);
        db.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await db.Documents.UpdateAsync("Invoice", "INV-0001", Values(("tax_rate", 20)), Admin);

        Assert.Equal(20m, Lines(updated)[0]["tax"]);
        Assert.Equal(120m, updated["grand_total"]);
        Assert.Equal(20m, (decimal)await ScalarDecimalAsync(db, "SELECT \"tax\" FROM \"tabInvoice Line\""));
    }

    [SkippableFact]
    public async Task Rules_and_conditions_refuse_bad_documents_and_leave_nothing_behind()
    {
        await using var db = await StackAsync();

        var noLines = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            db.Documents.InsertAsync("Invoice", Values(("tax_rate", 0)), Admin));
        Assert.Equal("Add at least one line", Assert.Single(noLines.Errors).Message);

        var badRow = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            db.Documents.InsertAsync("Invoice", Values(("lines", new[] { Line("A", 0, "1") })), Admin));
        Assert.Equal("lines[0].qty", Assert.Single(badRow.Errors).Field);

        var big = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            db.Documents.InsertAsync("Invoice", Values(("lines", new[] { Line("A", 10, "500") })), Admin));
        Assert.Equal("reason", Assert.Single(big.Errors).Field); // grand_total 5000 makes "reason" mandatory

        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabInvoice\""));
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabInvoice Line\""));
    }

    [SkippableFact]
    public async Task Fetch_from_works_across_documents_in_the_database()
    {
        await using var db = await StackAsync();
        var acme = await db.Documents.InsertAsync("Customer", Values(("customer_name", "Acme")), Admin);

        var doc = await db.Documents.InsertAsync("Invoice", Values(("customer", acme["name"]), ("lines", new[] { Line("A", 1, "1") })), Admin);

        Assert.Equal("Acme", doc["customer_name"]);
        Assert.Equal("Acme", (await db.Documents.GetAsync("Invoice", (string)doc["name"]!, Admin))["customer_name"]);
    }

    [SkippableFact]
    public async Task The_designer_rejects_a_formula_that_reads_a_field_that_does_not_exist_and_nothing_is_created()
    {
        await using var db = await StackAsync();
        var bad = new DocTypeMeta
        {
            Name = "Broken",
            Fields = [new DocFieldMeta { Name = "x", Type = FieldType.Int, Formula = "y + 1" }],
        };

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => db.Designer.CreateAsync(bad, Admin));

        Assert.Contains(ex.Errors, e => e.Field == "fields[0].formula");
        Assert.False(db.Registry.TryGet("Broken", out _));
        Assert.False(await db.TableExistsAsync("tabBroken"));
    }

    [SkippableFact]
    public async Task Logic_in_a_custom_doctype_is_loaded_again_after_a_restart()
    {
        await using var db = await StackAsync();

        var fresh = TestMetas.Registry();
        var failures = await new CustomDocTypeLoader(db.DocTypeStore, fresh).LoadAsync();

        Assert.Empty(failures);
        Assert.Equal("sum(lines, 'amount')", fresh.Get("Invoice").Fields.Single(f => f.Name == "subtotal").Formula);
        Assert.Equal("Add at least one line", Assert.Single(fresh.Get("Invoice").Validations).Message);
        Assert.Equal("parent.tax_rate", "parent.tax_rate"); // the child formula reads the parent's field
        Assert.Contains("parent.tax_rate", fresh.Get("Invoice Line").Fields.Single(f => f.Name == "tax").Formula);
    }
}
