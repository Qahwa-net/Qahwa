using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;
using Qahwa.Core.Expressions;

namespace Qahwa.Core;

/// <summary>One webhook: when something happens to a document, tell another system by posting JSON to its address.</summary>
public sealed record Webhook
{
    public const string New = "New";
    public const string Update = "Update";
    public const string Submit = "Submit";
    public const string Cancel = "Cancel";
    public const string Delete = "Delete";
    public static readonly string[] Events = [New, Update, Submit, Cancel, Delete];

    public const int MaxHooks = 10;
    public const int MaxUrl = 500;
    public const int MaxCondition = 500;
    public const int MinSecret = 16;
    public const int MaxSecret = 200;

    public required string Name { get; init; }
    public bool Active { get; init; } = true;
    public string Event { get; init; } = New;
    public string? Condition { get; init; }

    /// <summary>Where to post. https only (and never a private or internal address).</summary>
    public required string Url { get; init; }

    /// <summary>Signs each delivery. Write-only: it is never returned, and a blank value on save keeps the stored one.</summary>
    public string? Secret { get; init; }

    /// <summary>True when a secret is stored (what the API returns instead of the secret).</summary>
    public bool SecretSet { get; init; }

    /// <summary>The fields to send; empty means every field a person at the lowest permission level could see (never passwords).</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];
}

public sealed record WebhookSet
{
    public required string DocType { get; init; }
    public IReadOnlyList<Webhook> Hooks { get; init; } = [];
}

public sealed record StoredWebhooks(string DocType, string Definition, DateTime Modified, string ModifiedBy);

/// <summary>Settings for webhooks. Insecure (http, local) addresses are only ever for development.</summary>
public sealed class WebhookOptions
{
    public bool AllowInsecure { get; set; }
}

public interface IWebhookStore
{
    Task<IReadOnlyList<StoredWebhooks>> ListAsync(CancellationToken ct = default);

    Task<StoredWebhooks?> GetAsync(string docType, CancellationToken ct = default);

    /// <param name="expectedModified">Null when the DocType has no webhooks yet.</param>
    Task<bool> SaveAsync(StoredWebhooks row, DateTime? expectedModified, CancellationToken ct = default);

    Task<bool> DeleteAsync(string docType, CancellationToken ct = default);
}

public sealed class WebhookCatalog
{
    private ImmutableDictionary<string, WebhookSet> _sets = ImmutableDictionary.Create<string, WebhookSet>(StringComparer.OrdinalIgnoreCase);

    public WebhookSet? For(string doctype) => _sets.GetValueOrDefault(doctype);

    /// <summary>The hook with this name, if it is still active (a delivery queued earlier may outlive it).</summary>
    public Webhook? Find(string doctype, string name) => For(doctype)?.Hooks.FirstOrDefault(h => h.Active && h.Name == name);

    internal void Set(WebhookSet set)
    {
        if (set.Hooks.Any(h => h.Active))
            ImmutableInterlocked.AddOrUpdate(ref _sets, set.DocType, set, (_, _) => set);
        else
            ImmutableInterlocked.TryRemove(ref _sets, set.DocType, out _);
    }

    internal void Remove(string doctype) => ImmutableInterlocked.TryRemove(ref _sets, doctype, out _);
}

public sealed record WebhookItem(string DocType, string? Module, DocTypeSource Source, bool HasHooks, int Hooks, int ActiveHooks);

public sealed record WebhookView(
    string DocType, DocTypeSource Source, bool Exists, DateTime? Modified, WebhookSet Set, IReadOnlyList<string> Fields, bool CanEdit);

/// <summary>What came back from posting a delivery.</summary>
public sealed record WebhookResult(int StatusCode, string? Error = null)
{
    public bool Ok => Error is null && StatusCode is >= 200 and < 300;
}

/// <summary>Posts one delivery. Implementations must refuse non-public destinations; see <see cref="NetworkGuard"/>.</summary>
public interface IWebhookSender
{
    Task<WebhookResult> SendAsync(string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct = default);
}

