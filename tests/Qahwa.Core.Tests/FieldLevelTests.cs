using Qahwa.Abstractions;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class FieldLevelTests
{
    private const string BonusRow = """
        { "name": "Bonus Row", "isChildTable": true, "fields": [
            { "name": "amount", "type": "Currency" }, { "name": "reason", "type": "Data" } ] }
        """;

    private const string Employee = """
        {
          "name": "Employee",
          "autoname": "EMP-.###",
          "titleField": "full_name",
          "fields": [
            { "name": "full_name", "type": "Data", "required": true },
            { "name": "department", "type": "Data" },
            { "name": "salary", "type": "Currency", "permLevel": 1 },
            { "name": "badge", "type": "Data", "required": true, "permLevel": 1 },
            { "name": "bonuses", "type": "Table", "options": "Bonus Row", "permLevel": 1 },
            { "name": "notes", "type": "Text", "permLevel": 2 }
          ],
          "permissions": [
            { "role": "HR User", "read": true, "write": true, "create": true },
            { "role": "HR Manager", "read": true, "write": true, "create": true, "delete": true },
            { "role": "HR Manager", "read": true, "write": true, "permLevel": 1 },
            { "role": "Auditor", "read": true },
            { "role": "Auditor", "read": true, "permLevel": 1 },
            { "role": "Employee Self", "read": true, "permLevel": 1, "ifOwner": true }
          ]
        }
        """;

    private static readonly UserContext HrUser = new("hr@example.com", ["HR User"]);
    private static readonly UserContext HrManager = new("boss@example.com", ["HR Manager"]);
    private static readonly UserContext Auditor = new("audit@example.com", ["Auditor"]);
    private static readonly UserContext Self = new("self@example.com", ["HR User", "Employee Self"]);

    private readonly TestEngine _engine = new();

    public FieldLevelTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse(BonusRow));
        _engine.Registry.Add(DocTypeLoader.Parse(Employee));
        _engine.Registry.ValidateReferences();
    }

    private Task<Dictionary<string, object?>> CreateAsync(IUserContext? user = null, string name = "Ada", params (string, object?)[] extra) =>
        _engine.Documents.InsertAsync(
            "Employee",
            Values([("full_name", name), ("badge", "B-1"), ("salary", 5000), ("bonuses", new[] { Values(("amount", 100), ("reason", "q1")) }), ("notes", "n"), .. extra]),
            user ?? TestUsers.Admin);

    [Fact]
    public async Task A_document_only_contains_the_fields_the_person_may_read()
    {
        await CreateAsync();

        var hr = await _engine.Documents.GetAsync("Employee", "EMP-001", HrUser);
        var boss = await _engine.Documents.GetAsync("Employee", "EMP-001", HrManager);
        var admin = await _engine.Documents.GetAsync("Employee", "EMP-001", TestUsers.Admin);

        Assert.Equal("Ada", hr["full_name"]);
        Assert.DoesNotContain("salary", hr.Keys);       // absent, not null: it does not even reveal the field exists
        Assert.DoesNotContain("bonuses", hr.Keys);
        Assert.DoesNotContain("notes", hr.Keys);
        Assert.Equal(5000m, boss["salary"]);
        Assert.Single((List<Dictionary<string, object?>>)boss["bonuses"]!);
        Assert.DoesNotContain("notes", boss.Keys);       // level 2 needs its own rule
        Assert.Contains("notes", admin.Keys);
    }

    [Fact]
    public async Task Lists_leave_out_unreadable_fields_and_refuse_to_filter_or_sort_by_them()
    {
        await CreateAsync();

        var list = await _engine.Documents.ListAsync("Employee", new ListRequest(), HrUser);
        Assert.DoesNotContain("salary", list.Data[0].Keys);
        Assert.Contains("full_name", list.Data[0].Keys);

        foreach (var request in new[]
        {
            new ListRequest { Fields = ["salary"] },
            new ListRequest { Filters = [new RawFilter("salary", ">", 1)] },
            new ListRequest { OrderBy = "salary" },
        })
        {
            var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.ListAsync("Employee", request, HrUser));
            Assert.Equal("salary", Assert.Single(ex.Errors).Field);
        }

        var boss = await _engine.Documents.ListAsync("Employee", new ListRequest { Filters = [new RawFilter("salary", ">", 1)], OrderBy = "salary" }, HrManager);
        Assert.Single(boss.Data);
        Assert.Equal(5000m, boss.Data[0]["salary"]);
    }

    [Fact]
    public async Task A_filter_cannot_be_used_to_guess_a_hidden_value()
    {
        await CreateAsync();

        // If this were allowed, "salary = 5000" returning one row would reveal the salary.
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.ListAsync("Employee", new ListRequest { Filters = [new RawFilter("salary", "=", 5000)] }, HrUser));
    }

    [Fact]
    public async Task Creating_a_document_cannot_fill_in_fields_above_the_person_s_level()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            CreateAsync(HrUser, "Bob"));

        Assert.Contains(ex.Errors, e => e.Field == "salary");
        Assert.Contains(ex.Errors, e => e.Field == "badge");
        Assert.Contains(ex.Errors, e => e.Field == "bonuses");
        Assert.Contains(ex.Errors, e => e.Field == "notes");
    }

    [Fact]
    public async Task Empty_values_for_forbidden_fields_are_ignored_and_required_ones_are_not_demanded()
    {
        // "badge" is required but lives at level 1: someone who cannot fill it in is not blocked by it.
        var doc = await _engine.Documents.InsertAsync(
            "Employee", Values(("full_name", "Cy"), ("salary", ""), ("badge", null)), HrUser);

        Assert.Equal("Cy", doc["full_name"]);
        Assert.DoesNotContain("salary", doc.Keys);

        // ...while a person who can write the level must provide it.
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Employee", Values(("full_name", "Di")), HrManager));
        Assert.Equal("badge", Assert.Single(ex.Errors).Field);
    }

    [Fact]
    public async Task Updating_cannot_change_fields_above_the_level_but_an_unchanged_echo_is_fine()
    {
        await CreateAsync();
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("Employee", "EMP-001", Values(("salary", 9000)), HrUser));
        Assert.Equal("salary", Assert.Single(ex.Errors).Field);

        var echoed = await _engine.Documents.UpdateAsync(
            "Employee", "EMP-001", Values(("department", "Sales"), ("salary", 5000)), HrUser);
        Assert.Equal("Sales", echoed["department"]);

        var boss = await _engine.Documents.UpdateAsync("Employee", "EMP-001", Values(("salary", 9000)), HrManager);
        Assert.Equal(9000m, boss["salary"]);
    }

    [Fact]
    public async Task A_table_above_the_level_cannot_be_changed_or_emptied()
    {
        await CreateAsync();
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("Employee", "EMP-001", Values(("bonuses", new[] { Values(("amount", 1)) })), HrUser));
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("Employee", "EMP-001", Values(("bonuses", new List<object>())), HrUser));

        var stillThere = await _engine.Documents.GetAsync("Employee", "EMP-001", HrManager);
        Assert.Single((List<Dictionary<string, object?>>)stillThere["bonuses"]!);

        var boss = await _engine.Documents.UpdateAsync("Employee", "EMP-001", Values(("bonuses", new[]
        {
            Values(("name", ((List<Dictionary<string, object?>>)stillThere["bonuses"]!)[0]["name"]), ("amount", 250)),
        })), HrManager);
        Assert.Equal(250m, ((List<Dictionary<string, object?>>)boss["bonuses"]!)[0]["amount"]);
    }

    [Fact]
    public async Task What_a_write_returns_is_also_filtered()
    {
        var created = await _engine.Documents.InsertAsync("Employee", Values(("full_name", "Eve")), HrUser);

        Assert.DoesNotContain("salary", created.Keys);
        var updated = await _engine.Documents.UpdateAsync("Employee", (string)created["name"]!, Values(("department", "IT")), HrUser);
        Assert.DoesNotContain("salary", updated.Keys);
    }

    [Fact]
    public async Task A_level_rule_limited_to_own_documents_only_helps_on_documents_the_person_owns()
    {
        var mine = await _engine.Documents.InsertAsync("Employee", Values(("full_name", "Me")), Self);
        await _engine.Documents.UpdateAsync("Employee", (string)mine["name"]!, Values(("salary", 1), ("badge", "B-2")), TestUsers.Admin);
        var theirs = await CreateAsync(null, "Colleague");

        var own = await _engine.Documents.GetAsync("Employee", (string)mine["name"]!, Self);
        var other = await _engine.Documents.GetAsync("Employee", (string)theirs["name"]!, Self);

        Assert.Equal(1m, own["salary"]);                // "Employee Self" may read level 1 of their own document
        Assert.DoesNotContain("salary", other.Keys);   // but not of anyone else's
    }

    [Fact]
    public async Task Read_only_rules_at_a_level_allow_reading_and_not_writing()
    {
        await CreateAsync();
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var seen = await _engine.Documents.GetAsync("Employee", "EMP-001", Auditor);
        Assert.Equal(5000m, seen["salary"]);

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.UpdateAsync("Employee", "EMP-001", Values(("salary", 1)), Auditor)); // no write at level 0
    }

    [Fact]
    public async Task The_meta_view_hides_unreadable_fields_and_the_permission_rules()
    {
        var hr = _engine.Documents.GetMetaView("Employee", HrUser);
        var boss = _engine.Documents.GetMetaView("Employee", HrManager);
        var admin = _engine.Documents.GetMetaView("Employee", TestUsers.Admin);

        Assert.Equal(["full_name", "department"], hr.Meta.Fields.Select(f => f.Name));
        Assert.Empty(hr.Meta.Permissions);
        Assert.Equal([0], hr.WritableLevels);
        Assert.Equal(["full_name", "department", "salary", "badge", "bonuses"], boss.Meta.Fields.Select(f => f.Name));
        Assert.Equal([0, 1], boss.WritableLevels);
        Assert.Equal(6, admin.Meta.Fields.Count);
        Assert.Equal(Enumerable.Range(0, 10), admin.WritableLevels);
        Assert.Contains("Bonus Row", boss.Children.Keys);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_hidden_title_field_is_not_advertised()
    {
        var meta = DocTypeLoader.Parse("""
            { "name": "Secretive", "titleField": "code", "fields": [ { "name": "code", "type": "Data", "permLevel": 1 }, { "name": "x", "type": "Data" } ],
              "permissions": [ { "role": "HR User", "read": true } ] }
            """);
        _engine.Registry.Add(meta);

        var view = _engine.Documents.GetMetaView("Secretive", HrUser);

        Assert.Null(view.Meta.TitleField);
        await Task.CompletedTask;
    }

    [Fact]
    public void Field_access_is_computed_from_the_rules_that_match_the_person()
    {
        var meta = _engine.Registry.Get("Employee");
        var permissions = _engine.Permissions;

        var boss = permissions.FieldAccessFor(meta, HrManager, "x", creating: false);
        var auditor = permissions.FieldAccessFor(meta, Auditor, "x", creating: false);
        var selfOwn = permissions.FieldAccessFor(meta, Self, "self@example.com", creating: false);
        var selfOther = permissions.FieldAccessFor(meta, Self, "other@example.com", creating: false);
        var nobody = permissions.FieldAccessFor(meta, TestUsers.Nobody, "x", creating: false);
        var list = permissions.FieldAccessFor(meta, Self, null, creating: false);

        Assert.Equal([0, 1], boss.Readable.Order());
        Assert.Equal([0, 1], boss.Writable.Order());
        Assert.Equal([0, 1], auditor.Readable.Order());
        Assert.Empty(auditor.Writable);
        Assert.Contains(1, selfOwn.Readable);
        Assert.DoesNotContain(1, selfOther.Readable);
        Assert.DoesNotContain(1, list.Readable);       // in a list the owner is unknown, so own-only grants nothing above level 0
        Assert.Equal([0], nobody.Readable.Order());
        Assert.True(permissions.FieldAccessFor(meta, TestUsers.Admin, null, creating: false).IsUnrestricted);
    }

    [Fact]
    public void Definitions_are_checked_for_levels_and_duplicate_rules()
    {
        var bad = DocTypeLoader.Parse(Employee) with
        {
            Fields = [new DocFieldMeta { Name = "a", Type = FieldType.Data, PermLevel = 10 }],
            Permissions =
            [
                new DocPermMeta { Role = "X", Read = true },
                new DocPermMeta { Role = "X", Read = true },
                new DocPermMeta { Role = "Y", Read = true, Create = true, PermLevel = 1 },
                new DocPermMeta { Role = "Z", Read = true, PermLevel = 11 },
            ],
        };

        var errors = DocTypeLoader.Check(bad);

        Assert.Contains(errors, e => e.Field == "fields[0].permLevel");
        Assert.Contains(errors, e => e.Field == "permissions[1].role" && e.Message.Contains("already a rule"));
        Assert.Contains(errors, e => e.Field == "permissions[2].permLevel" && e.Message.Contains("level 0"));
        Assert.Contains(errors, e => e.Field == "permissions[3].permLevel");
    }

    [Fact]
    public void Child_table_fields_cannot_have_their_own_level()
    {
        var errors = DocTypeLoader.Check(new DocTypeMeta
        {
            Name = "Row",
            IsChildTable = true,
            Fields = [new DocFieldMeta { Name = "a", Type = FieldType.Data, PermLevel = 1 }],
        });

        Assert.Contains(errors, e => e.Field == "fields[0].permLevel" && e.Message.Contains("child table"));
    }

    [Fact]
    public void Levels_survive_a_round_trip_through_json()
    {
        var again = DocTypeLoader.Parse(DocTypeLoader.Serialize(DocTypeLoader.Parse(Employee)));

        Assert.Equal(1, again.Fields.Single(f => f.Name == "salary").PermLevel);
        Assert.Contains(again.Permissions, p => p.Role == "HR Manager" && p.PermLevel == 1);
    }
}
