using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class NoteTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", [UserContext.AllRole]);
    private static readonly UserContext Bob = new("bob@example.com", [UserContext.AllRole]);

    [Fact]
    public async Task A_note_is_private_to_its_owner_and_can_be_shared_like_any_document()
    {
        var made = await _engine.Documents.InsertAsync("Note", Values(("title", "Ideas"), ("content", "<p>Hi <script>x</script></p>")), Ann);
        var name = (string)made["name"]!;
        Assert.DoesNotContain("script", (string)(await _engine.Documents.GetAsync("Note", name, Ann))["content"]!, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Note", name, Bob));
        Assert.Empty((await _engine.Documents.ListAsync("Note", new ListRequest { Fields = ["name"], Limit = 10 }, Bob)).Data);
        Assert.Single((await _engine.Documents.ListAsync("Note", new ListRequest { Fields = ["name"], Limit = 10 }, Ann)).Data);
    }
}
