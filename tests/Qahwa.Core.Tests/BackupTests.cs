using System.IO.Compression;
using System.Text;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class BackupTests
{
    private readonly TestEngine _source = new();
    private readonly TestEngine _target = new();

    private const string Order = """
        { "name": "Basket", "autoname": "BSK-.####", "fields": [
            { "name": "customer", "type": "Data", "required": true }, { "name": "total", "type": "Currency" }, { "name": "due", "type": "Date" },
            { "name": "paid", "type": "Check" }, { "name": "lines", "type": "Table", "options": "Basket Line" } ],
          "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
        """;

    private const string Line = """{ "name": "Basket Line", "isChildTable": true, "fields": [ { "name": "item", "type": "Data" }, { "name": "qty", "type": "Int" } ] }""";

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    public BackupTests()
    {
        foreach (var engine in new[] { _source, _target })
        {
            engine.Registry.Add(DocTypeLoader.Parse(Line));
            engine.Registry.Add(DocTypeLoader.Parse(Order));
        }
    }

    private async Task<string> MakeBasketAsync(TestEngine engine, string customer, decimal total = 10.5m)
    {
        engine.Clock.Advance(TimeSpan.FromSeconds(1));
        var doc = await engine.Documents.InsertAsync("Basket", new Dictionary<string, object?>
        {
            ["customer"] = customer, ["total"] = total, ["due"] = "2026-03-04", ["paid"] = true,
            ["lines"] = new List<Dictionary<string, object?>> { new() { ["item"] = "Beans", ["qty"] = 2 }, new() { ["item"] = "Milk", ["qty"] = 5 } },
        }, Ann);
        return (string)doc["name"]!;
    }

    private static async Task<MemoryStream> BackupAsync(TestEngine engine, IUserContext? user = null)
    {
        var stream = new MemoryStream();
        await engine.Backup.WriteAsync(stream, user ?? TestUsers.Admin);
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream Zip(params (string Name, string Text)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var writer = new StreamWriter(zip.CreateEntry(name).Open()))
                    writer.Write(text);
        stream.Position = 0;
        return stream;
    }

    private const string Manifest = """{ "format": "qahwa-backup", "version": 1, "createdAt": "2026-03-01T00:00:00Z", "tables": [ { "docType": "Basket", "file": "data/0.jsonl", "rows": 1 } ], "series": [] }""";

    [Fact]
    public async Task What_was_backed_up_comes_back_into_an_empty_site_with_its_rows_values_and_naming_counters()
    {
        var one = await MakeBasketAsync(_source, "Acme", 10.5m);
        var two = await MakeBasketAsync(_source, "Globex", 99m);
        await using var backup = await BackupAsync(_source);

        var report = await _target.Backup.RestoreAsync(backup, dryRun: false, TestUsers.Admin);

        var baskets = report.Tables.Single(t => t.DocType == "Basket");
        Assert.Equal(2, baskets.Inserted);
        Assert.Equal(0, baskets.Failed);
        Assert.Equal(4, report.Tables.Single(t => t.DocType == "Basket Line").Inserted);
        var restored = await _target.Documents.GetAsync("Basket", one, Ann);
        Assert.Equal("Acme", restored["customer"]);
        Assert.Equal(10.5m, Convert.ToDecimal(restored["total"]));
        Assert.Equal(true, restored["paid"]);
        Assert.Equal(2, ((IEnumerable<object?>)restored["lines"]!).Count());
        Assert.Equal((await _source.Documents.GetAsync("Basket", two, Ann))["creation"], (await _target.Documents.GetAsync("Basket", two, Ann))["creation"]);
        Assert.Contains(await _target.Repository.ListSeriesAsync(), s => s.Prefix.StartsWith("BSK-") && s.Current == 2); // the next basket is BSK-0003, not a clash

        var third = await _target.Documents.InsertAsync("Basket", Values(("customer", "New")), Ann);
        Assert.NotEqual(one, third["name"]);
        Assert.NotEqual(two, third["name"]);
    }

    [Fact]
    public async Task Restoring_only_adds_and_never_overwrites_or_deletes()
    {
        var name = await MakeBasketAsync(_source, "Original");
        await using var backup = await BackupAsync(_source);
        await _target.Backup.RestoreAsync(await BackupAsync(_source), false, TestUsers.Admin);
        await _target.Documents.UpdateAsync("Basket", name, Values(("customer", "Changed after the backup")), Ann);
        var extra = await MakeBasketAsync(_target, "Only here");

        var again = await _target.Backup.RestoreAsync(backup, false, TestUsers.Admin);

        Assert.Equal(1, again.Tables.Single(t => t.DocType == "Basket").Existing);
        Assert.Equal(0, again.Tables.Single(t => t.DocType == "Basket").Inserted);
        Assert.Equal("Changed after the backup", (await _target.Documents.GetAsync("Basket", name, Ann))["customer"]);
        Assert.Equal("Only here", (await _target.Documents.GetAsync("Basket", extra, Ann))["customer"]);
    }

    [Fact]
    public async Task A_preview_changes_nothing()
    {
        await MakeBasketAsync(_source, "Acme");
        await using var backup = await BackupAsync(_source);

        var report = await _target.Backup.RestoreAsync(backup, dryRun: true, TestUsers.Admin);

        Assert.True(report.DryRun);
        Assert.Equal(1, report.Tables.Single(t => t.DocType == "Basket").Inserted);
        Assert.Empty((await _target.Documents.ListAsync("Basket", new ListRequest { Limit = 10 }, TestUsers.Admin)).Data);
        Assert.Empty(await _target.Repository.ListSeriesAsync());
    }

    [Fact]
    public async Task Passwords_sessions_and_queues_are_never_in_a_backup()
    {
        await _source.Auth.EnsureUserAsync("ann@example.com", "Ann", "a-long-passphrase-9", ["Clerk"]);
        await _source.Email.EnqueueAsync(["x@example.com"], "Secret subject", "Secret body", null, null, "ann@example.com");
        await using var backup = await BackupAsync(_source);

        using var zip = new ZipArchive(backup, ZipArchiveMode.Read);
        var everything = string.Concat(zip.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));

        Assert.DoesNotContain("AQAAAA", everything); // the password hash format
        Assert.DoesNotContain("a-long-passphrase-9", everything);
        Assert.DoesNotContain("Secret body", everything);
        Assert.Contains("ann@example.com", everything); // the User document itself is there
        var manifest = System.Text.Json.JsonDocument.Parse(new StreamReader(zip.GetEntry("manifest.json")!.Open()).ReadToEnd()).RootElement;
        Assert.DoesNotContain(manifest.GetProperty("tables").EnumerateArray(), t => BackupService.Skipped.Contains(t.GetProperty("docType").GetString()!));
    }

    [Fact]
    public async Task Definitions_made_in_the_desk_travel_with_it_and_exist_before_their_rows()
    {
        await _source.Designer.CreateAsync(DocTypeLoader.Parse("""{ "name": "Gadget", "fields": [ { "name": "label", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""), TestUsers.SystemManager);
        await _source.Documents.InsertAsync("Gadget", Values(("label", "G1")), Ann);
        await using var backup = await BackupAsync(_source);

        var report = await _target.Backup.RestoreAsync(backup, false, TestUsers.Admin);

        Assert.Contains(report.Definitions, d => d.DocType == "Gadget" && d.Ok);
        Assert.Equal(1, report.Tables.Single(t => t.DocType == "Gadget").Inserted);
    }

    [Fact]
    public async Task Only_an_administrator_can_make_or_restore_one()
    {
        var manager = TestUsers.SystemManager;
        await Assert.ThrowsAsync<PermissionDeniedException>(() => BackupAsync(_source, manager));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _target.Backup.RestoreAsync(Zip(), true, manager));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => BackupAsync(_source, UserContext.Anonymous));
    }

    [Fact]
    public async Task Files_that_are_not_backups_are_refused_with_a_reason()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => _target.Backup.RestoreAsync(new MemoryStream(Encoding.UTF8.GetBytes("not a zip at all")), true, TestUsers.Admin));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _target.Backup.RestoreAsync(Zip(("readme.txt", "hi")), true, TestUsers.Admin)); // no manifest
        await Assert.ThrowsAsync<DocumentValidationException>(() => _target.Backup.RestoreAsync(Zip(("manifest.json", "{ not json")), true, TestUsers.Admin));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _target.Backup.RestoreAsync(Zip(("manifest.json", """{ "format": "other", "version": 1, "tables": [], "series": [] }""")), true, TestUsers.Admin));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _target.Backup.RestoreAsync(Zip(("manifest.json", """{ "format": "qahwa-backup", "version": 99, "tables": [], "series": [] }""")), true, TestUsers.Admin));
    }

    [Fact]
    public async Task A_manifest_cannot_point_at_other_files_or_unknown_or_skipped_doctypes()
    {
        var manifest = """
            { "format": "qahwa-backup", "version": 1, "createdAt": "2026-03-01T00:00:00Z", "series": [], "tables": [
              { "docType": "Basket", "file": "../../etc/passwd", "rows": 1 },
              { "docType": "Basket", "file": "manifest.json", "rows": 1 },
              { "docType": "Nowhere", "file": "data/0.jsonl", "rows": 1 },
              { "docType": "Email Queue", "file": "data/0.jsonl", "rows": 1 } ] }
            """;

        var report = await _target.Backup.RestoreAsync(Zip(("manifest.json", manifest), ("data/0.jsonl", """{ "name": "x", "customer": "c" }""")), false, TestUsers.Admin);

        Assert.All(report.Tables, t => Assert.Equal(0, t.Inserted));
        Assert.All(report.Tables, t => Assert.NotNull(t.FirstProblem));
        Assert.Empty((await _target.Documents.ListAsync("Basket", new ListRequest { Limit = 10 }, TestUsers.Admin)).Data);
    }

    [Fact]
    public async Task Bad_rows_are_counted_and_do_not_stop_the_good_ones()
    {
        var rows = string.Join("\n",
            """{ "name": "OK-1", "customer": "Fine", "total": 5 }""",
            "this is not json",
            """{ "name": "", "customer": "No name" }""",
            """{ "customer": "No name key" }""",
            """{ "name": "BAD-DATE", "customer": "x", "due": "not a date" }""",
            """{ "name": "BAD-STATUS", "customer": "x", "docstatus": 7 }""",
            """{ "name": "OK-2", "customer": "Also fine", "unknown_column": "ignored", "owner": "ann@example.com" }""",
            """[1,2,3]""");

        var report = await _target.Backup.RestoreAsync(Zip(("manifest.json", Manifest), ("data/0.jsonl", rows)), false, TestUsers.Admin);

        var table = report.Tables.Single();
        Assert.Equal(2, table.Inserted);
        Assert.Equal(6, table.Failed);
        Assert.NotNull(table.FirstProblem);
        Assert.Equal("ann@example.com", (await _target.Documents.GetAsync("Basket", "OK-2", Ann))["owner"]);
    }

    [Fact]
    public async Task A_row_made_to_hurt_is_just_text()
    {
        var rows = """{ "name": "A'; DROP TABLE \"tabBasket\"; --", "customer": "<script>alert(1)</script>" }""";

        await _target.Backup.RestoreAsync(Zip(("manifest.json", Manifest), ("data/0.jsonl", rows)), false, TestUsers.Admin);

        var saved = await _target.Documents.GetAsync("Basket", "A'; DROP TABLE \"tabBasket\"; --", Ann);
        Assert.Equal("<script>alert(1)</script>", saved["customer"]);
    }

    [Fact]
    public async Task A_backup_that_unpacks_to_more_than_the_limit_is_refused_even_if_it_is_small_to_send()
    {
        var bomb = string.Join("\n", Enumerable.Repeat("""{ "name": "X", "customer": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }""", 5000));
        var zip = Zip(("manifest.json", Manifest), ("data/0.jsonl", bomb));

        await Assert.ThrowsAsync<DocumentValidationException>(() => _target.Backup.RestoreAsync(zip, true, TestUsers.Admin, default, maxBytes: 20_000));
    }

    [Fact]
    public async Task The_daily_file_is_made_once_and_only_the_newest_few_are_kept()
    {
        var folder = Path.Combine(Path.GetTempPath(), "qahwa-backup-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await MakeBasketAsync(_source, "Acme");
            var system = new UserContext("system@qahwa", [UserContext.AdministratorRole]);
            Directory.CreateDirectory(folder);
            foreach (var old in new[] { "20200101", "20200102", "20200103" })
                File.WriteAllText(Path.Combine(folder, $"qahwa-backup-{old}.zip"), "old");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine");

            var first = await _source.Backup.RunScheduledAsync(folder, 2, system);
            var second = await _source.Backup.RunScheduledAsync(folder, 2, system);

            Assert.NotNull(first);
            Assert.Null(second); // already made today
            Assert.Equal(2, Directory.GetFiles(folder, "qahwa-backup-????????.zip").Length);
            Assert.True(File.Exists(Path.Combine(folder, "notes.txt"))); // only its own files are ever deleted
            Assert.Empty(Directory.GetFiles(folder, "*.part"));
            using var zip = ZipFile.OpenRead(first);
            Assert.NotNull(zip.GetEntry("manifest.json"));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
    }
}
