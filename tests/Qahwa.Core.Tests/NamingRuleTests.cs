using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class NamingRuleTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    private void Define(string autoname) => _engine.Registry.Add(DocTypeLoader.Parse(
        "{ \"name\": \"Deal\", \"autoname\": " + System.Text.Json.JsonSerializer.Serialize(autoname) + """
        , "fields": [ { "name": "customer", "type": "Data" }, { "name": "kind", "type": "Data" }, { "name": "amount", "type": "Int" } ],
          "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
        """));

    private Task<Dictionary<string, object?>> DealAsync(string customer, string kind = "", int amount = 0) =>
        _engine.Documents.InsertAsync("Deal", Values(("customer", customer), ("kind", kind), ("amount", amount)), Ann);

    private Task<Dictionary<string, object?>> RuleAsync(string pattern, string? condition = null, int priority = 10, bool enabled = true) =>
        _engine.Documents.InsertAsync("Document Naming Rule", Values(("document_type", "Deal"), ("pattern", pattern), ("condition", condition), ("priority", priority), ("enabled", enabled)), TestUsers.Admin);

    [Fact]
    public async Task A_name_can_be_built_by_an_expression()
    {
        Define("expression:upper(kind) + \"-\" + customer");

        var deal = await DealAsync("acme", "big");

        Assert.Equal("BIG-acme", deal["name"]);
    }

    [Fact]
    public async Task An_expression_that_gives_nothing_or_something_unusable_is_refused()
    {
        Define("expression:customer + kind");

        await Assert.ThrowsAsync<DocumentValidationException>(() => DealAsync(string.Empty));
        await Assert.ThrowsAsync<DocumentValidationException>(() => DealAsync(new string('x', 141)));
        await Assert.ThrowsAsync<DocumentValidationException>(() => DealAsync("line\nbreak"));
        await _engine.Documents.InsertAsync("Deal", Values(("customer", "a")), Ann);
        await Assert.ThrowsAsync<ConflictException>(() => _engine.Documents.InsertAsync("Deal", Values(("customer", "a")), Ann));
    }

    [Fact]
    public void An_expression_rule_is_checked_when_the_doctype_is_defined()
    {
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("""{ "name": "X1", "autoname": "expression:customer +", "fields": [ { "name": "customer", "type": "Data" } ] }"""));
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("""{ "name": "X2", "autoname": "expression:ghost", "fields": [ { "name": "customer", "type": "Data" } ] }"""));
        Assert.NotNull(DocTypeLoader.Parse("""{ "name": "X3", "autoname": "expression:customer + creation", "fields": [ { "name": "customer", "type": "Data" } ] }"""));
    }

    [Fact]
    public async Task The_first_matching_rule_by_priority_names_the_document_and_the_doctype_rule_is_the_fallback()
    {
        Define("DEAL-.####");
        await RuleAsync("BIG-.###", "amount >= 1000", priority: 5);
        await RuleAsync("CO-{YYYY}-.###", "kind == \"company\"", priority: 7);
        await RuleAsync("ANY-.###", null, priority: 99);

        Assert.Equal("BIG-001", (await DealAsync("a", "company", 5000))["name"]);
        Assert.Equal("CO-2026-001", (await DealAsync("b", "company", 10))["name"]);
        Assert.Equal("ANY-001", (await DealAsync("c", "person", 10))["name"]);
        Assert.Equal("BIG-002", (await DealAsync("d", "person", 2000))["name"]);
    }

    [Fact]
    public async Task Without_a_matching_rule_the_doctypes_own_rule_applies_and_disabled_or_broken_rules_are_skipped()
    {
        Define("DEAL-.####");
        await RuleAsync("OFF-.###", null, priority: 1, enabled: false);
        await _engine.Repository.InsertAsync(_engine.Registry.Get("Document Naming Rule"), new Dictionary<string, object?>
        {
            ["name"] = "BROKEN", ["document_type"] = "Deal", ["enabled"] = true, ["priority"] = 2L, ["condition"] = "ghost +", ["pattern"] = "BRK-.###",
            ["creation"] = DateTime.UtcNow, ["modified"] = DateTime.UtcNow, ["owner"] = "x", ["modified_by"] = "x", ["docstatus"] = 0, ["idx"] = 0,
        });
        await RuleAsync("MAYBE-.###", "kind == \"x\"", priority: 3);

        Assert.Equal("DEAL-0001", (await DealAsync("a", "y"))["name"]);
    }

    [Fact]
    public async Task A_rule_can_use_a_field_or_an_expression_too()
    {
        Define("DEAL-.####");
        await RuleAsync("field:customer", "kind == \"named\"", priority: 1);
        await RuleAsync("expression:\"X\" + text(amount)", "kind == \"calc\"", priority: 2);

        Assert.Equal("acme", (await DealAsync("acme", "named"))["name"]);
        Assert.Equal("X42", (await DealAsync("z", "calc", 42))["name"]);
    }

    [Fact]
    public async Task Rules_are_checked_when_saved()
    {
        Define("DEAL-.####");
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Deal Line", "isChildTable": true, "fields": [ { "name": "x", "type": "Data" } ] }"""));

        foreach (var (type, pattern, condition) in new[]
        {
            ("Deal", "nonsense", (string?)null), ("Deal", "DEAL-.###", "ghost == 1"), ("Deal", "field:ghost", null), ("Deal", "expression:nope(", null),
            ("Deal", "prompt", null), ("Deal", "hash", null), ("Deal Line", "L-.###", null), ("Ghost", "G-.###", null), ("Deal", "A-{ghost}-.###", null),
        })
        {
            await Assert.ThrowsAsync<DocumentValidationException>(() =>
                _engine.Documents.InsertAsync("Document Naming Rule", Values(("document_type", type), ("pattern", pattern), ("condition", condition)), TestUsers.Admin));
        }

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.InsertAsync("Document Naming Rule", Values(("document_type", "Deal"), ("pattern", "OK-.###")), Ann));
        Assert.NotNull(await RuleAsync("OK-.###"));
    }
}
