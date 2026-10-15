using System.Text.Json;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>List settings, saved filters and bulk actions.</summary>
public class ListPowerTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Clerk = new("clerk@example.com", ["Clerk"]);
    private static readonly UserContext Boss = new("boss@example.com", ["Boss"]);

    public ListPowerTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            {
              "name": "Staff", "autoname": "field:person",
              "fields": [
                { "name": "person", "type": "Data", "required": true }, { "name": "team", "type": "Select", "options": "A\nB\nC" },
                { "name": "salary", "type": "Currency", "permLevel": 1 }, { "name": "total", "type": "Currency", "formula": "salary * 1" },
                { "name": "pin", "type": "Password" }
              ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true },
                { "role": "Boss", "read": true, "write": true, "create": true, "delete": true },
                { "role": "Boss", "read": true, "write": true, "permLevel": 1 }
              ]
            }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Reader Note", "fields": [ { "name": "text", "type": "Data" } ], "permissions": [ { "role": "Reader", "read": true } ] }
            """));
    }

    private static JsonElement Filters(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private Task<SavedFilterInfo> SaveFilterAsync(IUserContext user, string name, string filters = """[["team","=","A"]]""", bool isPublic = false) =>
        _engine.ListSettings.SaveFilterAsync("Staff", name, Filters(filters), isPublic, user);

    // ---- settings ----

    [Fact]
    public async Task A_person_starts_with_the_default_view()
    {
        var state = await _engine.ListSettings.GetAsync("Staff", Clerk);

        Assert.Null(state.Settings);
        Assert.Empty(state.Filters);
    }

    [Fact]
    public async Task Settings_are_saved_per_person_and_can_be_reset()
    {
        var saved = await _engine.ListSettings.SaveSettingsAsync("Staff", new ListViewSettings(["person", "team", "team", "modified"], 50, "team", true), Clerk);

        Assert.Equal(["person", "team", "modified"], saved.Columns); // duplicates dropped
        var mine = (await _engine.ListSettings.GetAsync("Staff", Clerk)).Settings!;
        Assert.Equal((50, "team", true), (mine.PageSize, mine.SortField, mine.SortDescending));
        Assert.Null((await _engine.ListSettings.GetAsync("Staff", Boss)).Settings);

        await _engine.ListSettings.SaveSettingsAsync("Staff", new ListViewSettings(["person"], 10, null, false), Clerk); // saving again replaces
        Assert.Equal(["person"], (await _engine.ListSettings.GetAsync("Staff", Clerk)).Settings!.Columns);

        await _engine.ListSettings.ResetSettingsAsync("Staff", Clerk);
        Assert.Null((await _engine.ListSettings.GetAsync("Staff", Clerk)).Settings);
    }

    [Fact]
    public async Task Settings_only_allow_columns_and_sorts_the_person_may_read()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.ListSettings.SaveSettingsAsync("Staff",
            new ListViewSettings(["person", "salary", "pin", "ghost", "x\"; DROP TABLE tabStaff;--"], 33, "salary", false), Clerk));

        var fields = ex.Errors.Select(e => e.Field).ToList();
        Assert.Contains("columns", fields);
        Assert.Contains("pageSize", fields);
        Assert.Contains("sortField", fields);
        Assert.Contains(ex.Errors, e => e.Message.Contains("'salary'")); // level 1: the Clerk may not show it
        Assert.Contains(ex.Errors, e => e.Message.Contains("'pin'"));    // passwords never
        await _engine.ListSettings.SaveSettingsAsync("Staff", new ListViewSettings(["person", "salary"], 20, "salary", false), Boss); // the Boss may
    }

    [Fact]
    public async Task A_setting_for_a_field_that_is_no_longer_readable_quietly_drops_it()
    {
        await _engine.ListSettings.SaveSettingsAsync("Staff", new ListViewSettings(["person", "salary"], 20, "salary", true), Boss);
        _engine.Overrides.Set("Staff", [
            new DocPermMeta { Role = "Boss", Read = true, Write = true, Create = true, Delete = true }, // the level 1 rule is gone
        ]);

        var settings = (await _engine.ListSettings.GetAsync("Staff", Boss)).Settings!;

        Assert.Equal(["person"], settings.Columns);
        Assert.Null(settings.SortField);
    }

    [Fact]
    public async Task Lists_settings_need_access_to_a_real_list()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.ListSettings.GetAsync("Staff", TestUsers.Nobody));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.ListSettings.GetAsync("Staff", UserContext.Anonymous));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ListSettings.GetAsync("Nope", Clerk));
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Line", "isChildTable": true, "fields": [ { "name": "a", "type": "Data" } ] }"""));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ListSettings.GetAsync("Line", Clerk));
    }

    // ---- saved filters ----

    [Fact]
    public async Task A_saved_filter_is_private_until_shared_and_the_same_name_replaces_it()
    {
        var first = await SaveFilterAsync(Clerk, "Team A");
        await SaveFilterAsync(Clerk, "team a", """[["team","=","B"]]"""); // same name, any case: replaced

        var mine = (await _engine.ListSettings.GetAsync("Staff", Clerk)).Filters;
        var one = Assert.Single(mine);
        Assert.Equal(first.Id, one.Id);
        Assert.True(one.Mine);
        Assert.Equal("B", one.Filters![0]![2]!.ToString());
        Assert.Empty((await _engine.ListSettings.GetAsync("Staff", Boss)).Filters);
    }

    [Fact]
    public async Task A_public_filter_is_seen_by_everyone_but_only_its_owner_removes_it()
    {
        var shared = await SaveFilterAsync(Clerk, "Everyone's", isPublic: true);

        var seen = Assert.Single((await _engine.ListSettings.GetAsync("Staff", Boss)).Filters);
        Assert.False(seen.Mine);
        Assert.Equal(Clerk.User, seen.Owner);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ListSettings.DeleteFilterAsync("Staff", shared.Id, Boss));

        await _engine.ListSettings.DeleteFilterAsync("Staff", shared.Id, Clerk);
        Assert.Empty((await _engine.ListSettings.GetAsync("Staff", Boss)).Filters);
    }

    [Fact]
    public async Task A_private_filter_cannot_be_removed_by_anyone_else_even_an_administrator()
    {
        var mine = await SaveFilterAsync(Clerk, "Mine");

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ListSettings.DeleteFilterAsync("Staff", mine.Id, Boss));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ListSettings.DeleteFilterAsync("Staff", mine.Id, TestUsers.Admin));
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Other Thing", "fields": [ { "name": "a", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true } ] }"""));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ListSettings.DeleteFilterAsync("Other Thing", mine.Id, Clerk)); // an id of another DocType's filter
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ListSettings.DeleteFilterAsync("Staff", "nope", Clerk));
    }

    [Fact]
    public async Task Sharing_a_filter_with_everyone_needs_the_right_to_edit_the_documents()
    {
        var reader = new UserContext("reader@example.com", ["Reader"]);

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.ListSettings.SaveFilterAsync("Reader Note", "All", Filters("""[["text","=","x"]]"""), true, reader));
        await _engine.ListSettings.SaveFilterAsync("Reader Note", "Private", Filters("""[["text","=","x"]]"""), false, reader);
    }

    [Fact]
    public async Task Filters_are_validated_for_shape_fields_and_size()
    {
        async Task Bad(string filters, string name = "F") =>
            await Assert.ThrowsAsync<DocumentValidationException>(() => SaveFilterAsync(Clerk, name, filters));

        await Bad("[]");
        await Bad("{}");
        await Bad("""[["team","="]]""");
        await Bad("""[["team","between","A"]]""");
        await Bad("""[["ghost","=","A"]]""");
        await Bad("""[["salary","=",1]]""");                        // level 1: not readable by the Clerk
        await Bad("""[["pin","=","x"]]""");                          // passwords never
        await Bad("""[["team","=",{"a":1}]]""");
        await Bad($$"""[["team","=","{{new string('x', 501)}}"]]""");
        await Bad("[" + string.Join(",", Enumerable.Repeat("""["team","=","A"]""", ListSettingsService.MaxFilters + 1)) + "]");
        await Bad("""[["team","=","A"]]""", "");
        await Bad("""[["team","=","A"]]""", new string('n', ListSettingsService.MaxNameLength + 1));
        await Bad("""[["team","=","A"]]""", "bad\nname");
        await SaveFilterAsync(Clerk, "ok", """[["team","in",["A","B"]],["person","like","%a%"],["total","is","set"]]""");
    }

    [Fact]
    public async Task A_person_keeps_a_limited_number_of_filters()
    {
        for (var i = 0; i < ListSettingsService.MaxSavedPerUser; i++)
            await SaveFilterAsync(Clerk, $"f{i}");

        await Assert.ThrowsAsync<DocumentValidationException>(() => SaveFilterAsync(Clerk, "one too many"));
        await SaveFilterAsync(Clerk, "f0", """[["team","=","C"]]"""); // replacing is still fine
    }

    // ---- bulk ----

    private async Task StaffAsync(params string[] names)
    {
        foreach (var name in names)
            await _engine.Documents.InsertAsync("Staff", Values(("person", name), ("team", "A")), Clerk);
    }

    [Fact]
    public async Task A_bulk_update_changes_each_document_and_logs_it()
    {
        await StaffAsync("Ann", "Bob", "Cat");

        var results = await _engine.Bulk.UpdateAsync("Staff", ["Ann", "Bob", "Cat"], "team", "B", Clerk);

        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.All(new[] { "Ann", "Bob", "Cat" }, async n => Assert.Equal("B", (await _engine.Documents.GetAsync("Staff", n, Clerk))["team"]));
        var timeline = await _engine.Activity.TimelineAsync("Staff", "Ann", Clerk);
        Assert.Contains(timeline, i => i.Event == "Update" && i.Data!["changed"]![0]![0]!.ToString() == "team");
    }

    [Fact]
    public async Task A_bulk_update_reports_the_documents_it_could_not_change_and_does_the_rest()
    {
        await StaffAsync("Ann", "Bob");

        var results = await _engine.Bulk.UpdateAsync("Staff", ["Ann", "MISSING", "Bob"], "team", "C", Clerk);

        Assert.Equal([true, false, true], results.Select(r => r.Ok));
        Assert.Contains("MISSING", results[1].Error);
        Assert.Equal("C", (await _engine.Documents.GetAsync("Staff", "Bob", Clerk))["team"]);
    }

    [Fact]
    public async Task Every_rule_still_applies_to_each_document_in_a_bulk_update()
    {
        await StaffAsync("Ann");

        var invalid = await _engine.Bulk.UpdateAsync("Staff", ["Ann"], "team", "Z", Clerk); // not one of the choices
        var protectedField = await _engine.Bulk.UpdateAsync("Staff", ["Ann"], "salary", 5, Clerk); // level 1

        Assert.False(invalid[0].Ok);
        Assert.Contains("team", invalid[0].Error);
        Assert.False(protectedField[0].Ok);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Bulk.UpdateAsync("Staff", ["Ann"], "team", "B", TestUsers.Nobody));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Bulk.DeleteAsync("Staff", ["Ann"], UserContext.Anonymous));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Bulk.DeleteAsync("Reader Note", ["x"], new UserContext("reader@example.com", ["Reader"])));
        Assert.Equal("A", (await _engine.Documents.GetAsync("Staff", "Ann", Clerk))["team"]);
    }

    [Fact]
    public async Task Bulk_updates_refuse_fields_that_cannot_be_set_and_bad_selections()
    {
        await StaffAsync("Ann");

        foreach (var field in new[] { "total", "pin", "ghost", "name", "owner", null, "" })
            await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Bulk.UpdateAsync("Staff", ["Ann"], field, "x", Clerk));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Bulk.UpdateAsync("Staff", [], "team", "A", Clerk));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Bulk.UpdateAsync("Staff", null, "team", "A", Clerk));
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Bulk.DeleteAsync("Staff", Enumerable.Range(0, BulkService.MaxDocuments + 1).Select(i => $"n{i}").ToList(), Clerk));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Bulk.UpdateAsync("Nope", ["a"], "team", "A", Clerk));
    }

    [Fact]
    public async Task A_bulk_delete_removes_what_it_may_and_reports_the_rest()
    {
        _engine.Overrides.Set("Staff", [
            new DocPermMeta { Role = "Clerk", Read = true, Write = true, Create = true, Delete = true, IfOwner = true },
            new DocPermMeta { Role = "Boss", Read = true, Write = true, Create = true, Delete = true },
        ]);
        await _engine.Documents.InsertAsync("Staff", Values(("person", "Mine")), Clerk);
        await _engine.Documents.InsertAsync("Staff", Values(("person", "Theirs")), Boss);

        var results = await _engine.Bulk.DeleteAsync("Staff", ["Mine", "Theirs"], Clerk);

        Assert.Equal([true, false], results.Select(r => r.Ok));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.GetAsync("Staff", "Mine", Boss));
        Assert.Equal("Theirs", (await _engine.Documents.GetAsync("Staff", "Theirs", Boss))["person"]);
    }

    [Fact]
    public async Task A_submitted_document_is_left_alone_by_a_bulk_delete()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Slip", "isSubmittable": true, "autoname": "field:label", "fields": [ { "name": "label", "type": "Data", "required": true } ],
              "permissions": [ { "role": "Boss", "read": true, "write": true, "create": true, "delete": true, "submit": true } ] }
            """));
        await _engine.Documents.InsertAsync("Slip", Values(("label", "Open")), Boss);
        await _engine.Documents.InsertAsync("Slip", Values(("label", "Locked")), Boss);
        await _engine.Documents.SubmitAsync("Slip", "Locked", Boss);

        var results = await _engine.Bulk.DeleteAsync("Slip", ["Open", "Locked"], Boss);

        Assert.Equal([true, false], results.Select(r => r.Ok));
        Assert.Contains("Cancel it first", results[1].Error);
    }
}