/// <summary>Decides whether an address is somewhere a webhook may post to: the public internet, never this machine or a private network.</summary>
public static class NetworkGuard
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
            return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)   // link-local, cloud metadata
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) // carrier-grade NAT
                || bytes[0] == 0 || bytes[0] >= 224        // "this network", multicast, reserved, broadcast
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                || (bytes[0] == 198 && bytes[1] is 18 or 19));
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal
                || address.IsIPv6Teredo || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8));
        }

        return false;
    }

    /// <summary>Why a webhook address cannot be used, or null. Names are checked at save time; the sender checks the real address again when it connects.</summary>
    public static string? UrlProblem(string? url, bool allowInsecure)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > Webhook.MaxUrl || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return "Enter the address to post to (up to 500 characters, no spaces).";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "That is not a web address.";
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowInsecure && uri.Scheme == Uri.UriSchemeHttp))
            return "Only https addresses can be used.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "The address cannot contain a user name or password.";
        var host = uri.IdnHost;
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || (!host.Contains('.') && !IPAddress.TryParse(host, out _)))
            return allowInsecure ? null : "Internal and local addresses cannot be used.";
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && !IsPublic(ip))
            return allowInsecure ? null : "Private and local network addresses cannot be used.";
        return null;
    }
}

/// <summary>
/// The Webhooks page: say which other systems are told when documents change. Hooks are data, validated against the DocType
/// and applied at once; managing them needs a System Manager.
/// </summary>
public sealed class WebhookManager(
    IMetaRegistry registry,
    IWebhookStore store,
    WebhookCatalog catalog,
    PermissionService permissions,
    TimeProvider clock,
    bool allowInsecure = false)
{
    private readonly SemaphoreSlim _changes = new(1, 1);

    private bool CanManage(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private void RequireManager(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!CanManage(user))
            throw new PermissionDeniedException("Only a System Manager can manage webhooks.");
    }

    private DocTypeMeta Workable(string doctype, out DocTypeSource source)
    {
        if (!registry.TryGet(doctype, out var meta) || !AssignmentRuleManager.Applies(meta))
            throw new NotFoundException($"DocType '{doctype}' was not found.");
        source = registry.SourceOf(doctype)!.Value;
        return meta;
    }

    /// <summary>Fields a webhook may send: real columns that are not passwords.</summary>
    public static IReadOnlyList<string> SendableFields(DocTypeMeta meta) =>
        meta.Fields.Where(f => FieldTypes.HasColumn(f.Type) && f.Type != FieldType.Password).Select(f => f.Name).ToList();

    private static WebhookSet Strip(WebhookSet set) =>
        set with { Hooks = set.Hooks.Select(h => h with { Secret = null, SecretSet = !string.IsNullOrEmpty(h.Secret) }).ToList() };

    private static WebhookSet? TryParse(string json)
    {
        try
        {
            return DocTypeLoader.ParseWebhooks(json);
        }
        catch (DocTypeValidationException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<WebhookItem>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var stored = (await store.ListAsync(ct)).ToDictionary(r => r.DocType, StringComparer.OrdinalIgnoreCase);
        return registry.All.Where(AssignmentRuleManager.Applies).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m =>
            {
                var set = stored.TryGetValue(m.Name, out var row) ? TryParse(row.Definition) : null;
                return new WebhookItem(m.Name, m.Module, registry.SourceOf(m.Name)!.Value, row is not null, set?.Hooks.Count ?? 0, set?.Hooks.Count(h => h.Active) ?? 0);
            }).ToList();
    }

    public async Task<WebhookView> GetAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var meta = Workable(doctype, out var source);
        var stored = await store.GetAsync(doctype, ct);
        var set = (stored is null ? null : TryParse(stored.Definition)) ?? new WebhookSet { DocType = doctype };
        return new WebhookView(doctype, source, stored is not null, stored?.Modified, Strip(set), SendableFields(meta), CanManage(user));
    }

    public async Task<WebhookView> SaveAsync(string doctype, WebhookSet set, DateTime? expectedModified, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        await _changes.WaitAsync(ct);
        try
        {
            var meta = Workable(doctype, out _);
            var stored = await store.GetAsync(doctype, ct);
            var previous = (stored is null ? null : TryParse(stored.Definition))?.Hooks.ToDictionary(h => h.Name, StringComparer.Ordinal) ?? [];
            set = Clean(set with { DocType = doctype }, previous);
            var errors = Validate(set, meta);
            if (errors.Count > 0)
                throw new DocumentValidationException(errors);
            if (stored?.Modified != expectedModified)
                throw new ConflictException($"The webhooks of '{doctype}' were changed by someone else. Reload and try again.");

            var utc = clock.GetUtcNow().UtcDateTime;
            var row = new StoredWebhooks(doctype, DocTypeLoader.SerializeWebhooks(set), new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc), user.User);
            if (!await store.SaveAsync(row, expectedModified, ct))
                throw new ConflictException($"The webhooks of '{doctype}' were changed by someone else. Reload and try again.");

            catalog.Set(set);
            return await GetAsync(doctype, user, ct);
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task DeleteAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        await _changes.WaitAsync(ct);
        try
        {
            Workable(doctype, out _);
            await store.DeleteAsync(doctype, ct);
            catalog.Remove(doctype);
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var row in await store.ListAsync(ct))
        {
            try
            {
                var set = DocTypeLoader.ParseWebhooks(row.Definition);
                var meta = Workable(row.DocType, out _);
                var errors = Validate(set with { Hooks = set.Hooks.Select(h => h with { Name = h.Name.Trim() }).ToList() }, meta);
                if (errors.Count > 0)
                    throw new DocTypeValidationException(string.Join(" ", errors.Select(e => $"{e.Field}: {e.Message}")));
                catalog.Set(set);
            }
            catch (Exception ex) when (ex is DocTypeValidationException or NotFoundException)
            {
                failures.Add($"The webhooks of '{row.DocType}' were skipped: {ex.Message}");
            }
        }

        return failures;
    }

    private static WebhookSet Clean(WebhookSet input, IReadOnlyDictionary<string, Webhook> previous) => input with
    {
        Hooks = (input.Hooks ?? []).Select(h =>
        {
            var name = h.Name?.Trim() ?? string.Empty;
            var secret = string.IsNullOrEmpty(h.Secret) ? previous.GetValueOrDefault(name)?.Secret : h.Secret;
            return h with
            {
                Name = name, Event = h.Event?.Trim() ?? string.Empty, Url = h.Url?.Trim() ?? string.Empty,
                Condition = string.IsNullOrWhiteSpace(h.Condition) ? null : h.Condition.Trim(),
                Secret = secret, SecretSet = !string.IsNullOrEmpty(secret),
                Fields = (h.Fields ?? []).Select(f => f?.Trim() ?? string.Empty).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            };
        }).ToList(),
    };

    public List<FieldError> Validate(WebhookSet set, DocTypeMeta meta)
    {
        var errors = new List<FieldError>();
        void Add(string path, string message) => errors.Add(new FieldError(path, message));
        if (set.Hooks.Count > Webhook.MaxHooks)
            Add("hooks", $"At most {Webhook.MaxHooks} webhooks per DocType.");

        var known = meta.Fields.Select(f => f.Name).Concat(StandardFields.Columns.Keys).ToHashSet(StringComparer.Ordinal);
        var sendable = SendableFields(meta).Concat(StandardFields.Columns.Keys).ToHashSet(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < set.Hooks.Count; i++)
        {
            var h = set.Hooks[i];
            if (h.Name.Length is 0 or > 100 || h.Name.Any(char.IsControl))
                Add($"hooks[{i}].name", "Give the webhook a name of up to 100 characters.");
            else if (!names.Add(h.Name))
                Add($"hooks[{i}].name", $"There is already a webhook named '{h.Name}'.");
            if (!Webhook.Events.Contains(h.Event))
                Add($"hooks[{i}].event", $"Use one of: {string.Join(", ", Webhook.Events)}.");
            if (NetworkGuard.UrlProblem(h.Url, allowInsecure) is { } problem)
                Add($"hooks[{i}].url", problem);
            if (string.IsNullOrEmpty(h.Secret))
                Add($"hooks[{i}].secret", $"Set a secret of at least {Webhook.MinSecret} characters: receivers use it to check that a delivery is really from here.");
            else if (h.Secret.Length is < Webhook.MinSecret or > Webhook.MaxSecret || h.Secret.Any(char.IsControl))
                Add($"hooks[{i}].secret", $"Use a secret of {Webhook.MinSecret} to {Webhook.MaxSecret} characters.");
            if (h.Fields.Any(f => !sendable.Contains(f)))
                Add($"hooks[{i}].fields", "Use fields of this DocType (passwords cannot be sent).");

            if (h.Condition is { } condition)
            {
                if (condition.Length > Webhook.MaxCondition)
                    Add($"hooks[{i}].condition", $"At most {Webhook.MaxCondition} characters.");
                else
                {
                    try
                    {
                        if (Expression.Parse(condition).Identifiers.FirstOrDefault(n => !known.Contains(n)) is { } unknown)
                            Add($"hooks[{i}].condition", $"'{unknown}' is not a field of this DocType.");
                    }
                    catch (ExpressionException ex)
                    {
                        Add($"hooks[{i}].condition", ex.Message);
                    }
                }
            }
        }

        return errors;
    }
}

