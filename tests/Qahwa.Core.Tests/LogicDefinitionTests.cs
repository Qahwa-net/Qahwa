using Qahwa.Abstractions;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Design-time checks: a DocType with a broken expression is refused with a message that points at it.</summary>
public class LogicDefinitionTests
{
    private static DocFieldMeta F(string name, FieldType type = FieldType.Data, Func<DocFieldMeta, DocFieldMeta>? change = null)
    {
        var field = new DocFieldMeta { Name = name, Type = type };
        return change is null ? field : change(field);
    }

    private static IReadOnlyList<FieldError> Check(DocTypeMeta meta) => DocTypeLoader.Check(meta);

    private static DocTypeMeta Meta(params DocFieldMeta[] fields) => new() { Name = "Thing", Fields = fields };

    [Fact]
    public void Syntax_errors_say_what_is_wrong_and_where_with_the_path_of_the_expression()
    {
        var errors = Check(Meta(F("a"), F("b", FieldType.Data, f => f with { DependsOn = "a == " })));

        var error = Assert.Single(errors);
        Assert.Equal("fields[1].dependsOn", error.Field);
        Assert.Contains("end of the expression", error.Message);
        Assert.Contains("character", error.Message);
    }

    [Fact]
    public void Unknown_fields_functions_and_misplaced_parent_are_refused()
    {
        var errors = Check(Meta(
            F("a"),
            F("b", FieldType.Data, f => f with { MandatoryDependsOn = "missing > 1" }),
            F("c", FieldType.Data, f => f with { ReadOnlyDependsOn = "nosuch(a)" }),
            F("d", FieldType.Data, f => f with { DependsOn = "parent.x == 1" })));

        Assert.Contains(errors, e => e.Field == "fields[1].mandatoryDependsOn" && e.Message.Contains("Unknown field 'missing'"));
        Assert.Contains(errors, e => e.Field == "fields[2].readOnlyDependsOn" && e.Message.Contains("Unknown function"));
        Assert.Contains(errors, e => e.Field == "fields[3].dependsOn" && e.Message.Contains("child table"));
    }

    [Fact]
    public void Standard_fields_can_be_used_in_expressions_and_parent_in_child_tables()
    {
        Assert.Empty(Check(Meta(F("a", FieldType.Data, f => f with { DependsOn = "owner == user() && docstatus == 0" }))));
        Assert.Empty(Check(new DocTypeMeta
        {
            Name = "Row",
            IsChildTable = true,
            Fields = [F("x", FieldType.Int, f => f with { Formula = "parent.rate * 2" })],
        }));
    }

    [Fact]
    public void An_empty_expression_is_an_error_not_silently_ignored()
    {
        var errors = Check(Meta(F("a", FieldType.Data, f => f with { DependsOn = "  " })));

        Assert.Equal("The expression is empty.", Assert.Single(errors).Message);
    }

    [Fact]
    public void Formula_rules_are_enforced()
    {
        var errors = Check(Meta(
            F("n", FieldType.Int),
            F("with_default", FieldType.Int, f => f with { Formula = "n + 1", Default = "5" }),
            F("section", FieldType.SectionBreak, f => f with { Formula = "n" }),
            F("fetching", FieldType.Int, f => f with { Formula = "n", FetchFrom = "n.x" })));

        Assert.Contains(errors, e => e.Field == "fields[1].default");
        Assert.Contains(errors, e => e.Field == "fields[2].formula" && e.Message.Contains("store a value"));
        Assert.Contains(errors, e => e.Field == "fields[3].fetchFrom" && e.Message.Contains("both"));
    }

    [Theory]
    [InlineData("a = b + 1", "b = a + 1")]
    [InlineData("a + 1", "a * 2")]
    public void Circular_formulas_are_refused_with_the_cycle_spelled_out(string a, string b)
    {
        var (first, second) = a.StartsWith("a =", StringComparison.Ordinal)
            ? ("b + 1", "a + 1")   // a depends on b, b depends on a
            : ("a + 1", "a * 2");   // a depends on itself
        var errors = Check(Meta(
            F("a", FieldType.Int, f => f with { Formula = first }),
            F("b", FieldType.Int, f => f with { Formula = second })));

        var error = Assert.Single(errors);
        Assert.Contains("Circular formula", error.Message);
        Assert.Contains("→", error.Message);
        Assert.NotEmpty(b);
    }

    [Fact]
    public void Chains_of_formulas_that_are_not_circular_are_fine()
    {
        Assert.Empty(Check(Meta(
            F("a", FieldType.Int),
            F("b", FieldType.Int, f => f with { Formula = "a + 1" }),
            F("c", FieldType.Int, f => f with { Formula = "b + a" }))));
    }

    [Fact]
    public void Expression_defaults_are_checked_but_plain_defaults_starting_with_equals_alone_are_literals()
    {
        var errors = Check(Meta(
            F("a", FieldType.Data, f => f with { Default = "=nosuch()" }),
            F("b", FieldType.Data, f => f with { Default = "=" })));

        Assert.Single(errors);
        Assert.Equal("fields[0].default", errors[0].Field);
    }

