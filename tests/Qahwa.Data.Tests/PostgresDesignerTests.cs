using Qahwa.Abstractions;
using Qahwa.Core;
using Qahwa.Core.Designer;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

/// <summary>The DocType designer against real PostgreSQL: DDL, rollback, persistence (needs Docker).</summary>
public class PostgresDesignerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // Administrator may design DocTypes and also use them without a permission rule.
    private static readonly UserContext Admin = TestUsers.Admin;

    private async Task<PgStack> StackAsync()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        return await postgres.CreateStackAsync();
    }

    private static DocFieldMeta Field(string name, FieldType type, bool unique = false, string? options = null) =>
        new() { Name = name, Type = type, Unique = unique, Options = options };

    private static DocTypeMeta Supplier(params DocFieldMeta[] extra) => new()
    {
        Name = "Supplier",
        Module = "Buying",
        Autoname = "SUP-.#####",
        Fields = [Field("supplier_name", FieldType.Data), Field("rating", FieldType.Int), .. extra],
    };

    private static async Task<bool> TableExistsAsync(PgStack db, string table) =>
        await db.ScalarAsync($"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = '{table}'") == 1;

    [SkippableFact]
    public async Task Creating_a_doctype_creates_a_real_table_and_documents_work_at_once()
    {
        await using var db = await StackAsync();

        await db.Designer.CreateAsync(Supplier(), Admin);

        Assert.True(await TableExistsAsync(db, "tabSupplier"));
        var doc = await db.Documents.InsertAsync("Supplier", Values(("supplier_name", "Globex"), ("rating", 4)), Admin);
        Assert.Equal("SUP-00001", doc["name"]);
        Assert.Equal(4, doc["rating"]);
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"__doctype\""));
    }

    [SkippableFact]
    public async Task Custom_doctypes_survive_a_restart()
    {
        await using var db = await StackAsync();
        await db.Designer.CreateAsync(Supplier(), Admin);

        var afterRestart = TestMetas.Registry(); // a fresh process knows only core and app DocTypes
        Assert.False(afterRestart.TryGet("Supplier", out _));
        var failures = await new CustomDocTypeLoader(db.DocTypeStore, afterRestart).LoadAsync();

        Assert.Empty(failures);
        Assert.Equal(DocTypeSource.Custom, afterRestart.SourceOf("Supplier"));
        Assert.True((await db.Sync.PlanAsync(afterRestart.Get("Supplier"))).IsEmpty);
    }

    [SkippableFact]
    public async Task Adding_a_field_alters_the_table_and_keeps_rows()
    {
        await using var db = await StackAsync();
        var created = await db.Designer.CreateAsync(Supplier(), Admin);
        await db.Documents.InsertAsync("Supplier", Values(("supplier_name", "Globex")), Admin);
        db.Clock.Advance(TimeSpan.FromMinutes(1));

        await db.Designer.UpdateAsync("Supplier", Supplier(Field("notes", FieldType.Text)), created.Modified, Admin);

        var doc = await db.Documents.UpdateAsync("Supplier", "SUP-00001", Values(("notes", "VIP")), Admin);
        Assert.Equal("VIP", doc["notes"]);
        Assert.Equal("Globex", doc["supplier_name"]);
    }

    [SkippableFact]
    public async Task A_removed_field_keeps_its_column_and_data()
    {
        await using var db = await StackAsync();
        var created = await db.Designer.CreateAsync(Supplier(), Admin);
        await db.Documents.InsertAsync("Supplier", Values(("supplier_name", "Globex"), ("rating", 5)), Admin);
        db.Clock.Advance(TimeSpan.FromMinutes(1));

        var slim = Supplier() with { Fields = [Field("supplier_name", FieldType.Data)] };
        await db.Designer.UpdateAsync("Supplier", slim, created.Modified, Admin);

        Assert.Equal(5, await db.ScalarAsync("SELECT \"rating\" FROM \"tabSupplier\""));
    }

    [SkippableFact]
    public async Task Making_a_field_unique_over_duplicates_is_a_friendly_conflict_and_changes_nothing()
    {
        await using var db = await StackAsync();
        var created = await db.Designer.CreateAsync(Supplier(), Admin);
        await db.Documents.InsertAsync("Supplier", Values(("supplier_name", "Same")), Admin);
        await db.Documents.InsertAsync("Supplier", Values(("supplier_name", "Same")), Admin);
        db.Clock.Advance(TimeSpan.FromMinutes(1));

        var unique = Supplier() with { Fields = [Field("supplier_name", FieldType.Data, unique: true), Field("rating", FieldType.Int)] };
        var ex = await Assert.ThrowsAsync<ConflictException>(() => db.Designer.UpdateAsync("Supplier", unique, created.Modified, Admin));

        Assert.Contains("duplicate", ex.Message);
        Assert.False(db.Registry.Get("Supplier").Fields[0].Unique);
        Assert.Equal((await db.DocTypeStore.GetAsync("Supplier"))!.Modified, created.Modified);
        Assert.Equal(2, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabSupplier\""));
    }

    [SkippableFact]
    public async Task Unique_can_be_turned_on_and_off()
    {
        await using var db = await StackAsync();
        var created = await db.Designer.CreateAsync(Supplier(Field("code", FieldType.Data, unique: true)), Admin);
        await db.Documents.InsertAsync("Supplier", Values(("supplier_name", "A"), ("code", "X1")), Admin);
        await Assert.ThrowsAsync<ConflictException>(() =>
            db.Documents.InsertAsync("Supplier", Values(("supplier_name", "B"), ("code", "X1")), Admin));
        db.Clock.Advance(TimeSpan.FromMinutes(1));

        await db.Designer.UpdateAsync("Supplier", Supplier(Field("code", FieldType.Data)), created.Modified, Admin);

        await db.Documents.InsertAsync("Supplier", Values(("supplier_name", "C"), ("code", "X1")), Admin);
        Assert.Equal(2, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabSupplier\" WHERE \"code\" = 'X1'"));
    }

    [SkippableFact]
    public async Task A_type_changed_behind_our_back_blocks_the_save()
    {
        await using var db = await StackAsync();
        var created = await db.Designer.CreateAsync(Supplier(), Admin);
        await using (var command = db.DataSource.CreateCommand("ALTER TABLE \"tabSupplier\" ALTER COLUMN \"rating\" TYPE text"))
            await command.ExecuteNonQueryAsync();

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            db.Designer.UpdateAsync("Supplier", Supplier(Field("extra", FieldType.Data)), created.Modified, Admin));

        Assert.Contains("rating", ex.Errors[0].Message);
    }

    [SkippableFact]
    public async Task Deleting_drops_the_table_only_when_it_is_empty()
    {
        await using var db = await StackAsync();
        await db.Designer.CreateAsync(Supplier(), Admin);
        await db.Documents.InsertAsync("Supplier", Values(("supplier_name", "Globex")), Admin);

        await Assert.ThrowsAsync<ConflictException>(() => db.Designer.DeleteAsync("Supplier", Admin));
        Assert.True(await TableExistsAsync(db, "tabSupplier"));

        await db.Documents.DeleteAsync("Supplier", "SUP-00001", Admin);
        await db.Designer.DeleteAsync("Supplier", Admin);

        Assert.False(await TableExistsAsync(db, "tabSupplier"));
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM \"__doctype\""));
        await db.Designer.CreateAsync(Supplier(), Admin); // the name is free again
    }

    [SkippableFact]
    public async Task Reserved_and_hostile_names_never_reach_the_database()
    {
        await using var db = await StackAsync();

        foreach (var name in new[] { "Series", "series", "x\"; DROP TABLE \"tabUser\"; --" })
        {
            await Assert.ThrowsAsync<DocumentValidationException>(() => db.Designer.CreateAsync(Supplier() with { Name = name }, Admin));
        }

        Assert.True(await TableExistsAsync(db, "tabUser"));
        Assert.True(await TableExistsAsync(db, "tabSeries"));
    }

    [SkippableFact]
    public async Task Preview_reports_the_sql_without_running_it()
    {
        await using var db = await StackAsync();

        var preview = await db.Designer.PreviewAsync(Supplier(), null, Admin);

        Assert.Empty(preview.Errors);
        Assert.StartsWith("CREATE TABLE \"tabSupplier\"", preview.Schema!.Changes[0].Sql);
        Assert.False(await TableExistsAsync(db, "tabSupplier"));
    }

    [SkippableFact]
    public async Task Two_designers_creating_the_same_name_at_once_leave_one_winner()
    {
        await using var db = await StackAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try
            {
                await db.Designer.CreateAsync(Supplier(), Admin);
                return true;
            }
            catch (QahwaException)
            {
                return false;
            }
        }));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"__doctype\""));
    }
}
