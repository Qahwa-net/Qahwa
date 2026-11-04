using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Virtual DocTypes: documents supplied from outside the database, read-only (ADR-158).</summary>
public class VirtualDocTypeTests
{
    private sealed class Rates : IVirtualSource
    {
        public string DocType => "Exchange Rate";

        public int Calls { get; private set; }

        private static readonly Dictionary<string, object?>[] Rows =
        [
            Row("USD", "Dollar", 1.00m), Row("EUR", "Euro", 1.08m), Row("GBP", "Pound", 1.27m), Row("SAR", "Riyal", 0.27m),
        ];

        private static Dictionary<string, object?> Row(string code, string label, decimal rate) =>
            new() { ["name"] = code, ["label"] = label, ["rate"] = rate, ["owner"] = "Administrator", ["creation"] = new DateTime(2026, 1, 1), ["modified"] = new DateTime(2026, 1, 1) };

        public Task<IReadOnlyList<Dictionary<string, object?>>> ListAsync(ListQuery query, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(VirtualQuery.Apply(Rows, query));
        }

        public Task<long> CountAsync(ListQuery query, CancellationToken ct) => Task.FromResult(VirtualQuery.Count(Rows, query));

        public Task<Dictionary<string, object?>?> GetAsync(string name, CancellationToken ct) => Task.FromResult(Rows.FirstOrDefault(r => (string)r["name"]! == name));
    }

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Eve = new("eve@example.com", ["Outsider"]);

    private readonly Rates _rates = new();
    private readonly TestEngine _engine;

    public VirtualDocTypeTests()
    {
        _engine = new TestEngine(new VirtualAwareRepository(new InMemoryDocumentRepository(), [_rates]));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Exchange Rate", "isVirtual": true, "titleField": "label",
              "fields": [ { "name": "label", "type": "Data" }, { "name": "rate", "type": "Decimal" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Quote", "fields": [ { "name": "customer", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private async Task<string[]> NamesAsync(ListRequest request, IUserContext? user = null) =>
        (await _engine.Documents.ListAsync("Exchange Rate", request, user ?? Ann)).Data.Select(r => (string)r["name"]!).ToArray();

    [Fact]
    public async Task A_virtual_doctype_lists_filters_sorts_and_pages_over_what_its_source_returns()
    {
        Assert.Equal(["EUR", "GBP", "SAR", "USD"], await NamesAsync(new ListRequest { OrderBy = "name", Descending = false, Limit = 10 }));
        Assert.Equal(["GBP"], await NamesAsync(new ListRequest { Filters = [new RawFilter("rate", ">", 1.1)], Limit = 10 }));
        Assert.Equal(["EUR", "USD"], await NamesAsync(new ListRequest { Filters = [new RawFilter("label", "in", new[] { "Dollar", "Euro" })], OrderBy = "name", Descending = false, Limit = 10 }));
        Assert.Equal(["USD", "SAR"], await NamesAsync(new ListRequest { OrderBy = "name", Descending = true, Limit = 2 }));
        Assert.Equal(["EUR"], await NamesAsync(new ListRequest { OrderBy = "name", Descending = false, Limit = 1 }));
        Assert.Equal(["GBP"], await NamesAsync(new ListRequest { Filters = [new RawFilter("label", "like", "%oun%")], Limit = 10 }));
    }

    [Fact]
    public async Task Only_the_columns_asked_for_come_back()
    {
        var rows = (await _engine.Documents.ListAsync("Exchange Rate", new ListRequest { Fields = ["label"], OrderBy = "name", Descending = false, Limit = 1 }, Ann)).Data;

        Assert.Equal(["name", "label"], rows[0].Keys.Order(StringComparer.Ordinal).Reverse());   // the name always comes along
        Assert.False(rows[0].ContainsKey("rate"));
    }

    [Fact]
    public async Task One_document_is_read_by_name_and_counted_and_grouped_like_any_other()
    {
        var doc = await _engine.Documents.GetAsync("Exchange Rate", "EUR", Ann);
        Assert.Equal("Euro", doc["label"]);
        var counted = await _engine.Documents.ListAsync("Exchange Rate", new ListRequest { Filters = [new RawFilter("rate", "<", 1.1)], Limit = 1 }, Ann);
        Assert.Equal(3, counted.Total);                                   // the count ignores the page size
        var groups = await _engine.Documents.GroupByAsync("Exchange Rate", "label", new ListRequest(), 20, Ann);
        Assert.Equal(4, groups.Count);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.GetAsync("Exchange Rate", "XXX", Ann));
    }

    [Fact]
    public async Task Permissions_still_decide_who_may_read_and_nothing_can_be_written()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => NamesAsync(new ListRequest { Limit = 10 }, Eve));

        // Even a role that may write cannot: the data is not ours to change.
        await Assert.ThrowsAsync<ConflictException>(() => _engine.Documents.InsertAsync("Exchange Rate", Values(("label", "Yen"), ("rate", 0.007)), Ann));
        await Assert.ThrowsAsync<ConflictException>(() => _engine.Documents.UpdateAsync("Exchange Rate", "EUR", Values(("rate", 2)), Ann));
        await Assert.ThrowsAsync<ConflictException>(() => _engine.Documents.DeleteAsync("Exchange Rate", "EUR", Ann));
    }

    [Fact]
    public void The_desk_is_told_a_virtual_doctype_cannot_be_changed()
    {
        var perms = _engine.Documents.GetPermissions(_engine.Registry.Get("Exchange Rate"), Ann);

        Assert.True(perms.Read);
        Assert.False(perms.Write || perms.Create || perms.Delete || perms.Import);   // the role may write, but the data is not ours
    }

    [Fact]
    public async Task Other_doctypes_go_to_the_real_store_as_before()
    {
        var made = await _engine.Documents.InsertAsync("Quote", Values(("customer", "Acme")), Ann);

        Assert.Equal("Acme", (await _engine.Documents.GetAsync("Quote", (string)made["name"]!, Ann))["customer"]);
        Assert.Equal(0, _rates.Calls);
    }

    [Fact]
    public async Task A_virtual_doctype_nobody_supplies_says_so()
    {
        var engine = new TestEngine(new VirtualAwareRepository(new InMemoryDocumentRepository(), []));
        engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Orphan Feed", "isVirtual": true, "fields": [ { "name": "label", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true } ] }
            """));

        var e = await Assert.ThrowsAsync<ConflictException>(() => engine.Documents.ListAsync("Orphan Feed", new ListRequest { Limit = 5 }, Ann));
        Assert.Contains("nothing supplies", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "name": "V1", "isVirtual": true, "isSingle": true, "fields": [] }""")]
    [InlineData("""{ "name": "V2", "isVirtual": true, "isSubmittable": true, "fields": [] }""")]
    [InlineData("""{ "name": "V3", "isVirtual": true, "isChildTable": true, "fields": [] }""")]
    [InlineData("""{ "name": "V4", "isVirtual": true, "fields": [ { "name": "secret", "type": "Password" } ] }""")]
    public void A_virtual_doctype_cannot_be_single_submittable_a_child_or_hold_secrets_and_files(string json) =>
        Assert.ThrowsAny<DocTypeValidationException>(() => DocTypeLoader.Parse(json));
}
