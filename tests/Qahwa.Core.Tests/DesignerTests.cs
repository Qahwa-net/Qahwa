using Qahwa.Abstractions;
using Qahwa.Core.Designer;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class DesignerTests
{
    private const string SupplierJson = """
        {
          "name": "Supplier",
          "module": "Buying",
          "autoname": "SUP-.#####",
          "titleField": "supplier_name",
          "fields": [
            { "name": "supplier_name", "type": "Data", "label": "Supplier Name", "required": true, "unique": true, "inListView": true },
            { "name": "supplier_type", "type": "Select", "options": "Local\nInternational", "default": "Local" }
          ],
          "permissions": [ { "role": "System Manager", "read": true, "write": true, "create": true, "delete": true } ]
        }
        """;

    private readonly TestEngine _engine = new();

    private static DocTypeMeta Supplier(Func<DocTypeMeta, DocTypeMeta>? change = null)
    {
        var meta = DocTypeLoader.Parse(SupplierJson);
        return change is null ? meta : change(meta);
    }

    private static DocTypeMeta WithField(DocTypeMeta meta, DocFieldMeta field) =>
        meta with { Fields = [.. meta.Fields, field] };

    private static DocFieldMeta Field(string name, FieldType type, string? options = null) =>
        new() { Name = name, Type = type, Options = options };

    [Fact]
    public async Task A_designed_doctype_works_immediately_without_a_restart()
    {
        var detail = await _engine.Designer.CreateAsync(Supplier(), TestUsers.SystemManager);

        Assert.Equal(DocTypeSource.Custom, detail.Source);
        Assert.True(detail.Editable);
        Assert.Equal(0, detail.DocumentCount);

        var doc = await _engine.Documents.InsertAsync(
            "Supplier", Values(("supplier_name", "Globex")), TestUsers.SystemManager);
        Assert.Equal("SUP-00001", doc["name"]);
        Assert.Equal("Local", doc["supplier_type"]);
        Assert.Contains("Supplier", _engine.Documents.ListMetas(TestUsers.SystemManager).Select(m => m.Name));
    }

    [Fact]
    public async Task The_definition_is_stored_and_listed_with_its_source()
    {
        await _engine.Designer.CreateAsync(Supplier(), TestUsers.SystemManager);

        var stored = await _engine.DocTypeStore.GetAsync("Supplier");
        Assert.Equal("sysmgr@example.com", stored!.Owner);
        Assert.Equal("Supplier", DocTypeLoader.Parse(stored.Definition).Name);

        var items = await _engine.Designer.ListAsync(TestUsers.SystemManager);
        Assert.Equal(DocTypeSource.Custom, items.Single(i => i.Name == "Supplier").Source);
        Assert.Equal(DocTypeSource.Core, items.Single(i => i.Name == "User").Source);
        Assert.False(items.Single(i => i.Name == "User").Editable);
        Assert.Equal(DocTypeSource.App, items.Single(i => i.Name == "Customer").Source);
    }

    [Fact]
    public async Task Only_system_managers_and_administrators_can_design()
    {
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Designer.CreateAsync(Supplier(), UserContext.Anonymous));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Designer.CreateAsync(Supplier(), TestUsers.Manager));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Designer.ListAsync(TestUsers.SalesA));
        await _engine.Designer.CreateAsync(Supplier(), TestUsers.Admin);
    }

    [Fact]
    public async Task Create_reports_every_problem_with_its_path()
    {
        var bad = new DocTypeMeta
        {
            Name = "Bad Thing",
            IsChildTable = true,
            Autoname = "field:nope",
            TitleField = "missing",
            Fields =
            [
                Field("Bad Name", FieldType.Data),
                Field("ok", FieldType.Data),
                Field("ok", FieldType.Int),
                Field("grid", FieldType.Table, "Customer"),
                Field("pick", FieldType.Select),
                Field("owner", FieldType.Data),
                Field("ref", FieldType.Link, "Nowhere"),
                Field("file", FieldType.Attach),
            ],
        };

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Designer.CreateAsync(bad, TestUsers.SystemManager));

        var paths = ex.Errors.Select(e => e.Field).ToHashSet();
        Assert.Contains("fields[0].name", paths);
        Assert.Contains("fields[2].name", paths);   // duplicate
        Assert.Contains("fields[3].type", paths);   // a child table cannot hold another table
        Assert.Contains("fields[3].options", paths); // and Customer is not a child table
        Assert.Contains("fields[4].options", paths); // select without options
        Assert.Contains("fields[5].name", paths);   // reserved
        Assert.Contains("fields[6].options", paths); // link target missing
        Assert.Contains("fields[7].type", paths);   // attachments are not supported yet
        Assert.Contains("titleField", paths);
        Assert.Contains("autoname", paths);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("customer")]
    [InlineData("Comment")]
    [InlineData("Series")]
    [InlineData("x\"; DROP TABLE y; --")]
    public async Task Names_that_exist_or_are_reserved_or_unsafe_are_refused(string name)
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Designer.CreateAsync(Supplier(m => m with { Name = name }), TestUsers.SystemManager));

        Assert.Contains(ex.Errors, e => e.Field == "name");
    }

    [Fact]
    public async Task Link_fields_can_point_at_existing_doctypes_including_new_custom_ones()
    {
        await _engine.Designer.CreateAsync(Supplier(), TestUsers.SystemManager);

        var order = new DocTypeMeta
        {
            Name = "Purchase Request",
            Fields = [Field("supplier", FieldType.Link, "Supplier"), Field("buyer", FieldType.Link, "User")],
        };
        await _engine.Designer.CreateAsync(order, TestUsers.SystemManager);

        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Purchase Request", Values(("supplier", "SUP-99999")), TestUsers.Admin));
    }

    [Fact]
    public async Task Editing_adds_fields_and_keeps_existing_documents()
    {
        var created = await _engine.Designer.CreateAsync(Supplier(), TestUsers.SystemManager);
        await _engine.Documents.InsertAsync("Supplier", Values(("supplier_name", "Globex")), TestUsers.SystemManager);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await _engine.Designer.UpdateAsync(
            "Supplier", WithField(Supplier(), Field("rating", FieldType.Int)), created.Modified, TestUsers.SystemManager);

        Assert.True(updated.Modified > created.Modified);
        Assert.Equal(1, updated.DocumentCount);
        var edited = await _engine.Documents.UpdateAsync("Supplier", "SUP-00001", Values(("rating", "5")), TestUsers.SystemManager);
        Assert.Equal(5, edited["rating"]);
    }

    [Fact]
    public async Task Existing_field_types_and_names_cannot_change()
    {
        var created = await _engine.Designer.CreateAsync(Supplier(), TestUsers.SystemManager);

        var typeChange = Supplier(m => m with
        {
            Fields = [m.Fields[0] with { Type = FieldType.Text }, m.Fields[1]],
        });
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Designer.UpdateAsync("Supplier", typeChange, created.Modified, TestUsers.SystemManager));
        Assert.Contains(ex.Errors, e => e.Field == "fields[0].type");

        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Designer.UpdateAsync("Supplier", Supplier(m => m with { Name = "Vendor" }), created.Modified, TestUsers.SystemManager));
    }

    [Fact]
    public async Task Removing_a_field_keeps_its_data_and_preview_says_so()
    {
        var created = await _engine.Designer.CreateAsync(Supplier(), TestUsers.SystemManager);
        var without = Supplier(m => m with { Fields = [m.Fields[0]] });

        var preview = await _engine.Designer.PreviewAsync(without, "Supplier", TestUsers.SystemManager);

        Assert.Empty(preview.Errors);
        Assert.Contains("supplier_type", Assert.Single(preview.Notes));
        await _engine.Designer.UpdateAsync("Supplier", without, created.Modified, TestUsers.SystemManager);
        Assert.True(_engine.Schema.HasTable("Supplier"));
    }

    [Fact]
    public async Task Stale_edits_are_conflicts()
    {
        var created = await _engine.Designer.CreateAsync(Supplier(), TestUsers.SystemManager);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Designer.UpdateAsync("Supplier", WithField(Supplier(), Field("a", FieldType.Data)), created.Modified, TestUsers.SystemManager);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _engine.Designer.UpdateAsync("Supplier", WithField(Supplier(), Field("b", FieldType.Data)), created.Modified, TestUsers.SystemManager));
    }

    [Fact]
    public async Task Standard_doctypes_cannot_be_edited_or_deleted()
    {
        var customer = _engine.Registry.Get("Customer");

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Designer.UpdateAsync("Customer", customer, null, TestUsers.Admin));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Designer.DeleteAsync("User", TestUsers.Admin));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Designer.DeleteAsync("Nope", TestUsers.Admin));
    }

    [Fact]
    public async Task A_doctype_with_documents_cannot_be_deleted()
    {
        await _engine.Designer.CreateAsync(Supplier(), TestUsers.SystemManager);
        await _engine.Documents.InsertAsync("Supplier", Values(("supplier_name", "Globex")), TestUsers.SystemManager);

        await Assert.ThrowsAsync<ConflictException>(() => _engine.Designer.DeleteAsync("Supplier", TestUsers.SystemManager));

        await _engine.Documents.DeleteAsync("Supplier", "SUP-00001", TestUsers.SystemManager);
        await _engine.Designer.DeleteAsync("Supplier", TestUsers.SystemManager);
        Assert.False(_engine.Registry.TryGet("Supplier", out _));
        Assert.False(_engine.Schema.HasTable("Supplier"));
        Assert.Null(await _engine.DocTypeStore.GetAsync("Supplier"));
    }

    [Fact]
    public async Task A_doctype_used_by_a_link_cannot_be_deleted()
    {
        await _engine.Designer.CreateAsync(Supplier(), TestUsers.SystemManager);
        await _engine.Designer.CreateAsync(
            new DocTypeMeta { Name = "Quote", Fields = [Field("supplier", FieldType.Link, "Supplier")] }, TestUsers.SystemManager);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => _engine.Designer.DeleteAsync("Supplier", TestUsers.SystemManager));

        Assert.Contains("Quote.supplier", ex.Message);
    }

    [Fact]
    public async Task A_deleted_name_can_be_used_again()
    {
        await _engine.Designer.CreateAsync(Supplier(), TestUsers.SystemManager);
        await _engine.Designer.DeleteAsync("Supplier", TestUsers.SystemManager);

        await _engine.Designer.CreateAsync(Supplier(), TestUsers.SystemManager);

        Assert.True(_engine.Registry.TryGet("Supplier", out _));
    }

    [Fact]
    public async Task Preview_returns_errors_and_planned_changes_without_saving()
    {
        var broken = await _engine.Designer.PreviewAsync(Supplier(m => m with { Name = "Customer" }), null, TestUsers.SystemManager);
        Assert.NotEmpty(broken.Errors);
        Assert.Null(broken.Schema);

        var good = await _engine.Designer.PreviewAsync(Supplier(), null, TestUsers.SystemManager);
        Assert.Empty(good.Errors);
        Assert.False(good.Schema!.TableExists);
        Assert.Contains("Create table", good.Schema.Changes[0].Description);
        Assert.False(_engine.Registry.TryGet("Supplier", out _));
    }

    [Fact]
    public async Task Custom_doctypes_are_reloaded_from_the_store_and_bad_rows_are_skipped()
    {
        await _engine.Designer.CreateAsync(Supplier(), TestUsers.SystemManager);
        var now = DateTime.UtcNow;
        await _engine.DocTypeStore.InsertAsync(new StoredDocType("Broken", "{ not json", now, now, "x", "x"));
        await _engine.DocTypeStore.InsertAsync(new StoredDocType("Customer", DocTypeLoader.Serialize(DocTypeLoader.Parse("""{ "name": "Customer", "fields": [ { "name": "different", "type": "Data" } ] }""")), now, now, "x", "x")); // not the same as the standard one

        var fresh = TestMetas.Registry();
        var failures = await new CustomDocTypeLoader(_engine.DocTypeStore, fresh).LoadAsync();

        Assert.True(fresh.TryGet("Supplier", out _));
        Assert.Equal(DocTypeSource.Custom, fresh.SourceOf("Supplier"));
        Assert.Equal(DocTypeSource.App, fresh.SourceOf("Customer")); // the standard one wins
        Assert.Equal(2, failures.Count);
    }

    [Fact]
    public async Task A_stored_definition_identical_to_the_standard_one_is_accepted_quietly_as_developer_mode_leaves_it()
    {
        var now = DateTime.UtcNow;
        await _engine.DocTypeStore.InsertAsync(new StoredDocType("Customer", DocTypeLoader.Serialize(_engine.Registry.Get("Customer")), now, now, "x", "x"));

        var fresh = TestMetas.Registry();
        var failures = await new CustomDocTypeLoader(_engine.DocTypeStore, fresh).LoadAsync();

        Assert.Empty(failures);
        Assert.Equal(DocTypeSource.App, fresh.SourceOf("Customer"));
    }

    [Fact]
    public void Catalog_lists_every_field_type_exactly_once()
    {
        var types = FieldTypeCatalog.All.Select(i => i.Type).ToList();

        Assert.Equal(Enum.GetValues<FieldType>().Order(), types.Order());
        Assert.All(FieldTypeCatalog.All.Where(i => !i.Supported), i => Assert.NotNull(i.UnsupportedReason));
        Assert.False(FieldTypeCatalog.IsSupported(FieldType.Password));
        Assert.True(FieldTypeCatalog.IsSupported(FieldType.Table));
        Assert.True(FieldTypeCatalog.IsSupported(FieldType.Currency));
    }

    [Fact]
    public async Task The_registry_stays_consistent_under_concurrent_changes()
    {
        var registry = TestMetas.Registry();
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var meta = new DocTypeMeta { Name = $"Thing {w}", Fields = [Field($"f{i % 5}", FieldType.Data)] };
                registry.Upsert(meta, DocTypeSource.Custom);
                if (i % 7 == 0)
                    registry.Remove(meta.Name);
            }
        }));
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
                Assert.True(registry.All.Count >= 3); // core User/Role/Has Role + Customer/Territory minus nothing
        });

        await Task.WhenAll(writers.Append(reader));
    }

    [Fact]
    public void Check_reports_all_errors_and_validate_throws_the_first_with_the_full_list()
    {
        var meta = new DocTypeMeta { Name = "1bad", Fields = [Field("A", FieldType.Data), Field("a b", FieldType.Data)] };

        var errors = DocTypeLoader.Check(meta);
        var ex = Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Validate(meta));

        Assert.Equal(3, errors.Count);
        Assert.Equal(errors, ex.Errors);
    }
}
