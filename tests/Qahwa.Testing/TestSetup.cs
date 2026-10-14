using Qahwa.Core;

namespace Qahwa.Testing;

public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public static class TestMetas
{
    public const string Territory = """
        {
          "name": "Territory",
          "autoname": "field:territory_name",
          "fields": [ { "name": "territory_name", "type": "Data", "required": true } ],
          "permissions": [ { "role": "Sales Manager", "read": true, "create": true } ]
        }
        """;

    public const string Customer = """
        {
          "name": "Customer",
          "autoname": "CUST-.#####",
          "titleField": "customer_name",
          "fields": [
            { "name": "customer_name", "type": "Data", "required": true, "unique": true },
            { "name": "customer_type", "type": "Select", "options": "Company\nIndividual", "default": "Company" },
            { "name": "territory", "type": "Link", "options": "Territory" },
            { "name": "credit_limit", "type": "Currency" },
            { "name": "since", "type": "Date" },
            { "name": "active", "type": "Check", "default": "1" }
          ],
          "permissions": [
            { "role": "Sales Manager", "read": true, "write": true, "create": true, "delete": true },
            { "role": "Sales User", "read": true, "write": true, "create": true, "ifOwner": true }
          ]
        }
        """;

    public static MetaRegistry Registry()
    {
        var registry = new MetaRegistry();
        registry.Add(DocTypeLoader.Parse(Territory));
        registry.Add(DocTypeLoader.Parse(Customer));
        registry.ValidateReferences();
        return registry;
    }
}

public static class TestUsers
{
    public static readonly UserContext Admin = new("Administrator", [UserContext.AdministratorRole]);
    public static readonly UserContext Manager = new("manager@example.com", ["Sales Manager"]);
    public static readonly UserContext SalesA = new("a@example.com", ["Sales User"]);
    public static readonly UserContext SalesB = new("b@example.com", ["Sales User"]);
    public static readonly UserContext Nobody = new("nobody@example.com", []);
}

public sealed class TestEngine
{
    public TestEngine(IDocumentRepository? repository = null)
    {
        Registry = TestMetas.Registry();
        Repository = repository ?? new InMemoryDocumentRepository();
        Clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        Documents = new DocumentService(
            Registry, Repository, new PermissionService(), new NamingService(Repository), Clock);
    }

    public MetaRegistry Registry { get; }
    public IDocumentRepository Repository { get; }
    public TestClock Clock { get; }
    public DocumentService Documents { get; }

    public static Dictionary<string, object?> Values(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);
}