    [Fact]
    public void Fetch_from_must_name_a_link_field_of_the_same_doctype()
    {
        var errors = Check(Meta(
            F("name_text"),
            F("customer", FieldType.Link, f => f with { Options = "Customer" }),
            F("a", FieldType.Data, f => f with { FetchFrom = "customer" }),
            F("b", FieldType.Data, f => f with { FetchFrom = "name_text.value" }),
            F("c", FieldType.Data, f => f with { FetchFrom = "customer.customer_name" }),
            F("d", FieldType.Data, f => f with { FetchIfEmpty = true })));

        Assert.Contains(errors, e => e.Field == "fields[2].fetchFrom" && e.Message.Contains("link_field.target_field"));
        Assert.Contains(errors, e => e.Field == "fields[3].fetchFrom" && e.Message.Contains("not a Link field"));
        Assert.DoesNotContain(errors, e => e.Field == "fields[4].fetchFrom");
        Assert.Contains(errors, e => e.Field == "fields[5].fetchIfEmpty");
    }

    [Fact]
    public void Fetch_from_is_checked_against_the_linked_doctype_once_it_is_known()
    {
        var registry = TestMetas.Registry();
        var meta = Meta(
            F("customer", FieldType.Link, f => f with { Options = "Customer" }),
            F("ok", FieldType.Data, f => f with { FetchFrom = "customer.customer_name" }),
            F("missing", FieldType.Data, f => f with { FetchFrom = "customer.no_such_field" }),
            F("wrong_type", FieldType.Int, f => f with { FetchFrom = "customer.customer_name" }),
            F("standard", FieldType.Data, f => f with { FetchFrom = "customer.owner" }));

        var errors = registry.CheckReferences(meta);

        Assert.DoesNotContain(errors, e => e.Field == "fields[1].fetchFrom");
        Assert.Contains(errors, e => e.Field == "fields[2].fetchFrom" && e.Message.Contains("no field"));
        Assert.Contains(errors, e => e.Field == "fields[3].fetchFrom" && e.Message.Contains("cannot be copied"));
        Assert.DoesNotContain(errors, e => e.Field == "fields[4].fetchFrom");
    }

    [Fact]
    public void Validation_rules_need_a_message_a_real_field_and_a_valid_expression()
    {
        var meta = Meta(F("a", FieldType.Int)) with
        {
            Validations =
            [
                new ValidationRule { Expression = "a > 0", Message = "ok" },
                new ValidationRule { Expression = "a >", Message = "broken" },
                new ValidationRule { Expression = "a > 0", Message = " " },
                new ValidationRule { Expression = "a > 0", Message = "x", Field = "nope" },
                new ValidationRule { Expression = "zzz > 0", Message = "x" },
            ],
        };

        var errors = Check(meta);

        Assert.DoesNotContain(errors, e => e.Field.StartsWith("validations[0]"));
        Assert.Contains(errors, e => e.Field == "validations[1].expression");
        Assert.Contains(errors, e => e.Field == "validations[2].message");
        Assert.Contains(errors, e => e.Field == "validations[3].field");
        Assert.Contains(errors, e => e.Field == "validations[4].expression" && e.Message.Contains("Unknown field"));
    }

    [Fact]
    public void Definitions_with_logic_survive_a_round_trip_through_json()
    {
        var meta = Meta(F("a", FieldType.Int), F("b", FieldType.Int, f => f with { Formula = "a * 2", DependsOn = "a > 0", FetchIfEmpty = false })) with
        {
            Validations = [new ValidationRule { Expression = "a < 100", Message = "Too big", Field = "a" }],
        };

        var again = DocTypeLoader.Parse(DocTypeLoader.Serialize(meta));

        Assert.Equal("a * 2", again.Fields[1].Formula);
        Assert.Equal("a > 0", again.Fields[1].DependsOn);
        Assert.Equal("Too big", Assert.Single(again.Validations).Message);
        Assert.Equal("a", again.Validations[0].Field);
    }

    [Fact]
    public async Task The_designer_reports_broken_logic_and_a_good_definition_works_immediately()
    {
        var engine = new TestEngine();
        var broken = Meta(F("qty", FieldType.Int), F("total", FieldType.Int, f => f with { Formula = "qty * price" })) with { Name = "Basket" };

        var preview = await engine.Designer.PreviewAsync(broken, null, TestUsers.Admin);
        Assert.Contains(preview.Errors, e => e.Field == "fields[1].formula" && e.Message.Contains("Unknown field 'price'"));
        await Assert.ThrowsAsync<DocumentValidationException>(() => engine.Designer.CreateAsync(broken, TestUsers.Admin));

        var good = Meta(
            F("qty", FieldType.Int),
            F("price", FieldType.Currency),
            F("total", FieldType.Currency, f => f with { Formula = "qty * price", ReadOnly = true }),
            F("reason", FieldType.Data, f => f with { MandatoryDependsOn = "qty > 100" })) with
        {
            Name = "Basket",
            Validations = [new ValidationRule { Expression = "qty >= 0", Message = "No negative quantities", Field = "qty" }],
        };
        await engine.Designer.CreateAsync(good, TestUsers.Admin);

        var doc = await engine.Documents.InsertAsync("Basket", Values(("qty", 4), ("price", 2.5)), TestUsers.Admin);
        Assert.Equal(10m, doc["total"]);
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            engine.Documents.InsertAsync("Basket", Values(("qty", 500), ("price", 1)), TestUsers.Admin)); // reason needed above 100
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            engine.Documents.InsertAsync("Basket", Values(("qty", -1), ("price", 1)), TestUsers.Admin)); // rule
    }
}
