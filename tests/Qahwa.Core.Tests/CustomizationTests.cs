using Qahwa.Abstractions;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class CustomizationTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Sys = TestUsers.SystemManager;

    private static DocFieldMeta Custom(string name, FieldType type = FieldType.Data, string? label = null, bool required = false, string? options = null) =>
        new() { Name = name, Type = type, Label = label, Required = required, Options = options };

    private Task<CustomizationView> SaveAsync(string doctype, Customization c, DateTime? expected = null, IUserContext? user = null) =>
        _engine.Customization.SaveAsync(doctype, c, expected, user ?? Sys);

    private DocFieldMeta Field(string doctype, string name) => _engine.Registry.Get(doctype).Fields.Single(f => f.Name == name);

    // ---- custom fields ----

    [Fact]
    public async Task A_custom_field_appears_on_the_doctype_and_works_like_any_other()
    {
        await SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_phone", label: "Phone")] });

        Assert.Equal("Phone", Field("Customer", "custom_phone").Label);
        var doc = await _engine.Documents.InsertAsync("Customer", Values(("customer_name", "Acme"), ("custom_phone", "555")), TestUsers.Admin);
        Assert.Equal("555", doc["custom_phone"]);
        Assert.Equal("555", (await _engine.Documents.ListAsync("Customer", new ListRequest { Filters = [new("custom_phone", "=", "555")] }, TestUsers.Admin)).Data.Single()["custom_phone"]);
        Assert.Contains(_engine.Documents.GetMetaView("Customer", TestUsers.Admin).Meta.Fields, f => f.Name == "custom_phone");
    }

    [Fact]
    public async Task A_required_custom_field_is_enforced_and_the_schema_is_extended()
    {
        await SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_code", required: true)] });

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Customer", Values(("customer_name", "Acme")), TestUsers.Admin));
        Assert.Contains(ex.Errors, e => e.Field == "custom_code");
        Assert.True(_engine.Schema.HasTable("Customer")); // the schema manager was asked to add the column
    }

    [Fact]
    public async Task Custom_fields_can_be_ordered_and_the_title_field_changed()
    {
        await SaveAsync("Customer", new Customization
        {
            CustomFields = [Custom("custom_code")],
            Order = ["custom_code", "customer_name"],
            TitleField = "custom_code",
        });

        var meta = _engine.Registry.Get("Customer");
        Assert.Equal("custom_code", meta.Fields[0].Name);
        Assert.Equal("customer_name", meta.Fields[1].Name);
        Assert.Equal("custom_code", meta.TitleField);
        Assert.Contains(meta.Fields, f => f.Name == "credit_limit"); // fields not listed follow
    }

    // ---- property overrides ----

    [Fact]
    public async Task Properties_of_standard_fields_can_be_changed_and_cleared()
    {
        await SaveAsync("Customer", new Customization
        {
            Overrides = new Dictionary<string, FieldOverride>
            {
                ["customer_type"] = new() { Label = "Kind", Required = true, Options = "Company\nIndividual\nPartner", Description = "Pick one", Default = "Partner" },
                ["credit_limit"] = new() { Hidden = true, InListView = true, PermLevel = 1 },
            },
        });

        var type = Field("Customer", "customer_type");
        Assert.Equal("Kind", type.Label);
        Assert.True(type.Required);
        Assert.Equal("Company\nIndividual\nPartner", type.Options);
        Assert.Equal("Partner", type.Default);
        var limit = Field("Customer", "credit_limit");
        Assert.True(limit.Hidden);
        Assert.True(limit.InListView);
        Assert.Equal(1, limit.PermLevel);

        // "" clears a text property back to nothing.
        await SaveAsync("Customer", new Customization { Overrides = new Dictionary<string, FieldOverride> { ["customer_type"] = new() { Default = "" } } }, _engine.CustomizationStore.Modified("Customer"));
        Assert.Null(Field("Customer", "customer_type").Default);
    }

    [Fact]
    public async Task Customising_never_changes_the_definition_so_a_new_save_starts_from_the_original()
    {
        var first = await SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_a")], Overrides = new Dictionary<string, FieldOverride> { ["customer_type"] = new() { Label = "Kind" } } });
        await SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_b")] }, first.Modified);

        var meta = _engine.Registry.Get("Customer");
        Assert.DoesNotContain(meta.Fields, f => f.Name == "custom_a");
        Assert.Contains(meta.Fields, f => f.Name == "custom_b");
        Assert.Null(Field("Customer", "customer_type").Label); // the earlier label change is gone: the new customisation did not include it
    }

    [Fact]
    public async Task Conditions_can_be_added_to_standard_fields_and_are_checked()
    {
        await SaveAsync("Customer", new Customization { Overrides = new Dictionary<string, FieldOverride> { ["credit_limit"] = new() { DependsOn = "customer_type == 'Company'" } } });
        Assert.Equal("customer_type == 'Company'", Field("Customer", "credit_limit").DependsOn);

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            SaveAsync("Customer", new Customization { Overrides = new Dictionary<string, FieldOverride> { ["credit_limit"] = new() { DependsOn = "nope ==" } } }, _engine.CustomizationStore.Modified("Customer")));
        Assert.Contains(ex.Errors, e => e.Field.StartsWith("fields[credit_limit].dependsOn", StringComparison.Ordinal));
    }

    // ---- validation ----

    [Fact]
    public async Task Bad_customisations_are_refused_with_the_field_named()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync("Customer", new Customization
        {
            CustomFields = [Custom("phone"), Custom("custom_x"), Custom("custom_x"), Custom("custom_t", FieldType.Table), Custom("custom_p", FieldType.Password), Custom("customer_name")],
            Overrides = new Dictionary<string, FieldOverride>
            {
                ["nope"] = new() { Label = "X" },
                ["credit_limit"] = new() { Options = "a\nb" },
                ["territory"] = new() { PermLevel = 12 },
            },
        }));

        var fields = ex.Errors.Select(e => e.Field).ToHashSet();
        Assert.Contains("fields[phone].name", fields);
        Assert.Contains("fields[custom_x].name", fields); // duplicate
        Assert.Contains("fields[custom_t].type", fields);
        Assert.Contains("fields[custom_p].type", fields);
        Assert.Contains("fields[customer_name].name", fields); // collides with a standard field
        Assert.Contains("fields[nope]", fields);
        Assert.Contains("fields[credit_limit].options", fields);
        Assert.Contains("fields[territory].permLevel", fields);
        Assert.DoesNotContain(_engine.Registry.Get("Customer").Fields, f => f.Name == "custom_x"); // nothing was applied

        // Once the fields themselves are fine, order and title field are checked against the result.
        var later = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            SaveAsync("Customer", new Customization { Order = ["ghost"], TitleField = "missing" }));
        Assert.Contains(later.Errors, e => e.Field == "order");
        Assert.Contains(later.Errors, e => e.Field == "titleField");
    }

    [Fact]
    public async Task A_custom_link_must_point_at_a_real_doctype()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_rel", FieldType.Link, options: "Nowhere")] }));

        Assert.Contains(ex.Errors, e => e.Field == "fields[custom_rel].options");
        await SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_rel", FieldType.Link, options: "Territory")] });
    }

    // ---- who and what ----

    [Fact]
    public async Task Only_managers_customise_and_only_administrators_touch_framework_doctypes()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => SaveAsync("Customer", new Customization(), user: TestUsers.Manager));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Customization.ListAsync(UserContext.Anonymous));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => SaveAsync("User", new Customization { CustomFields = [Custom("custom_phone")] }));

        await SaveAsync("User", new Customization { CustomFields = [Custom("custom_phone")] }, user: TestUsers.Admin);
        Assert.Contains(_engine.Registry.Get("User").Fields, f => f.Name == "custom_phone");
        Assert.False((await _engine.Customization.GetAsync("User", Sys)).CanEdit); // a System Manager may look only
        Assert.True((await _engine.Customization.GetAsync("Customer", Sys)).CanEdit);
    }

    [Fact]
    public async Task Designer_doctypes_and_child_tables_are_not_customised_here()
    {
        await _engine.Designer.CreateAsync(DocTypeLoader.Parse("""{ "name": "Gadget", "fields": [ { "name": "a", "type": "Data" } ] }"""), TestUsers.Admin);
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Line", "isChildTable": true, "fields": [ { "name": "a", "type": "Data" } ] }"""));

        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync("Gadget", new Customization()));
        await Assert.ThrowsAsync<NotFoundException>(() => SaveAsync("Line", new Customization()));
        await Assert.ThrowsAsync<NotFoundException>(() => SaveAsync("Nope", new Customization()));

        var items = await _engine.Customization.ListAsync(Sys);
        Assert.DoesNotContain(items, i => i.Name is "Gadget" or "Line");
        Assert.Contains(items, i => i.Name == "Customer");
    }

    [Fact]
    public async Task The_list_shows_what_is_customised()
    {
        await SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_a"), Custom("custom_b")] });

        var items = await _engine.Customization.ListAsync(Sys);

        var customer = items.Single(i => i.Name == "Customer");
        Assert.True(customer.Customized);
        Assert.Equal(2, customer.CustomFieldCount);
        Assert.False(items.Single(i => i.Name == "Territory").Customized);
    }

    [Fact]
    public async Task Concurrent_customisation_is_detected()
    {
        var first = await SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_a")] });

        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync("Customer", new Customization())); // someone who did not see the first
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_a"), Custom("custom_b")] }, first.Modified);

        Assert.True(second.Modified > first.Modified);
        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync("Customer", new Customization(), first.Modified));
    }

    // ---- reset and restart ----

    [Fact]
    public async Task Reset_goes_back_to_the_definition_but_keeps_the_data_of_removed_custom_fields()
    {
        await SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_phone")], Overrides = new Dictionary<string, FieldOverride> { ["customer_type"] = new() { Label = "Kind" } } });
        var id = (string)(await _engine.Documents.InsertAsync("Customer", Values(("customer_name", "Acme"), ("custom_phone", "555")), TestUsers.Admin))["name"]!;

        var view = await _engine.Customization.ResetAsync("Customer", Sys);

        Assert.False(view.Customized);
        Assert.DoesNotContain(_engine.Registry.Get("Customer").Fields, f => f.Name == "custom_phone");
        Assert.Null(Field("Customer", "customer_type").Label);
        Assert.Null(await _engine.CustomizationStore.GetAsync("Customer"));

        await SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_phone")] });
        Assert.Equal("555", (await _engine.Documents.GetAsync("Customer", id, TestUsers.Admin))["custom_phone"]); // and it comes back
    }

    [Fact]
    public async Task Customisations_are_reapplied_after_a_restart_and_a_broken_one_is_skipped()
    {
        await SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_phone")], Order = ["custom_phone"] });
        await _engine.CustomizationStore.SaveAsync(new StoredCustomization("Territory", "{ not json", DateTime.UtcNow, "x"), null);
        await _engine.CustomizationStore.SaveAsync(new StoredCustomization("Gone", """{ "customFields": [] }""", DateTime.UtcNow, "x"), null);

        var restarted = TestMetas.Registry();
        var manager = new CustomizationManager(restarted, _engine.CustomizationStore, _engine.Schema, _engine.Permissions, _engine.Clock);
        var failures = await manager.LoadAsync();

        Assert.Equal(2, failures.Count);
        Assert.Equal("custom_phone", restarted.Get("Customer").Fields[0].Name);
        Assert.DoesNotContain(restarted.Get("Territory").Fields, f => f.Name.StartsWith("custom_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Permission_rules_are_not_touched_by_customising()
    {
        var before = _engine.Registry.Get("Customer").Permissions;

        await SaveAsync("Customer", new Customization { CustomFields = [Custom("custom_a")] });

        Assert.Equal(before, _engine.Registry.Get("Customer").Permissions);
        await _engine.Documents.ListAsync("Customer", new ListRequest(), TestUsers.Manager);
    }
}
