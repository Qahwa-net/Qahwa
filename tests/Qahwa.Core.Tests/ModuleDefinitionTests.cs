using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ModuleDefinitionTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Clerk = new("clerk@example.com", ["Clerk", UserContext.AllRole]);

    [Fact]
    public async Task Everyone_signed_in_can_read_modules_but_only_system_managers_change_them()
    {
        await _engine.Documents.InsertAsync("Module Definition", Values(("module_name", "Selling"), ("label", "Sales"), ("sort_order", 1)), TestUsers.Admin);
        var list = await _engine.Documents.ListAsync("Module Definition", new ListRequest { Fields = ["module_name", "label"], Limit = 10 }, Clerk);
        Assert.Equal("Sales", Assert.Single(list.Data)["label"]);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Module Definition", Values(("module_name", "Evil")), Clerk));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.DeleteAsync("Module Definition", "Selling", Clerk));
    }

    [Fact]
    public async Task A_module_is_defined_once()
    {
        await _engine.Documents.InsertAsync("Module Definition", Values(("module_name", "Stock")), TestUsers.Admin);
        await Assert.ThrowsAnyAsync<QahwaException>(() => _engine.Documents.InsertAsync("Module Definition", Values(("module_name", "Stock")), TestUsers.Admin));
    }
}
