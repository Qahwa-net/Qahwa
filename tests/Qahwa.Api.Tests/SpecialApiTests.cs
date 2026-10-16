using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class SpecialApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    private HttpClient Admin => _factory.ClientFor("Administrator");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private async Task CreateDocTypeAsync(object definition) =>
        Assert.Equal(HttpStatusCode.Created, (await Admin.PostAsJsonAsync("/api/designer/doctypes", new { definition })).StatusCode);

    [Fact]
    public void Csv_cells_are_quoted_and_cannot_run_as_formulas()
    {
        var csv = CsvExport.Write(
            ["name", "note", "amount"],
            [
                new Dictionary<string, object?> { ["name"] = "a,b", ["note"] = "=HYPERLINK(\"http://evil\")", ["amount"] = -5m },
                new Dictionary<string, object?> { ["name"] = "line\nbreak", ["note"] = "+1", ["amount"] = null },
                new Dictionary<string, object?> { ["name"] = "say \"hi\"", ["note"] = "@cmd", ["amount"] = 1.5m },
            ]);

        var lines = csv.Split("\r\n");
        Assert.Equal("name,note,amount", lines[0]);
        Assert.Equal("\"a,b\",\"'=HYPERLINK(\"\"http://evil\"\")\",-5", lines[1]); // text is defused, numbers are left alone
        Assert.StartsWith("\"line", lines[2]);
        Assert.Contains("'+1", csv);
        Assert.Contains("'@cmd", csv);
        Assert.Contains("\"say \"\"hi\"\"\"", csv);
    }

    [Fact]
    public async Task Export_downloads_csv_for_people_with_the_export_right_only()
    {
        await CreateDocTypeAsync(new
        {
            name = "Expense",
            fields = new object[] { new { name = "title", type = "Data" }, new { name = "amount", type = "Currency" } },
            permissions = new object[]
            {
                new { role = "Sales Manager", read = true, create = true, export = true, report = true },
                new { role = "Sales User", read = true },
            },
        });
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        await manager.PostAsJsonAsync("/api/resource/Expense", new { title = "=cmd|' /C calc'!A0", amount = 5 });

        var export = await manager.GetAsync("/api/resource/Expense/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", export.Content.Headers.ContentDisposition?.ToString());
        var body = await export.Content.ReadAsStringAsync();
        Assert.Contains("'=cmd", body);
        Assert.DoesNotContain(",=cmd", body);

        var user = _factory.ClientFor("u@example.com", "Sales User");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/resource/Expense/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/resource/Expense/export")).StatusCode);

        var report = await manager.GetAsync("/api/resource/Expense/report?group_by=title&aggregate=sum&field=amount");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Equal(5m, (await JsonOf(report)).GetProperty("data")[0].GetProperty("value").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/resource/Expense/report?group_by=title&aggregate=drop")).StatusCode);
    }

    [Fact]
    public async Task A_single_doctype_works_without_a_list_and_a_tree_serves_its_nodes()
    {
        await CreateDocTypeAsync(new
        {
            name = "Site Settings",
            isSingle = true,
            fields = new object[] { new { name = "title", type = "Data", @default = "My Site" } },
            permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true } },
        });
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");

        var shown = await JsonOf(await manager.GetAsync("/api/resource/Site%20Settings/Site%20Settings"));
        Assert.Equal("My Site", shown.GetProperty("data").GetProperty("title").GetString());
        var saved = await manager.PutAsJsonAsync("/api/resource/Site%20Settings/Site%20Settings", new { title = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.DeleteAsync("/api/resource/Site%20Settings/Site%20Settings")).StatusCode);

        await CreateDocTypeAsync(new
        {
            name = "Area",
            isTree = true,
            autoname = "field:area_name",
            fields = new object[] { new { name = "area_name", type = "Data", required = true } },
            permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true } },
        });
        await manager.PostAsJsonAsync("/api/resource/Area", new { area_name = "Earth", is_group = true });
        await manager.PostAsJsonAsync("/api/resource/Area", new { area_name = "Asia", parent_node = "Earth" });

        var roots = await JsonOf(await manager.GetAsync("/api/tree/Area"));
        var kids = await JsonOf(await manager.GetAsync("/api/tree/Area?parent=Earth"));
        Assert.Equal("Earth", roots.GetProperty("data")[0].GetProperty("name").GetString());
        Assert.Equal("Asia", kids.GetProperty("data")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/resource/Area", new { area_name = "Bad", parent_node = "Asia" })).StatusCode);
    }

    [Fact]
    public async Task Rename_over_the_api_and_the_series_manager()
    {
        await CreateDocTypeAsync(new
        {
            name = "Folder",
            allowRename = true,
            autoname = "FLD-.###",
            fields = new object[] { new { name = "title", type = "Data" } },
            permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true, delete = true } },
        });
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var id = (await JsonOf(await manager.PostAsJsonAsync("/api/resource/Folder", new { title = "x" }))).GetProperty("data").GetProperty("name").GetString();

        var renamed = await manager.PostAsJsonAsync($"/api/resource/Folder/{id}/rename", new { newName = "Docs", merge = false });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Docs", (await JsonOf(renamed)).GetProperty("data").GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/resource/Folder/Docs")).StatusCode);

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        var list = await JsonOf(await sysmgr.GetAsync("/api/naming/series"));
        Assert.Contains(list.GetProperty("data").EnumerateArray(), s => s.GetProperty("prefix").GetString() == "FLD-" && s.GetProperty("current").GetInt64() == 1);
        var set = await sysmgr.PutAsJsonAsync("/api/naming/series", new { prefix = "FLD-", current = 40 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("FLD-041", (await JsonOf(await manager.PostAsJsonAsync("/api/resource/Folder", new { title = "y" }))).GetProperty("data").GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/naming/series")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysmgr.PutAsJsonAsync("/api/naming/series", new { prefix = "NOPE-", current = 1 })).StatusCode);
    }

    [Fact]
    public async Task A_password_field_is_never_returned_over_the_api()
    {
        await CreateDocTypeAsync(new
        {
            name = "Vault Entry",
            autoname = "field:title",
            fields = new object[] { new { name = "title", type = "Data", required = true }, new { name = "secret", type = "Password" } },
            permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true } },
        });
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");

        var created = await manager.PostAsJsonAsync("/api/resource/Vault Entry", new { title = "main", secret = "hunter2" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.DoesNotContain("hunter2", await created.Content.ReadAsStringAsync());
        var read = await manager.GetAsync("/api/resource/Vault Entry/main");
        var text = await read.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hunter2", text);
        Assert.Equal("********", (await JsonOf(read)).GetProperty("data").GetProperty("secret").GetString());

        var listed = await manager.GetAsync("/api/resource/Vault Entry?fields=[\"name\",\"secret\"]");
        Assert.Equal(HttpStatusCode.BadRequest, listed.StatusCode);
        var plain = await (await manager.GetAsync("/api/resource/Vault Entry")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("hunter2", plain);
        Assert.DoesNotContain("fake:", plain);
    }

    [Fact]
    public async Task A_deleted_document_can_be_restored_by_a_system_manager()
    {
        await CreateDocTypeAsync(new
        {
            name = "Keepsake",
            autoname = "field:title",
            fields = new object[] { new { name = "title", type = "Data", required = true }, new { name = "note", type = "Data" } },
            permissions = new object[] { new { role = "Sales Manager", read = true, write = true, create = true, delete = true }, new { role = "System Manager", read = true, create = true } },
        });
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        await manager.PostAsJsonAsync("/api/resource/Keepsake", new { title = "gone", note = "hello" });
        Assert.Equal(HttpStatusCode.OK, (await manager.DeleteAsync("/api/resource/Keepsake/gone")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync("/api/resource/Keepsake/gone")).StatusCode);

        var listed = await JsonOf(await sysmgr.GetAsync("/api/resource/Deleted Document?filters=[[\"deleted_doctype\",\"=\",\"Keepsake\"]]"));
        var id = listed.GetProperty("data")[0].GetProperty("name").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"/api/deleted-documents/{id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsync($"/api/deleted-documents/{id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysmgr.PostAsync("/api/deleted-documents/NOPE/restore", null)).StatusCode);

        var restored = await sysmgr.PostAsync($"/api/deleted-documents/{id}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal("gone", (await JsonOf(restored)).GetProperty("data").GetProperty("name").GetString());
        Assert.Equal("hello", (await JsonOf(await manager.GetAsync("/api/resource/Keepsake/gone"))).GetProperty("data").GetProperty("note").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await sysmgr.PostAsync($"/api/deleted-documents/{id}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task Tags_and_likes_work_over_the_api_and_filter_lists()
    {
        await CreateDocTypeAsync(new
        {
            name = "Pinboard",
            autoname = "field:title",
            fields = new object[] { new { name = "title", type = "Data", required = true } },
            permissions = new object[] { new { role = "Sales Manager", read = true, write = true, create = true }, new { role = "Sales User", read = true } },
        });
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var reader = _factory.ClientFor("r@example.com", "Sales User");
        await manager.PostAsJsonAsync("/api/resource/Pinboard", new { title = "p1" });
        await manager.PostAsJsonAsync("/api/resource/Pinboard", new { title = "p2" });

        var added = await manager.PostAsJsonAsync("/api/resource/Pinboard/p1/tags", new { tag = "Urgent" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal("urgent", (await JsonOf(added)).GetProperty("data")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/resource/Pinboard/p1/tags", new { tag = "a,b" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/resource/Pinboard/p1/tags", new { tag = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/resource/Pinboard/p1/tags")).StatusCode);

        var filtered = await JsonOf(await reader.GetAsync("/api/resource/Pinboard?filters=[[\"_tag\",\"=\",\"urgent\"]]"));
        Assert.Single(filtered.GetProperty("data").EnumerateArray());
        var popular = await JsonOf(await reader.GetAsync("/api/tags/Pinboard"));
        Assert.Equal(1, popular.GetProperty("data")[0].GetProperty("count").GetInt32());

        var liked = await JsonOf(await reader.PostAsync("/api/resource/Pinboard/p2/like", null));
        Assert.True(liked.GetProperty("data").GetProperty("liked").GetBoolean());
        var mine = await JsonOf(await reader.GetAsync("/api/resource/Pinboard?filters=[[\"_liked_by\",\"=\",\"__user\"]]"));
        Assert.Equal("p2", mine.GetProperty("data")[0].GetProperty("name").GetString());
        var unliked = await JsonOf(await reader.DeleteAsync("/api/resource/Pinboard/p2/like"));
        Assert.Equal(0, unliked.GetProperty("data").GetProperty("count").GetInt32());

        var removed = await JsonOf(await manager.DeleteAsync("/api/resource/Pinboard/p1/tags/urgent"));
        Assert.Empty(removed.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task Live_updates_stream_to_people_watching_a_document_they_can_read()
    {
        await CreateDocTypeAsync(new
        {
            name = "Whiteboard",
            autoname = "field:title",
            fields = new object[] { new { name = "title", type = "Data", required = true }, new { name = "note", type = "Data" } },
            permissions = new object[] { new { role = "Sales Manager", read = true, write = true, create = true }, new { role = "Sales User", read = true } },
        });
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var reader = _factory.ClientFor("r@example.com", "Sales User");
        await manager.PostAsJsonAsync("/api/resource/Whiteboard", new { title = "w1" });

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/realtime", HttpCompletionOption.ResponseHeadersRead)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync("/api/realtime?doctype=Whiteboard", HttpCompletionOption.ResponseHeadersRead)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync("/api/realtime?doctype=Whiteboard&name=ghost", HttpCompletionOption.ResponseHeadersRead)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("n@example.com", "Nobody").GetAsync("/api/realtime?doctype=Whiteboard&name=w1", HttpCompletionOption.ResponseHeadersRead)).StatusCode);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stream = await reader.GetAsync("/api/realtime?doctype=Whiteboard&name=w1", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
        using var lines = new StreamReader(await stream.Content.ReadAsStreamAsync(timeout.Token));
        Assert.StartsWith(":", (await lines.ReadLineAsync(timeout.Token))!); // connected

        await manager.PutAsJsonAsync("/api/resource/Whiteboard/w1", new { note = "changed" });

        string? eventLine = null, dataLine = null;
        while (dataLine is null && await lines.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event:")) eventLine = line;
            if (line.StartsWith("data:")) dataLine = line;
        }

        Assert.Equal("event: doc", eventLine);
        Assert.Contains("\"name\":\"w1\"", dataLine);
        Assert.Contains("m@example.com", dataLine);
        Assert.DoesNotContain("changed", dataLine); // where, never what
    }

    [Fact]
    public async Task A_person_manages_only_their_own_email_digest()
    {
        var ann = _factory.ClientFor("ann@example.com", "Sales Manager");
        var bob = _factory.ClientFor("bob@example.com", "Sales Manager");

        var start = (await JsonOf(await ann.GetAsync("/api/email-digest"))).GetProperty("data");
        Assert.False(start.GetProperty("enabled").GetBoolean());

        var set = await ann.PutAsJsonAsync("/api/email-digest", new { enabled = true, frequency = "Weekly", sendHour = 9 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var data = (await JsonOf(set)).GetProperty("data");
        Assert.True(data.GetProperty("enabled").GetBoolean());
        Assert.Equal("Weekly", data.GetProperty("frequency").GetString());
        Assert.Equal(9, data.GetProperty("sendHour").GetInt32());
        Assert.False((await JsonOf(await bob.GetAsync("/api/email-digest"))).GetProperty("data").GetProperty("enabled").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PutAsJsonAsync("/api/email-digest", new { frequency = "Hourly" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PutAsJsonAsync("/api/email-digest", new { sendHour = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/email-digest")).StatusCode);
    }

    [Fact]
    public async Task The_desk_gets_what_installed_apps_hand_it_at_start_up()
    {
        var ann = _factory.ClientFor("ann@example.com", "Sales Manager");

        var boot = await ann.GetAsync("/api/boot");

        Assert.Equal(HttpStatusCode.OK, boot.StatusCode);
        Assert.Equal("no-store", boot.Headers.CacheControl?.ToString());
        Assert.Equal("ann@example.com", (await JsonOf(boot)).GetProperty("data").GetProperty("apps").GetProperty("testapp").GetProperty("hello").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/boot")).StatusCode);
    }

    [Fact]
    public async Task Bulk_rename_over_the_api_reports_each_document()
    {
        await CreateDocTypeAsync(new
        {
            name = "Binder",
            allowRename = true,
            autoname = "field:title",
            fields = new object[] { new { name = "title", type = "Data", required = true } },
            permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true } },
        });
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        foreach (var title in new[] { "OLD-1", "OLD-2", "KEEP" })
            await manager.PostAsJsonAsync("/api/resource/Binder", new { title });

        var response = await manager.PostAsJsonAsync("/api/resource/Binder/bulk", new
        {
            action = "rename",
            names = new[] { "OLD-1", "OLD-2", "KEEP" },
            renames = new[] { new { from = "OLD-1", to = "NEW-1" }, new { from = "OLD-2", to = "KEEP" }, new { from = "GHOST", to = "X" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(1, body.GetProperty("succeeded").GetInt32());
        Assert.Equal(2, body.GetProperty("failed").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/resource/Binder/NEW-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/resource/Binder/OLD-2")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/resource/Binder/bulk", new { action = "rename" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/resource/Binder/bulk", new { action = "rename", renames = new[] { new { from = "NEW-1", to = "Z" } } })).StatusCode);
    }
}

public sealed class LinkApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task Link_search_and_titles_show_friendly_names()
    {
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var id = (await JsonOf(await manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme Trading" }))).GetProperty("data").GetProperty("name").GetString();

        var search = await JsonOf(await manager.GetAsync("/api/link/Customer/search?q=acme"));
        var option = search.GetProperty("data")[0];
        Assert.Equal(id, option.GetProperty("value").GetString());
        Assert.Equal("Acme Trading", option.GetProperty("label").GetString());

        var names = Uri.EscapeDataString($"[\"{id}\",\"MISSING\"]");
        var titles = await JsonOf(await manager.GetAsync($"/api/link/Customer/titles?names={names}"));
        Assert.Equal("Acme Trading", titles.GetProperty("data").GetProperty(id!).GetProperty("label").GetString());
        Assert.False(titles.GetProperty("data").TryGetProperty("MISSING", out _));

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/link/Customer/titles?names=not-json")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("n@example.com", "Nobody").GetAsync("/api/link/Customer/search?q=")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/link/Customer/search?q=")).StatusCode);
    }

    [Fact]
    public async Task The_share_picker_lists_people_by_name()
    {
        var admin = _factory.ClientFor("Administrator");
        await admin.PostAsJsonAsync("/api/resource/User", new { email = "ann@example.com", full_name = "Ann Lee" });
        var seller = _factory.ClientFor("a@example.com", "Sales User");
        var id = (await JsonOf(await seller.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        var found = await JsonOf(await seller.GetAsync($"/api/share/Customer/{id}/users?q=ann"));
        Assert.Equal("Ann Lee", found.GetProperty("data")[0].GetProperty("label").GetString());

        await seller.PutAsJsonAsync($"/api/share/Customer/{id}", new { user = "ann@example.com", read = true });
        var shares = await JsonOf(await seller.GetAsync($"/api/share/Customer/{id}"));
        Assert.Equal("Ann Lee", shares.GetProperty("data")[0].GetProperty("fullName").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("b@example.com", "Sales User").GetAsync($"/api/share/Customer/{id}/users?q=a")).StatusCode);
    }
}

public sealed class CustomizeApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task Add_a_custom_field_to_a_standard_doctype_over_the_api_and_use_it()
    {
        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        var put = await sysmgr.PutAsJsonAsync("/api/customize/doctypes/Customer", new
        {
            customization = new
            {
                customFields = new[] { new { name = "custom_phone", type = "Data", label = "Phone", required = false } },
                overrides = new { customer_type = new { label = "Kind" } },
            },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var view = (await JsonOf(put)).GetProperty("data");
        Assert.True(view.GetProperty("customized").GetBoolean());
        Assert.Contains(view.GetProperty("effective").GetProperty("fields").EnumerateArray(), f => f.GetProperty("name").GetString() == "custom_phone");
        Assert.DoesNotContain(view.GetProperty("definition").GetProperty("fields").EnumerateArray(), f => f.GetProperty("name").GetString() == "custom_phone");

        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var meta = await JsonOf(await manager.GetAsync("/api/meta/Customer"));
        Assert.Contains(meta.GetProperty("data").GetProperty("fields").EnumerateArray(), f => f.GetProperty("name").GetString() == "custom_phone");
        var created = await manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme", custom_phone = "555" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("555", (await JsonOf(created)).GetProperty("data").GetProperty("custom_phone").GetString());

        var reset = await sysmgr.DeleteAsync("/api/customize/doctypes/Customer");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False((await JsonOf(reset)).GetProperty("data").GetProperty("customized").GetBoolean());
    }

    [Fact]
    public async Task The_customize_api_checks_who_asks_and_what_is_sent()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/customize/doctypes")).StatusCode);
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/customize/doctypes")).StatusCode);

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        Assert.Equal(HttpStatusCode.OK, (await sysmgr.GetAsync("/api/customize/doctypes")).StatusCode);
        var bad = await sysmgr.PutAsJsonAsync("/api/customize/doctypes/Customer", new { customization = new { customFields = new[] { new { name = "phone", type = "Data" } } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sysmgr.PutAsJsonAsync("/api/customize/doctypes/User", new { customization = new { customFields = Array.Empty<object>() } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysmgr.GetAsync("/api/customize/doctypes/Nope")).StatusCode);
    }
}

public sealed class ActivityApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task The_timeline_shows_updates_and_comments_for_people_who_can_read_the_document()
    {
        var seller = _factory.ClientFor("a@example.com", "Sales User");
        var id = (await JsonOf(await seller.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();
        await seller.PutAsJsonAsync($"/api/resource/Customer/{id}", new { credit_limit = 500 });

        var comment = await seller.PostAsJsonAsync($"/api/resource/Customer/{id}/comments", new { content = "Call them <b>Monday</b>" });
        Assert.Equal(HttpStatusCode.Created, comment.StatusCode);
        var commentId = (await JsonOf(comment)).GetProperty("data").GetProperty("id").GetString();

        var timeline = (await JsonOf(await seller.GetAsync($"/api/resource/Customer/{id}/timeline"))).GetProperty("data");
        var kinds = timeline.EnumerateArray().Select(i => i.GetProperty("kind").GetString()).ToList();
        Assert.Contains("comment", kinds);
        Assert.Equal(2, kinds.Count(k => k == "version"));
        var update = timeline.EnumerateArray().First(i => i.GetProperty("event").GetString() == "Update");
        Assert.Equal("credit_limit", update.GetProperty("data").GetProperty("changed")[0][0].GetString());
        // Comments are returned as plain text; the Desk escapes them, the server never interprets markup.
        Assert.Equal("Call them <b>Monday</b>", timeline.EnumerateArray().First(i => i.GetProperty("kind").GetString() == "comment").GetProperty("content").GetString());

        Assert.Equal(HttpStatusCode.OK, (await seller.PutAsJsonAsync($"/api/resource/Customer/{id}/comments/{commentId}", new { content = "edited" })).StatusCode);
        var comments = (await JsonOf(await seller.GetAsync($"/api/resource/Customer/{id}/comments"))).GetProperty("data");
        Assert.Equal("edited", comments[0].GetProperty("content").GetString());
        Assert.Equal(HttpStatusCode.OK, (await seller.DeleteAsync($"/api/resource/Customer/{id}/comments/{commentId}")).StatusCode);
    }

    [Fact]
    public async Task Only_readers_comment_only_authors_edit_and_bad_input_is_refused()
    {
        var seller = _factory.ClientFor("a@example.com", "Sales User");
        var other = _factory.ClientFor("b@example.com", "Sales User");
        var id = (await JsonOf(await seller.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();
        var commentId = (await JsonOf(await seller.PostAsJsonAsync($"/api/resource/Customer/{id}/comments", new { content = "mine" }))).GetProperty("data").GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/api/resource/Customer/{id}/timeline")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/resource/Customer/{id}/timeline")).StatusCode); // own-only role, not their document
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"/api/resource/Customer/{id}/comments", new { content = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync($"/api/resource/Customer/{id}/comments", new { content = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.GetAsync($"/api/resource/Customer/{id}/timeline?before=garbage")).StatusCode);

        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync($"/api/resource/Customer/{id}/comments/{commentId}", new { content = "hijack" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await seller.DeleteAsync($"/api/resource/Customer/{id}/comments/nope")).StatusCode);
    }
}

public sealed class CollaborationApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private async Task PersonAsync(string email, string name, string role)
    {
        var admin = _factory.ClientFor("Administrator");
        await admin.PostAsJsonAsync("/api/resource/Role", new { role_name = role });
        await admin.PostAsJsonAsync("/api/resource/User", new { email, full_name = name });
        await admin.PostAsJsonAsync("/api/resource/Has Role", new { user = email, role = role });
    }

    [Fact]
    public async Task Assign_follow_and_notifications_work_end_to_end()
    {
        await PersonAsync("a@example.com", "Ann", "Sales Manager");
        await PersonAsync("b@example.com", "Bob", "Sales Manager");
        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        var bob = _factory.ClientFor("b@example.com", "Sales Manager");
        var id = (await JsonOf(await ann.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        var assigned = await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/assignments", new { user = "b@example.com", description = "Call them", date = "2026-12-31", priority = "High" });
        Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
        var todo = (await JsonOf(assigned)).GetProperty("data")[0];
        Assert.Equal("Bob", todo.GetProperty("userName").GetString());

        var mine = (await JsonOf(await bob.GetAsync("/api/assignments/mine"))).GetProperty("data");
        Assert.Equal(id, mine[0].GetProperty("docName").GetString());
        var count = (await JsonOf(await bob.GetAsync("/api/notifications/count"))).GetProperty("data").GetProperty("unread").GetInt32();
        Assert.Equal(1, count);
        var inbox = (await JsonOf(await bob.GetAsync("/api/notifications?unread=true"))).GetProperty("data");
        Assert.Equal("Assignment", inbox[0].GetProperty("type").GetString());

        var notificationId = inbox[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await ann.PostAsync($"/api/notifications/{notificationId}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PostAsync($"/api/notifications/{notificationId}/read", null)).StatusCode);
        Assert.Equal(0, (await JsonOf(await bob.GetAsync("/api/notifications/count"))).GetProperty("data").GetProperty("unread").GetInt32());

        var done = await bob.PostAsync($"/api/assignments/{todo.GetProperty("id").GetString()}/complete", null);
        Assert.Equal("Closed", (await JsonOf(done)).GetProperty("data").GetProperty("status").GetString());

        var follow = await bob.GetAsync($"/api/resource/Customer/{id}/follow");
        Assert.True((await JsonOf(follow)).GetProperty("data").GetProperty("following").GetBoolean());
        var unfollowed = await bob.DeleteAsync($"/api/resource/Customer/{id}/follow");
        Assert.False((await JsonOf(unfollowed)).GetProperty("data").GetProperty("following").GetBoolean());
    }

    [Fact]
    public async Task The_collaboration_endpoints_check_who_asks()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/assignments/mine")).StatusCode);

        var seller = _factory.ClientFor("a@example.com", "Sales User");
        var id = (await JsonOf(await seller.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();
        var other = _factory.ClientFor("b@example.com", "Sales User");

        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync($"/api/resource/Customer/{id}/follow", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/resource/Customer/{id}/assignments")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PostAsJsonAsync($"/api/resource/Customer/{id}/assignments", new { user = "ghost@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.GetAsync("/api/assignments/mine?status=Cancelled")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await seller.GetAsync("/api/notifications?before=garbage")).StatusCode);
    }
}

public sealed class ListPowerApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task Settings_and_saved_filters_round_trip_and_are_validated()
    {
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");

        var start = (await JsonOf(await manager.GetAsync("/api/list-settings/Customer"))).GetProperty("data");
        Assert.Equal(JsonValueKind.Null, start.GetProperty("settings").ValueKind);

        var saved = await manager.PutAsJsonAsync("/api/list-settings/Customer/settings", new { columns = new[] { "customer_name", "modified" }, pageSize = 50, sortField = "customer_name", sortDescending = false });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(50, (await JsonOf(saved)).GetProperty("data").GetProperty("pageSize").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PutAsJsonAsync("/api/list-settings/Customer/settings", new { columns = new[] { "nope" }, pageSize = 20 })).StatusCode);

        var filter = await manager.PostAsJsonAsync("/api/list-settings/Customer/filters", new { name = "Companies", filters = new[] { new[] { "customer_type", "=", "Company" } }, isPublic = false });
        Assert.Equal(HttpStatusCode.OK, filter.StatusCode);
        var id = (await JsonOf(filter)).GetProperty("data").GetProperty("id").GetString();
        var listed = (await JsonOf(await manager.GetAsync("/api/list-settings/Customer"))).GetProperty("data");
        Assert.Equal("Companies", listed.GetProperty("filters")[0].GetProperty("name").GetString());
        Assert.Equal("customer_type", listed.GetProperty("settings").ValueKind == JsonValueKind.Object ? listed.GetProperty("filters")[0].GetProperty("filters")[0][0].GetString() : "");

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/list-settings/Customer/filters", new { name = "Bad", filters = new[] { new[] { "ghost", "=", "x" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientFor("o@example.com", "Sales Manager").DeleteAsync($"/api/list-settings/Customer/filters/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.DeleteAsync($"/api/list-settings/Customer/filters/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.DeleteAsync("/api/list-settings/Customer/settings")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/list-settings/Customer")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("n@example.com", "Nobody").GetAsync("/api/list-settings/Customer")).StatusCode);
    }

    [Fact]
    public async Task Bulk_update_and_delete_report_each_document()
    {
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var ids = new List<string>();
        foreach (var name in new[] { "A", "B", "C" })
            ids.Add((await JsonOf(await manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = name }))).GetProperty("data").GetProperty("name").GetString()!);

        var update = await manager.PostAsJsonAsync("/api/resource/Customer/bulk", new { action = "update", names = ids.Append("MISSING"), field = "credit_limit", value = 900 });
        var body = await JsonOf(update);
        Assert.Equal(3, body.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, body.GetProperty("failed").GetInt32());
        Assert.Equal(900m, (await JsonOf(await manager.GetAsync($"/api/resource/Customer/{ids[0]}"))).GetProperty("data").GetProperty("credit_limit").GetDecimal());

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/resource/Customer/bulk", new { action = "explode", names = ids })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/resource/Customer/bulk", new { action = "update", names = ids, field = "name", value = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/resource/Customer/bulk", new { action = "delete", names = Array.Empty<string>() })).StatusCode);

        var delete = await JsonOf(await manager.PostAsJsonAsync("/api/resource/Customer/bulk", new { action = "delete", names = ids }));
        Assert.Equal(3, delete.GetProperty("succeeded").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/resource/Customer/bulk", new { action = "delete", names = ids })).StatusCode);
    }
}

public sealed class ListSidebarApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task Group_by_counts_and_comment_counts_are_served_to_people_who_can_list()
    {
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        string? first = null;
        foreach (var type in new[] { "Company", "Company", "Individual" })
        {
            var id = (await JsonOf(await manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = Guid.NewGuid().ToString("N")[..6], customer_type = type }))).GetProperty("data").GetProperty("name").GetString();
            first ??= id;
        }

        var groups = (await JsonOf(await manager.GetAsync("/api/resource/Customer/group-by?field=customer_type"))).GetProperty("data");
        Assert.Equal("Company", groups[0].GetProperty("value").GetString());
        Assert.Equal(2, groups[0].GetProperty("count").GetInt32());
        var filtered = (await JsonOf(await manager.GetAsync("/api/resource/Customer/group-by?field=customer_type&filters=" + Uri.EscapeDataString("[[\"customer_type\",\"=\",\"Individual\"]]")))).GetProperty("data");
        Assert.Single(filtered.EnumerateArray());

        await manager.PostAsJsonAsync($"/api/resource/Customer/{first}/comments", new { content = "hello" });
        var counts = (await JsonOf(await manager.GetAsync("/api/resource/Customer/comment-counts?names=" + Uri.EscapeDataString($"[\"{first}\"]")))).GetProperty("data");
        Assert.Equal(1, counts.GetProperty(first!).GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/resource/Customer/group-by?field=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/resource/Customer/comment-counts?names=not-json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/resource/Customer/group-by?field=customer_type")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("n@example.com", "Nobody").GetAsync("/api/resource/Customer/group-by?field=customer_type")).StatusCode);
    }
}
