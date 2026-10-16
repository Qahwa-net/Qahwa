using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ViewLogTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Editor"]);
    private static readonly UserContext Rae = new("rae@example.com", ["Reader"]);

    public ViewLogTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Memo", "autoname": "field:title", "trackViews": true, "fields": [ { "name": "title", "type": "Data", "required": true } ],
              "permissions": [ { "role": "Editor", "read": true, "write": true, "create": true }, { "role": "Reader", "read": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Quiet", "autoname": "field:title", "fields": [ { "name": "title", "type": "Data" } ], "permissions": [ { "role": "Editor", "read": true, "write": true, "create": true } ] }
            """));
    }

    [Fact]
    public async Task Opening_a_document_is_recorded_once_per_person_and_counted_after_ten_minutes()
    {
        await _engine.Documents.InsertAsync("Memo", Values(("title", "m1")), Ann);

        await _engine.Documents.GetAsync("Memo", "m1", Rae);
        await _engine.Documents.GetAsync("Memo", "m1", Rae); // a reload
        ((TestClock)_engine.Clock).Advance(TimeSpan.FromMinutes(11));
        await _engine.Documents.GetAsync("Memo", "m1", Rae);
        await _engine.Documents.GetAsync("Memo", "m1", Ann);

        var viewers = await _engine.ViewLog.ViewersAsync("Memo", "m1", Ann);
        Assert.Equal(2, viewers.Count);
        Assert.Equal(2, viewers.Single(v => v.User == Rae.User).Views);
    }

    [Fact]
    public async Task Only_people_who_may_change_the_document_see_who_viewed_it()
    {
        await _engine.Documents.InsertAsync("Memo", Values(("title", "m1")), Ann);
        await _engine.Documents.GetAsync("Memo", "m1", Rae);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.ViewLog.ViewersAsync("Memo", "m1", Rae));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ViewLog.ViewersAsync("Memo", "ghost", Ann));
    }

    [Fact]
    public async Task A_doctype_that_does_not_track_views_keeps_no_log_and_a_forbidden_read_is_not_a_view()
    {
        await _engine.Documents.InsertAsync("Quiet", Values(("title", "q")), Ann);
        await _engine.Documents.GetAsync("Quiet", "q", Ann);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Memo", "m1", new UserContext("x@example.com", ["Nobody"])));

        Assert.Empty(await _engine.Repository.ListAsync(_engine.Registry.Get("View Log"), new ListQuery { Limit = 10 }));
        Assert.Empty(await _engine.ViewLog.ViewersAsync("Quiet", "q", Ann));
    }

    [Fact]
    public async Task The_log_goes_with_a_deleted_document()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Gone", "autoname": "field:title", "trackViews": true, "fields": [ { "name": "title", "type": "Data" } ], "permissions": [ { "role": "Editor", "read": true, "write": true, "create": true, "delete": true } ] }"""));
        await _engine.Documents.InsertAsync("Gone", Values(("title", "g")), Ann);
        await _engine.Documents.GetAsync("Gone", "g", Ann);
        Assert.Single(await _engine.Repository.ListAsync(_engine.Registry.Get("View Log"), new ListQuery { Limit = 10 }));

        await _engine.Documents.DeleteAsync("Gone", "g", Ann);

        Assert.Empty(await _engine.Repository.ListAsync(_engine.Registry.Get("View Log"), new ListQuery { Limit = 10 }));
    }
}
