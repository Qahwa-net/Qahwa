using Qahwa.Testing;
using Qahwa.Typed;

namespace Qahwa.Core.Tests;

public class TypedDocumentTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Clerk = new("clerk@example.com", ["Clerk"]);

    public TypedDocumentTests()
    {
        foreach (var file in new[] { "typed_bill_line.json", "typed_bill.json" })
            _engine.Registry.Add(DocTypeLoader.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TypedDocTypes", file))));
    }

    [Fact]
    public async Task A_typed_document_saves_and_reads_back_through_the_engine()
    {
        var bill = new TypedBill { Customer = "Acme", BillDate = new DateOnly(2026, 3, 1), Paid = true, Total = 12.5m };
        var line = bill.AddLines();
        line.Item = "pen";
        line.Qty = 3;
        line.Rate = 4.25m;

        var saved = await _engine.Documents.InsertAsync(TypedBill.DocType, bill.Values, Clerk);
        var loaded = new TypedBill(await _engine.Documents.GetAsync(TypedBill.DocType, (string)saved["name"]!, Clerk));

        Assert.Equal("Acme", loaded.Customer);
        Assert.Equal(new DateOnly(2026, 3, 1), loaded.BillDate);
        Assert.True(loaded.Paid);
        Assert.Equal(12.5m, loaded.Total);
        Assert.Equal(loaded.Name, saved["name"]);
        var row = Assert.Single(loaded.Lines);
        Assert.Equal("pen", row.Item);
        Assert.Equal(3, row.Qty);
        Assert.Equal(4.25m, row.Rate);
        Assert.Equal(0, loaded.Docstatus);
    }

    [Fact]
    public void Empty_fields_read_as_null_and_a_check_as_false()
    {
        var bill = new TypedBill();
        Assert.Null(bill.Customer);
        Assert.Null(bill.Total);
        Assert.False(bill.Paid);
        Assert.Empty(bill.Lines);
    }

    [Fact]
    public void A_field_that_clashes_with_a_built_in_member_gets_its_own_property()
    {
        var bill = new TypedBill { ValuesField = "shown as a field" };
        Assert.Equal("shown as a field", bill.ValuesField);
        Assert.Equal("shown as a field", bill.Values["values"]);
        Assert.Equal("values", TypedBill.Fields.ValuesField);
    }

    [Fact]
    public void Layout_fields_get_no_property_and_field_names_are_constants()
    {
        Assert.DoesNotContain(typeof(TypedBill).GetProperties(), p => p.Name == "Section");
        Assert.Equal("bill_date", TypedBill.Fields.BillDate);
        Assert.Equal("Typed Bill", TypedBill.DocType);
    }

    [Fact]
    public void Values_that_arrive_as_text_are_converted()
    {
        var bill = new TypedBill(new Dictionary<string, object?> { ["bill_date"] = "2026-05-06", ["total"] = 7, ["paid"] = "1" });
        Assert.Equal(new DateOnly(2026, 5, 6), bill.BillDate);
        Assert.Equal(7m, bill.Total);
        Assert.True(bill.Paid);
    }
}