/// <summary>
/// Turns document events into queued deliveries and sends them. A delivery is a row of the Core DocType <c>Webhook Delivery</c>,
/// signed with the hook's secret (<c>X-Qahwa-Signature: t=&lt;unix&gt;,v1=&lt;hmac&gt;</c>), tried up to five times with growing delays, and
/// claimed with a compare-and-swap so several servers never send the same one twice.
/// </summary>
public sealed class WebhookService(
    WebhookCatalog catalog,
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    IWebhookSender sender,
    HistoryRecorder history)
{
    public const string DocTypeName = "Webhook Delivery";
    public const int MaxAttempts = 5;
    public const int MaxPayload = 256 * 1024;
    private const int BatchSize = 50;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);
    private static readonly UserContext Robot = new("Administrator", [UserContext.AdministratorRole]);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private DateTime Next(DateTime previous) => history.Now() > previous ? history.Now() : previous.AddTicks(10);

    // ---- events to deliveries ----

    private async Task QueueAsync(Webhook hook, DocTypeMeta meta, string name, string eventName, IReadOnlyDictionary<string, object?> doc, CancellationToken ct)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        var allowed = hook.Fields.Count > 0 ? hook.Fields.ToHashSet(StringComparer.Ordinal)
            : meta.Fields.Where(f => FieldTypes.HasColumn(f.Type) && f.Type != FieldType.Password && f.PermLevel == 0).Select(f => f.Name)
                .Concat(StandardFields.Columns.Keys).ToHashSet(StringComparer.Ordinal);
        foreach (var key in allowed)
        {
            if (doc.TryGetValue(key, out var value))
                data[key] = value;
        }

        var payload = new Dictionary<string, object?>
        {
            ["event"] = eventName, ["doctype"] = meta.Name, ["name"] = name, ["timestamp"] = history.Now().ToString("o", CultureInfo.InvariantCulture), ["data"] = data,
        };
        var body = JsonSerializer.Serialize(payload, Json);
        if (Encoding.UTF8.GetByteCount(body) > MaxPayload)
        {
            payload["data"] = null;
            payload["truncated"] = true;
            body = JsonSerializer.Serialize(payload, Json);
        }

        var row = HistoryRecorder.Standard("Administrator", history.Now());
        row["webhook"] = hook.Name;
        row["reference_doctype"] = meta.Name;
        row["reference_name"] = name;
        row["event"] = eventName;
        row["payload"] = body;
        row["status"] = "Pending";
        row["attempts"] = 0L;
        row["next_attempt"] = history.Now();
        await repository.InsertAsync(Meta, row, null, ct);
    }

    /// <summary>A document was saved: queue a delivery for each matching hook. Never fails the save.</summary>
    public async Task AfterSaveAsync(
        DocTypeMeta meta, string name, bool isNew, IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?> after, CancellationToken ct = default)
    {
        if (!AssignmentRuleManager.Applies(meta) || catalog.For(meta.Name) is not { } set)
            return;
        try
        {
            var oldStatus = Convert.ToInt32(before?.GetValueOrDefault("docstatus") ?? 0, CultureInfo.InvariantCulture);
            var newStatus = Convert.ToInt32(after.GetValueOrDefault("docstatus") ?? 0, CultureInfo.InvariantCulture);
            var changed = before is not null && meta.Fields.Where(f => FieldTypes.HasColumn(f.Type)).Any(f => !Equals(Normal(before.GetValueOrDefault(f.Name)), Normal(after.GetValueOrDefault(f.Name))));
            foreach (var hook in set.Hooks.Where(h => h.Active))
            {
                var applies = hook.Event switch
                {
                    Webhook.New => isNew,
                    Webhook.Update => !isNew && changed,
                    Webhook.Submit => !isNew && oldStatus == 0 && newStatus == 1,
                    Webhook.Cancel => !isNew && oldStatus == 1 && newStatus == 2,
                    _ => false,
                };
                if (applies && documents.EvaluateCondition(hook.Condition, meta, after, Robot))
                    await QueueAsync(hook, meta, name, hook.Event, after, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The document is saved; telling other systems is queued best-effort and must not fail the save.
        }
    }

    /// <summary>A document was deleted: queue a delivery with what it was.</summary>
    public async Task AfterDeleteAsync(DocTypeMeta meta, string name, IReadOnlyDictionary<string, object?> doc, CancellationToken ct = default)
    {
        if (!AssignmentRuleManager.Applies(meta) || catalog.For(meta.Name) is not { } set)
            return;
        try
        {
            foreach (var hook in set.Hooks.Where(h => h.Active && h.Event == Webhook.Delete && documents.EvaluateCondition(h.Condition, meta, doc, Robot)))
                await QueueAsync(hook, meta, name, Webhook.Delete, doc, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // See AfterSaveAsync.
        }
    }

    private static object? Normal(object? value) => value switch
    {
        null or "" => null,
        decimal d => d,
        long l => (decimal)l,
        int i => (decimal)i,
        double d => (decimal)d,
        _ => value,
    };

    // ---- sending ----

    /// <summary>The signature receivers compute to check a delivery: HMAC-SHA256 of <c>timestamp + "." + body</c>.</summary>
    public static string Sign(string secret, long timestamp, string body)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "." + body));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>Sends what is due. Safe to run on several servers at once.</summary>
    public async Task<(int Sent, int Failed)> DeliverPendingAsync(CancellationToken ct = default)
    {
        var now = history.Now();
        var rows = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("status", FilterOp.In, new List<object?> { "Pending", "Sending" }), new("next_attempt", FilterOp.Le, now)],
            OrderBy = "next_attempt", Descending = false, Limit = BatchSize,
        }, ct);

        int sent = 0, failed = 0;
        foreach (var row in rows)
        {
            if (row["status"] as string == "Sending" && (DateTime)row["modified"]! > now - StuckAfter)
                continue; // another server is sending it

            var attempts = Convert.ToInt64(row["attempts"] ?? 0L, CultureInfo.InvariantCulture) + 1;
            var claim = new Dictionary<string, object?> { ["status"] = "Sending", ["attempts"] = attempts, ["modified"] = Next((DateTime)row["modified"]!) };
            if (!await repository.UpdateAsync(Meta, (string)row["name"]!, claim, (DateTime)row["modified"]!, null, ct))
                continue;

            var outcome = new Dictionary<string, object?> { ["modified"] = Next((DateTime)claim["modified"]!) };
            var hook = catalog.Find((string)row["reference_doctype"]!, (string)row["webhook"]!);
            WebhookResult result;
            if (hook is null || string.IsNullOrEmpty(hook.Secret))
                result = new WebhookResult(0, "The webhook was removed or switched off.");
            else
            {
                var body = (string)row["payload"]!;
                var timestamp = new DateTimeOffset(DateTime.SpecifyKind(history.Now(), DateTimeKind.Utc)).ToUnixTimeSeconds();
                var headers = new Dictionary<string, string>
                {
                    ["X-Qahwa-Event"] = (string)row["event"]!,
                    ["X-Qahwa-Delivery"] = (string)row["name"]!,
                    ["X-Qahwa-Signature"] = $"t={timestamp},v1={Sign(hook.Secret, timestamp, body)}",
                };
                try
                {
                    result = await sender.SendAsync(hook.Url, headers, body, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = new WebhookResult(0, ex.Message);
                }
            }

            outcome["response_code"] = (long)result.StatusCode;
            if (result.Ok)
            {
                outcome["status"] = "Sent";
                outcome["last_error"] = null;
                sent++;
            }
            else
            {
                var reason = new string((result.Error ?? $"The receiver answered {result.StatusCode}.").Select(c => char.IsControl(c) ? ' ' : c).Take(300).ToArray());
                outcome["last_error"] = reason;
                var permanent = hook is null;
                outcome["status"] = permanent || attempts >= MaxAttempts ? "Failed" : "Pending";
                outcome["next_attempt"] = history.Now() + Backoff[Math.Min((int)attempts - 1, Backoff.Length - 1)];
                failed++;
            }

            await repository.UpdateAsync(Meta, (string)row["name"]!, outcome, null, null, ct);
        }

        return (sent, failed);
    }
}
