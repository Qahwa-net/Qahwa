using System.Net;
using System.Text.Json;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class WebhookTests
{
    private const string Secret = "a-long-enough-shared-secret";
    private const string Url = "https://hooks.example.com/qahwa";

    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    public WebhookTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Shipment", "isSubmittable": true, "autoname": "SHP-.####", "fields": [
                { "name": "customer", "type": "Data" }, { "name": "status", "type": "Data" }, { "name": "amount", "type": "Currency" },
                { "name": "margin", "type": "Currency", "permLevel": 1 }, { "name": "pin", "type": "Password" }, { "name": "notes", "type": "Text" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true, "submit": true, "cancel": true } ] }
            """));
    }

    private static Webhook Hook(string name = "Notify", string @event = Webhook.New, string url = Url, string? secret = Secret) =>
        new() { Name = name, Event = @event, Url = url, Secret = secret };

    private Task<WebhookView> SaveAsync(params Webhook[] hooks) =>
        _engine.WebhookManager.SaveAsync("Shipment", new WebhookSet { DocType = "Shipment", Hooks = hooks }, null, TestUsers.SystemManager);

    private async Task<string> ShipmentAsync(params (string, object?)[] extra)
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        var values = Values(("customer", "Acme"), ("amount", 120));
        foreach (var (key, value) in extra)
            values[key] = value;
        return (string)(await _engine.Documents.InsertAsync("Shipment", values, Ann))["name"]!;
    }

    private async Task<List<Dictionary<string, object?>>> DeliveriesAsync() =>
        (await _engine.Documents.ListAsync("Webhook Delivery", new ListRequest { Fields = ["webhook", "event", "status", "attempts", "last_error", "response_code", "reference_name"], OrderBy = "creation", Descending = false, Limit = 100 }, TestUsers.Admin)).Data.ToList();

    // ---- extra headers and the test button ----

    private static Webhook WithHeaders(params (string Name, string? Value)[] headers) =>
        Hook() with { Headers = headers.Select(h => new WebhookHeader { Name = h.Name, Value = h.Value }).ToList() };

    [Fact]
    public async Task Extra_headers_are_sent_but_never_shown_again_and_a_blank_value_keeps_the_stored_one()
    {
        var saved = await SaveAsync(WithHeaders(("Authorization", "Bearer s3cret-token"), ("X-Tenant", "acme")));

        var shown = saved.Set.Hooks.Single().Headers;
        Assert.Equal(["Authorization", "X-Tenant"], shown.Select(h => h.Name));
        Assert.All(shown, h => { Assert.Null(h.Value); Assert.True(h.ValueSet); });

        // Saving again with the values left blank keeps them; changing one replaces only that one.
        await _engine.WebhookManager.SaveAsync("Shipment", new WebhookSet { DocType = "Shipment", Hooks = [WithHeaders(("Authorization", ""), ("X-Tenant", "globex")) with { Secret = null }] }, saved.Modified, TestUsers.SystemManager);
        await ShipmentAsync();
        await _engine.Webhooks.DeliverPendingAsync();

        var headers = Assert.Single(_engine.WebhookSender.Sent).Headers;
        Assert.Equal("Bearer s3cret-token", headers["Authorization"]);
        Assert.Equal("globex", headers["X-Tenant"]);
        Assert.StartsWith("t=", headers["X-Qahwa-Signature"]); // Qahwa's own headers are still there
    }

    [Fact]
    public async Task Headers_that_belong_to_the_connection_or_to_Qahwa_are_refused_with_the_reason()
    {
        async Task<DocumentValidationException> Fails(params (string, string?)[] headers) =>
            await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(WithHeaders(headers)));

        Assert.Contains((await Fails(("Host", "evil.example"))).Errors, e => e.Field == "hooks[0].headers[0].name");
        Assert.Contains((await Fails(("X-Qahwa-Signature", "forged"))).Errors, e => e.Field == "hooks[0].headers[0].name");
        Assert.Contains((await Fails(("Content-Length", "1"))).Errors, e => e.Field == "hooks[0].headers[0].name");
        Assert.Contains((await Fails(("Bad Name", "x"))).Errors, e => e.Field == "hooks[0].headers[0].name");
        Assert.Contains((await Fails(("X-A", "x"), ("x-a", "y"))).Errors, e => e.Message.Contains("already used"));
        Assert.Contains((await Fails(("X-A", "line one\r\nX-Injected: yes"))).Errors, e => e.Field == "hooks[0].headers[0].value");
        Assert.Contains((await Fails(("X-A", ""))).Errors, e => e.Field == "hooks[0].headers[0].value");
        Assert.Contains((await Fails(Enumerable.Range(0, 11).Select(i => ($"X-H{i}", (string?)"v")).ToArray())).Errors, e => e.Field == "hooks[0].headers");
    }

    [Fact]
    public async Task The_test_button_sends_a_signed_ping_with_no_document_data_and_reports_the_answer()
    {
        await SaveAsync(WithHeaders(("X-Tenant", "acme")) with { Active = false }); // works even when switched off

        var result = await _engine.Webhooks.TestAsync("Shipment", "Notify", TestUsers.SystemManager);

        Assert.True(result.Ok);
        var sent = Assert.Single(_engine.WebhookSender.Sent);
        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal("ping", body.GetProperty("event").GetString());
        Assert.True(body.GetProperty("test").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal("acme", sent.Headers["X-Tenant"]);
        var parts = sent.Headers["X-Qahwa-Signature"].Split(',');
        Assert.Equal(WebhookService.Sign(Secret, long.Parse(parts[0]["t=".Length..]), sent.Body), parts[1]["v1=".Length..]);
        Assert.Empty(await DeliveriesAsync()); // a test is not a delivery

        await Assert.ThrowsAsync<ConflictException>(() => _engine.Webhooks.TestAsync("Shipment", "Notify", TestUsers.SystemManager)); // too soon
        _engine.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True((await _engine.Webhooks.TestAsync("Shipment", "Notify", TestUsers.SystemManager)).Ok);
    }

    [Fact]
    public async Task The_test_button_needs_a_saved_webhook_a_manager_and_shows_a_failing_receiver()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Webhooks.TestAsync("Shipment", "Notify", TestUsers.SystemManager));
        await SaveAsync(Hook());
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Webhooks.TestAsync("Shipment", "Notify", Ann));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Webhooks.TestAsync("Shipment", "Other", TestUsers.SystemManager));

        _engine.WebhookSender.Status = 500;
        var result = await _engine.Webhooks.TestAsync("Shipment", "Notify", TestUsers.SystemManager);
        Assert.False(result.Ok);
        Assert.Equal(500, result.StatusCode);
    }

    // ---- events become signed deliveries ----

    [Fact]
    public async Task A_new_document_is_posted_signed_with_only_the_fields_everyone_may_see()
    {
        await SaveAsync(Hook());
        var id = await ShipmentAsync(("notes", "n"));
        await _engine.Documents.UpdateAsync("Shipment", id, Values(("margin", 30)), TestUsers.Admin); // a field only higher levels may see

        var run = await _engine.Webhooks.DeliverPendingAsync();

        Assert.Equal((1, 0), run);
        var sent = Assert.Single(_engine.WebhookSender.Sent);
        Assert.Equal(Url, sent.Url);
        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal(("New", "Shipment", id), (body.GetProperty("event").GetString(), body.GetProperty("doctype").GetString(), body.GetProperty("name").GetString()));
        var data = body.GetProperty("data");
        Assert.Equal("Acme", data.GetProperty("customer").GetString());
        Assert.False(data.TryGetProperty("margin", out _)); // a higher permission level
        Assert.False(data.TryGetProperty("pin", out _));    // never passwords
        Assert.Equal("New", sent.Headers["X-Qahwa-Event"]);

        var header = sent.Headers["X-Qahwa-Signature"];
        var parts = header.Split(',');
        var t = long.Parse(parts[0]["t=".Length..]);
        Assert.Equal(WebhookService.Sign(Secret, t, sent.Body), parts[1]["v1=".Length..]);
        Assert.NotEqual(WebhookService.Sign("another-secret-entirely", t, sent.Body), parts[1]["v1=".Length..]);
        Assert.Equal("Sent", (string)Assert.Single(await DeliveriesAsync())["status"]!);
    }

    [Fact]
    public async Task Update_submit_cancel_and_delete_each_have_their_own_event_and_only_real_changes_count()
    {
        await SaveAsync(Hook("U", Webhook.Update), Hook("S", Webhook.Submit), Hook("C", Webhook.Cancel), Hook("D", Webhook.Delete));
        var id = await ShipmentAsync();
        Assert.Empty(await DeliveriesAsync()); // none of them is about creating

        await _engine.Documents.UpdateAsync("Shipment", id, Values(("customer", "Acme")), Ann); // same value
        Assert.Empty(await DeliveriesAsync());
        await _engine.Documents.UpdateAsync("Shipment", id, Values(("customer", "Bolt")), Ann);
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Documents.SubmitAsync("Shipment", id, Ann);
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Documents.CancelAsync("Shipment", id, Ann);
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        var gone = await ShipmentAsync();
        await _engine.Documents.DeleteAsync("Shipment", gone, Ann);

        Assert.Equal(["Update", "Submit", "Cancel", "Delete"], (await DeliveriesAsync()).Select(d => (string)d["event"]!));
        await _engine.Webhooks.DeliverPendingAsync();
        var deleted = JsonDocument.Parse(_engine.WebhookSender.Sent.Last().Body).RootElement;
        Assert.Equal(("Delete", gone, "Acme"), (deleted.GetProperty("event").GetString(), deleted.GetProperty("name").GetString(), deleted.GetProperty("data").GetProperty("customer").GetString()));
    }

    [Fact]
    public async Task A_condition_and_a_field_list_shape_what_is_sent()
    {
        await SaveAsync(Hook() with { Condition = "amount > 100", Fields = ["customer", "amount"] });

        await ShipmentAsync(("amount", 50));
        var big = await ShipmentAsync(("amount", 500));
        await _engine.Webhooks.DeliverPendingAsync();

        var body = JsonDocument.Parse(Assert.Single(_engine.WebhookSender.Sent).Body).RootElement;
        Assert.Equal(big, body.GetProperty("name").GetString());
        Assert.Equal(["amount", "customer"], body.GetProperty("data").EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task A_huge_document_is_sent_as_a_note_that_it_changed_not_as_a_huge_post()
    {
        await SaveAsync(Hook() with { Fields = ["notes"] });
        await ShipmentAsync(("notes", new string('x', 100)));
        var big = await ShipmentAsync();
        await _engine.Documents.UpdateAsync("Shipment", big, Values(("notes", "short")), Ann);
        // A document too big for the limit is trimmed to its name and a flag.
        var hugeText = new string('y', WebhookService.MaxPayload);
        await _engine.Repository.UpdateAsync(_engine.Registry.Get("Shipment"), big, new Dictionary<string, object?> { ["notes"] = hugeText }, null);
        var third = await ShipmentAsync(("notes", hugeText[..60_000]));
        Assert.NotNull(third);

        await _engine.Webhooks.DeliverPendingAsync();

        Assert.All(_engine.WebhookSender.Sent, s => Assert.True(s.Body.Length < WebhookService.MaxPayload + 2000));
    }

    // ---- delivery: retries, claims, removal ----

    [Fact]
    public async Task A_failing_receiver_is_retried_later_with_growing_delays_then_given_up_on()
    {
        await SaveAsync(Hook());
        await ShipmentAsync();
        _engine.WebhookSender.Status = 500;

        Assert.Equal((0, 1), await _engine.Webhooks.DeliverPendingAsync());
        Assert.Equal((0, 0), await _engine.Webhooks.DeliverPendingAsync()); // not due yet

        foreach (var wait in new[] { 2, 6, 40, 150 })
        {
            _engine.Clock.Advance(TimeSpan.FromMinutes(wait));
            await _engine.Webhooks.DeliverPendingAsync();
        }

        var row = Assert.Single(await DeliveriesAsync());
        Assert.Equal(("Failed", (long)WebhookService.MaxAttempts, 500L), ((string)row["status"]!, Convert.ToInt64(row["attempts"]), Convert.ToInt64(row["response_code"])));
        Assert.Equal(WebhookService.MaxAttempts, _engine.WebhookSender.Sent.Count);
        _engine.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal((0, 0), await _engine.Webhooks.DeliverPendingAsync());
    }

    [Fact]
    public async Task A_receiver_that_recovers_gets_the_delivery_on_the_retry()
    {
        await SaveAsync(Hook());
        await ShipmentAsync();
        _engine.WebhookSender.Status = 503;
        await _engine.Webhooks.DeliverPendingAsync();

        _engine.WebhookSender.Status = 204;
        _engine.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal((1, 0), await _engine.Webhooks.DeliverPendingAsync());

        Assert.Equal("Sent", (string)Assert.Single(await DeliveriesAsync())["status"]!);
    }

    [Fact]
    public async Task A_sender_that_throws_is_a_failed_attempt_with_its_reason()
    {
        await SaveAsync(Hook());
        await ShipmentAsync();
        _engine.WebhookSender.Throw = new InvalidOperationException("connection refused");

        Assert.Equal((0, 1), await _engine.Webhooks.DeliverPendingAsync());

        var row = Assert.Single(await DeliveriesAsync());
        Assert.Equal(("Pending", "connection refused"), ((string)row["status"]!, (string)row["last_error"]!));
    }

    [Fact]
    public async Task Several_servers_delivering_at_once_send_each_delivery_once()
    {
        await SaveAsync(Hook());
        for (var i = 0; i < 6; i++)
            await ShipmentAsync();

        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _engine.Webhooks.DeliverPendingAsync()));

        Assert.Equal(6, runs.Sum(r => r.Sent));
        Assert.Equal(6, _engine.WebhookSender.Sent.Select(s => s.Headers["X-Qahwa-Delivery"]).Distinct().Count());
    }

    [Fact]
    public async Task A_delivery_for_a_removed_or_switched_off_hook_fails_without_sending()
    {
        await SaveAsync(Hook());
        await ShipmentAsync();
        await _engine.WebhookManager.DeleteAsync("Shipment", TestUsers.SystemManager);

        await _engine.Webhooks.DeliverPendingAsync();

        Assert.Empty(_engine.WebhookSender.Sent);
        Assert.Equal("Failed", (string)Assert.Single(await DeliveriesAsync())["status"]!);
    }

    // ---- managing hooks ----

    [Theory]
    [InlineData("http://hooks.example.com/x", "Only https")]
    [InlineData("ftp://hooks.example.com/x", "Only https")]
    [InlineData("not a url", "address")]
    [InlineData("https://localhost/x", "Internal")]
    [InlineData("https://service.localhost/x", "Internal")]
    [InlineData("https://printer.local/x", "Internal")]
    [InlineData("https://db.internal/x", "Internal")]
    [InlineData("https://intranet/x", "Internal")]
    [InlineData("https://127.0.0.1/x", "Private")]
    [InlineData("https://10.1.2.3/x", "Private")]
    [InlineData("https://192.168.0.5/x", "Private")]
    [InlineData("https://172.16.0.1/x", "Private")]
    [InlineData("https://169.254.169.254/latest/meta-data", "Private")]
    [InlineData("https://[::1]/x", "Private")]
    [InlineData("https://[fd00::1]/x", "Private")]
    [InlineData("https://user:pass@hooks.example.com/x", "user name")]
    [InlineData("", "address")]
    public async Task Addresses_that_are_not_public_https_are_refused(string url, string reason)
    {
        var errors = (await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(Hook(url: url)))).Errors;
        Assert.Contains(errors, e => e.Field == "hooks[0].url" && e.Message.Contains(reason, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.1.2.3", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("2001:db8::1", false)]
    public void Only_public_addresses_pass_the_network_guard(string address, bool allowed) =>
        Assert.Equal(allowed, NetworkGuard.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task Problems_are_reported_by_hook_and_field()
    {
        var errors = (await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(
            Hook("", secret: Secret),
            Hook("Dup", secret: Secret),
            Hook("dup", secret: Secret),
            Hook("Odd", "Sometimes"),
            Hook("Short", secret: "short"),
            Hook("NoSecret", secret: null),
            Hook("Cond") with { Condition = "nope == 1" },
            Hook("Fields") with { Fields = ["pin", "nope"] }))).Errors.Select(e => e.Field).ToHashSet();

        foreach (var expected in new[] { "hooks[0].name", "hooks[2].name", "hooks[3].event", "hooks[4].secret", "hooks[5].secret", "hooks[6].condition", "hooks[7].fields" })
            Assert.Contains(expected, errors);
    }

    [Fact]
    public async Task At_most_ten_hooks_per_doctype()
    {
        var many = Enumerable.Range(0, 11).Select(i => Hook($"H{i}")).ToArray();
        Assert.Contains("hooks", (await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(many))).Errors.Select(e => e.Field));
    }

    [Fact]
    public async Task The_secret_is_write_only_and_a_blank_secret_keeps_the_stored_one()
    {
        var saved = await SaveAsync(Hook());
        var view = await _engine.WebhookManager.GetAsync("Shipment", TestUsers.SystemManager);
        var hook = Assert.Single(view.Set.Hooks);
        Assert.Equal((null, true), (hook.Secret, hook.SecretSet));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(view));

        await _engine.WebhookManager.SaveAsync("Shipment", view.Set with { Hooks = [hook with { Event = Webhook.Update, Secret = "" }] }, saved.Modified, TestUsers.SystemManager);
        await ShipmentAsync();
        var id = (await _engine.Documents.ListAsync("Shipment", new ListRequest { Fields = ["name"], Limit = 1 }, Ann)).Data[0]["name"]!.ToString()!;
        await _engine.Documents.UpdateAsync("Shipment", id, Values(("customer", "Changed")), Ann);
        await _engine.Webhooks.DeliverPendingAsync();

        var t = long.Parse(_engine.WebhookSender.Sent.Single().Headers["X-Qahwa-Signature"].Split(',')[0]["t=".Length..]);
        Assert.Equal(WebhookService.Sign(Secret, t, _engine.WebhookSender.Sent.Single().Body), _engine.WebhookSender.Sent.Single().Headers["X-Qahwa-Signature"].Split(',')[1]["v1=".Length..]);
    }

    [Fact]
    public async Task Only_system_managers_manage_webhooks_stale_saves_conflict_and_hooks_survive_a_restart()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.WebhookManager.ListAsync(Ann));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.WebhookManager.ListAsync(UserContext.Anonymous));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WebhookManager.GetAsync("ToDo", TestUsers.SystemManager));

        await SaveAsync(Hook());
        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(Hook("Other")));

        var restarted = new WebhookCatalog();
        var manager = new WebhookManager(_engine.Registry, _engine.WebhookStore, restarted, _engine.Permissions, _engine.Clock);
        Assert.Empty(await manager.LoadAsync());
        Assert.Equal(Secret, restarted.Find("Shipment", "Notify")!.Secret); // the secret is kept for signing
    }

    [Fact]
    public async Task Insecure_and_local_addresses_are_only_possible_when_development_allows_them()
    {
        var dev = new WebhookManager(_engine.Registry, _engine.WebhookStore, _engine.WebhookCatalog, _engine.Permissions, _engine.Clock, allowInsecure: true);

        await dev.SaveAsync("Shipment", new WebhookSet { DocType = "Shipment", Hooks = [Hook(url: "http://localhost:5000/hook")] }, null, TestUsers.SystemManager);

        Assert.NotNull(_engine.WebhookCatalog.Find("Shipment", "Notify"));
    }
}
