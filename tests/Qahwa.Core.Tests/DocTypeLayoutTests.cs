using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>DocType Layouts: a simpler form for some roles, without touching permissions (ADR-157).</summary>
public class DocTypeLayoutTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Clerk = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Boss = new("bob@example.com", ["Boss"]);

    public DocTypeLayoutTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Quote", "fields": [
                { "name": "customer", "type": "Data", "required": true },
                { "name": "notes", "type": "Text" },
                { "name": "margin", "type": "Percent" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true }, { "role": "Boss", "read": true, "write": true, "create": true } ] }
            """));
    }

    private Task<Dictionary<string, object?>> LayoutAsync(string name, string roles, string hidden, int sequence = 0, bool enabled = true, string doctype = "Quote") =>
        _engine.Documents.InsertAsync("DocType Layout", Values(("layout_name", name), ("document_type", doctype), ("roles", roles), ("hidden_fields", hidden), ("sequence", sequence), ("enabled", enabled)), TestUsers.Admin);

    private async Task<string[]> HiddenFor(IUserContext user) =>
        (await _engine.Documents.GetMetaViewAsync("Quote", user)).Meta.Fields.Where(f => f.Hidden).Select(f => f.Name).Order().ToArray();

    [Fact]
    public async Task A_layout_hides_fields_for_its_roles_only()
    {
        await LayoutAsync("Clerk form", "Clerk", "margin\nnotes");

        Assert.Equal(["margin", "notes"], await HiddenFor(Clerk));
        Assert.Empty(await HiddenFor(Boss));
    }

    [Fact]
    public async Task With_no_roles_a_layout_is_for_everyone_and_the_smallest_position_wins()
    {
        await LayoutAsync("Everyone", "", "notes", sequence: 5);
        await LayoutAsync("Clerks first", "Clerk", "margin", sequence: 1);

        Assert.Equal(["margin"], await HiddenFor(Clerk));   // the lower position fits the clerk
        Assert.Equal(["notes"], await HiddenFor(Boss));     // only the open one fits the boss
    }

    [Fact]
    public async Task A_disabled_layout_does_nothing_and_a_layout_never_changes_what_may_be_saved()
    {
        await LayoutAsync("Off", "Clerk", "margin", enabled: false);
        Assert.Empty(await HiddenFor(Clerk));

        await LayoutAsync("Clerk form", "Clerk", "margin");
        // Hidden is about the form: the API still takes the field, and permissions are untouched.
        var made = await _engine.Documents.InsertAsync("Quote", Values(("customer", "Acme"), ("margin", 12)), Clerk);
        Assert.Equal(12.0, Convert.ToDouble(made["margin"]));
    }

    [Fact]
    public async Task A_layout_must_name_real_optional_fields_of_a_real_doctype()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => LayoutAsync("Bad field", "Clerk", "nothing"));
        await Assert.ThrowsAsync<DocumentValidationException>(() => LayoutAsync("Required", "Clerk", "customer"));   // a required field cannot be hidden
        await Assert.ThrowsAsync<DocumentValidationException>(() => LayoutAsync("No doctype", "Clerk", "notes", doctype: "Nothing Here"));
    }

    [Fact]
    public async Task Only_system_managers_manage_layouts()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("DocType Layout",
            Values(("layout_name", "Mine"), ("document_type", "Quote")), Clerk));
    }
}
