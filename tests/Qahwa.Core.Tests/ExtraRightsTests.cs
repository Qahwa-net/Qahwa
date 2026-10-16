using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ExtraRightsTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Lim = new("lim@example.com", ["Limited"]);
    private static readonly UserContext Both = new("both@example.com", ["Clerk", "Limited"]);

    public ExtraRightsTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Memo", "autoname": "field:title", "fields": [ { "name": "title", "type": "Data" } ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true },
                { "role": "Limited", "read": true, "write": true, "create": true, "import": false, "share": false, "print": false, "email": false } ] }
            """));
    }

    private Task MemoAsync(string title) => _engine.Documents.InsertAsync("Memo", Values(("title", title)), Ann);

    [Fact]
    public async Task A_rule_can_switch_off_import_share_print_and_email_for_a_role()
    {
        await MemoAsync("m1");

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.DataImport.PreviewAsync("Memo", "title\nx\n", "Insert", Lim));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.ShareManager.EnsureCanShareAsync("Memo", "m1", Lim));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Print.RenderAsync("Memo", "m1", null, null, Lim));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Email.SendFromDocumentAsync("Memo", "m1", new EmailRequest(["a@example.com"], "s", "m"), Lim));
        Assert.False(_engine.Documents.GetPermissions(_engine.Registry.Get("Memo"), Lim).Print);
    }

    [Fact]
    public async Task Rules_written_without_the_new_rights_change_nothing_and_another_role_can_still_grant_them()
    {
        await MemoAsync("m1");

        Assert.NotNull(await _engine.Print.RenderAsync("Memo", "m1", null, null, Ann));
        await _engine.ShareManager.EnsureCanShareAsync("Memo", "m1", Ann);
        Assert.NotNull(await _engine.DataImport.PreviewAsync("Memo", "title\nx\n", "Insert", Ann));
        Assert.NotNull(await _engine.Print.RenderAsync("Memo", "m1", null, null, Both)); // one role that grants it is enough
        Assert.True(_engine.Documents.GetPermissions(_engine.Registry.Get("Memo"), Both).Email);
    }

    [Fact]
    public async Task Administrators_are_never_restricted()
    {
        await MemoAsync("m1");

        Assert.NotNull(await _engine.Print.RenderAsync("Memo", "m1", null, null, TestUsers.Admin));
    }
}
