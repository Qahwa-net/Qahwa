using Qahwa.Abstractions;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace Qahwa.Core;

/// <summary>When a hook runs. The Before events run before the change is stored (and may change it or refuse it); the After events once it is stored.</summary>
public enum DocEvent
{
    BeforeSave,
    BeforeSubmit,
    BeforeCancel,
    BeforeDelete,
    AfterSave,
    AfterSubmit,
    AfterCancel,
    AfterDelete,
}

/// <summary>What a document hook is given. In a Before event <see cref="Doc"/> may be changed; in an After event it is a read-only view of what was stored.</summary>
public sealed class DocHookContext(
    DocEvent @event, DocTypeMeta meta, string? name, bool isNew, Dictionary<string, object?> doc, IReadOnlyDictionary<string, object?>? old,
    IUserContext user, DocumentService documents, IServiceProvider services, CancellationToken ct)
{
    public DocEvent Event { get; } = @event;
    public DocTypeMeta Meta { get; } = meta;
    public string DocType => Meta.Name;
    public string? Name { get; } = name;
    public bool IsNew { get; } = isNew;
    public Dictionary<string, object?> Doc { get; } = doc;
    public IReadOnlyDictionary<string, object?>? Old { get; } = old;

    /// <summary>The person who made the change. Anything the hook does through <see cref="Documents"/> is done as them, with their permissions.</summary>
    public IUserContext User { get; } = user;

    public DocumentService Documents { get; } = documents;
    public IServiceProvider Services { get; } = services;
    public CancellationToken Cancellation { get; } = ct;

    /// <summary>Stops the change with a message shown to the person (Before events only).</summary>
    public void Refuse(string message) => throw new DocumentValidationException([new FieldError(string.Empty, message)]);
}

public delegate Task DocHook(DocHookContext context);

/// <summary>What a permission check of an app is given: what is being done, to which document, by whom.</summary>
public sealed class PermissionContext(DocTypeMeta meta, PermAction action, IReadOnlyDictionary<string, object?> doc, IUserContext user)
{
    public string DocType => meta.Name;
    public PermAction Action { get; } = action;

    /// <summary>The document as stored (for a new one, the values being saved). Changing it changes nothing.</summary>
    public IReadOnlyDictionary<string, object?> Doc { get; } = doc;

    public IUserContext User { get; } = user;
}

/// <summary>Decides whether the person may do this to this document. Returning false refuses; it can never allow what roles do not.</summary>
public delegate bool PermissionCheck(PermissionContext context);

/// <summary>A condition an app adds to every list of a DocType: the field must be one of the values. No values means no documents.</summary>
public sealed record PermissionFilter(string Field, IReadOnlyList<object?> Values);

/// <summary>Lists the conditions to add to the lists this person sees. It can only narrow what roles already allow.</summary>
public delegate IReadOnlyList<PermissionFilter> PermissionQuery(IUserContext user);

internal sealed record RegisteredPermissionCheck(string App, string DocType, PermissionCheck Check);

internal sealed record RegisteredPermissionQuery(string App, string DocType, PermissionQuery Query);

/// <summary>Who may call a method, how often, and whether a GET may.</summary>
public sealed record MethodOptions
{
    /// <summary>A person needs one of these roles (empty: any signed-in person).</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    public bool AllowGet { get; init; }

    public int PerMinute { get; init; } = 60;
}

public sealed class MethodContext(string name, IUserContext user, JsonElement args, DocumentService documents, IServiceProvider services, CancellationToken ct)
{
    public string Name { get; } = name;
    public IUserContext User { get; } = user;

    /// <summary>The arguments as a JSON object (an empty object when none were sent).</summary>
    public JsonElement Args { get; } = args;

    public DocumentService Documents { get; } = documents;
    public IServiceProvider Services { get; } = services;
    public CancellationToken Cancellation { get; } = ct;

    public string? Text(string name) => Args.ValueKind == JsonValueKind.Object && Args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public string RequiredText(string name) => Text(name) is { Length: > 0 } s ? s : throw new DocumentValidationException([new FieldError(name, $"'{name}' is required.")]);

    public JsonElement? Value(string name) => Args.ValueKind == JsonValueKind.Object && Args.TryGetProperty(name, out var v) ? v : null;
}

public delegate Task<object?> MethodHandler(MethodContext context);

/// <summary>What an app uses to say what it adds. See <see cref="IQahwaApp.Configure"/>.</summary>
public interface IAppBuilder
{
    /// <summary>Run <paramref name="hook"/> when a document of this DocType is saved, submitted, cancelled or deleted.</summary>
    IAppBuilder On(string doctype, DocEvent @event, DocHook hook);

    /// <summary>
    /// Narrows who may do what to the documents of this DocType. Roles and User Permissions are applied first; this can only take access
    /// away (never grant it), for read, write, create, delete, submit and cancel. It does not apply to Administrators or to the
    /// framework's own DocTypes. A check that throws refuses the action (and is logged).
    /// </summary>
    IAppBuilder HasPermission(string doctype, PermissionCheck check);

    /// <summary>
    /// Adds conditions to every list, count, group-by and export of this DocType for a person, so documents they may not open are not
    /// listed either (a list is filtered, not refused). Use together with <see cref="HasPermission"/>. A query that throws lists nothing.
    /// </summary>
    IAppBuilder PermissionQuery(string doctype, PermissionQuery query);

    /// <summary>A server function people (or other systems) may call at <c>POST /api/method/&lt;app&gt;.&lt;name&gt;</c>.</summary>
    IAppBuilder Method(string name, MethodHandler handler, MethodOptions? options = null);

    /// <summary>A job run by the scheduler about every <paramref name="every"/> (at least a minute). It must be safe to run twice, as several servers may.</summary>
    IAppBuilder Schedule(string name, TimeSpan every, Func<IServiceProvider, CancellationToken, Task> job);

    /// <summary>A document made when the app is first installed on a site (and left alone if it is already there).</summary>
    IAppBuilder Fixture(string doctype, IReadOnlyDictionary<string, object?> values);
}

/// <summary>
/// An installable package of business logic: DocTypes (JSON files in a folder), hooks on documents, server functions, scheduled jobs,
/// fixtures and patches. Write one as a class library, implement this interface and register it with <c>AddQahwaApp&lt;T&gt;()</c>.
/// </summary>
public interface IQahwaApp
{
    /// <summary>Lower-case letters, digits and underscores, starting with a letter (it prefixes the app's methods).</summary>
    string Name { get; }

    string Version { get; }

    string Title => Name;

    /// <summary>A folder of DocType JSON files that belong to the app (searched recursively), or null.</summary>
    string? DocTypesPath => null;

    /// <summary>One-time data changes that go with the app's versions (see <see cref="IPatch"/>). Name them with the app name as a prefix.</summary>
    IReadOnlyList<IPatch> Patches => [];

    void Configure(IAppBuilder app);
}

public sealed record InstalledApp(string Name, string Title, string Version, bool Installed);

internal sealed record RegisteredHook(string App, string DocType, DocEvent Event, DocHook Hook);

internal sealed record RegisteredMethod(string App, string FullName, MethodHandler Handler, MethodOptions Options);

internal sealed record RegisteredJob(string App, string Name, TimeSpan Every, Func<IServiceProvider, CancellationToken, Task> Job);

internal sealed record RegisteredFixture(string App, string DocType, IReadOnlyDictionary<string, object?> Values);

/// <summary>
/// Everything the installed apps added. Built once at start-up from the registered apps; a mistake (a bad or repeated name, a job that
/// runs more than once a minute, a hook for a DocType that does not exist) stops the server with a message that names the app.
/// </summary>
public sealed partial class AppCatalog
{
    public const int MinutesBetweenJobs = 1;

    [GeneratedRegex("^[a-z][a-z0-9_]{1,30}$")]
    private static partial Regex AppName();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,40}$")]
    private static partial Regex ShortName();

    private readonly List<RegisteredHook> _hooks = [];
    private readonly List<RegisteredPermissionCheck> _permissionChecks = [];
    private readonly List<RegisteredPermissionQuery> _permissionQueries = [];
    private readonly Dictionary<string, RegisteredMethod> _methods = new(StringComparer.Ordinal);
    private readonly List<RegisteredJob> _jobs = [];
    private readonly List<RegisteredFixture> _fixtures = [];
    private readonly List<IQahwaApp> _apps = [];

    public AppCatalog(IEnumerable<IQahwaApp> apps)
    {
        foreach (var app in apps)
            Add(app);
    }

    /// <summary>Adds an app (at start-up; tests add theirs here).</summary>
    public void Add(IQahwaApp app)
    {
        if (!AppName().IsMatch(app.Name))
            throw new InvalidOperationException($"'{app.Name}' is not a valid app name (lower-case letters, digits and underscores, starting with a letter).");
        if (app.Name == "qahwa" || _apps.Any(a => a.Name == app.Name))
            throw new InvalidOperationException($"The app name '{app.Name}' is used twice (or is reserved).");
        if (string.IsNullOrWhiteSpace(app.Version) || app.Version.Length > 40)
            throw new InvalidOperationException($"The app '{app.Name}' needs a version.");
        _apps.Add(app);
        app.Configure(new Builder(this, app.Name));
    }

    public IReadOnlyList<IQahwaApp> Apps => _apps;

    internal IReadOnlyList<RegisteredHook> HooksFor(string doctype, DocEvent @event) => _hooks.Where(h => h.DocType == doctype && h.Event == @event).ToList();

    internal IReadOnlyList<RegisteredPermissionCheck> PermissionChecksFor(string doctype) => _permissionChecks.Where(c => c.DocType == doctype).ToList();

    internal IReadOnlyList<RegisteredPermissionQuery> PermissionQueriesFor(string doctype) => _permissionQueries.Where(c => c.DocType == doctype).ToList();

    internal IReadOnlyList<RegisteredJob> Jobs => _jobs;

    internal IReadOnlyList<RegisteredFixture> Fixtures => _fixtures;

    internal IReadOnlyDictionary<string, RegisteredMethod> Methods => _methods;

    internal IEnumerable<string> HookedDocTypes => _hooks.Select(h => h.DocType).Distinct(StringComparer.Ordinal);

    /// <summary>Checks that every hook and fixture names a DocType that exists. Call after all DocTypes are loaded.</summary>
    public void Validate(IMetaRegistry registry)
    {
        foreach (var hook in _hooks.Where(h => !registry.TryGet(h.DocType, out _)))
            throw new InvalidOperationException($"The app '{hook.App}' has a hook for the DocType '{hook.DocType}', which does not exist.");
        foreach (var check in _permissionChecks.Where(c => !registry.TryGet(c.DocType, out _)))
            throw new InvalidOperationException($"The app '{check.App}' has a permission check for the DocType '{check.DocType}', which does not exist.");
        foreach (var query in _permissionQueries.Where(c => !registry.TryGet(c.DocType, out _)))
            throw new InvalidOperationException($"The app '{query.App}' has a permission query for the DocType '{query.DocType}', which does not exist.");
        foreach (var fixture in _fixtures.Where(f => !registry.TryGet(f.DocType, out _)))
            throw new InvalidOperationException($"The app '{fixture.App}' has a fixture for the DocType '{fixture.DocType}', which does not exist.");
    }

    private sealed class Builder(AppCatalog catalog, string app) : IAppBuilder
    {
        public IAppBuilder On(string doctype, DocEvent @event, DocHook hook)
        {
            if (string.IsNullOrWhiteSpace(doctype))
                throw new InvalidOperationException($"The app '{app}' has a hook without a DocType.");
            catalog._hooks.Add(new RegisteredHook(app, doctype, @event, hook ?? throw new ArgumentNullException(nameof(hook))));
            return this;
        }

        public IAppBuilder HasPermission(string doctype, PermissionCheck check)
        {
            if (string.IsNullOrWhiteSpace(doctype))
                throw new InvalidOperationException($"The app '{app}' has a permission check without a DocType.");
            catalog._permissionChecks.Add(new RegisteredPermissionCheck(app, doctype, check ?? throw new ArgumentNullException(nameof(check))));
            return this;
        }

        public IAppBuilder PermissionQuery(string doctype, PermissionQuery query)
        {
            if (string.IsNullOrWhiteSpace(doctype))
                throw new InvalidOperationException($"The app '{app}' has a permission query without a DocType.");
            catalog._permissionQueries.Add(new RegisteredPermissionQuery(app, doctype, query ?? throw new ArgumentNullException(nameof(query))));
            return this;
        }

        public IAppBuilder Method(string name, MethodHandler handler, MethodOptions? options = null)
        {
            if (!ShortName().IsMatch(name))
                throw new InvalidOperationException($"'{name}' is not a valid method name in the app '{app}' (lower-case letters, digits and underscores).");
            var full = $"{app}.{name}";
            if (!catalog._methods.TryAdd(full, new RegisteredMethod(app, full, handler, options ?? new MethodOptions())))
                throw new InvalidOperationException($"The method '{full}' is registered twice.");
            return this;
        }

        public IAppBuilder Schedule(string name, TimeSpan every, Func<IServiceProvider, CancellationToken, Task> job)
        {
            if (!ShortName().IsMatch(name) || every < TimeSpan.FromMinutes(MinutesBetweenJobs))
                throw new InvalidOperationException($"The job '{name}' of the app '{app}' needs a valid name and to run at most once a minute.");
            if (catalog._jobs.Any(j => j.App == app && j.Name == name))
                throw new InvalidOperationException($"The job '{app}.{name}' is registered twice.");
            catalog._jobs.Add(new RegisteredJob(app, name, every, job));
            return this;
        }

        public IAppBuilder Fixture(string doctype, IReadOnlyDictionary<string, object?> values)
        {
            catalog._fixtures.Add(new RegisteredFixture(app, doctype, values));
            return this;
        }
    }
}

public static class AppRegistration
{
    /// <summary>Adds an app. Its DocTypes are loaded, its hooks, methods and jobs are active and its fixtures are made on every site at the next start.</summary>
    public static IServiceCollection AddQahwaApp<T>(this IServiceCollection services) where T : class, IQahwaApp, new()
    {
        var app = new T();
        services.AddSingleton<IQahwaApp>(app);
        foreach (var patch in app.Patches)
            services.AddSingleton(patch);
        return services;
    }
}

/// <summary>
/// Runs the hooks of the apps. A Before hook sees (a copy of) the document being saved: what it changes is converted and checked like
/// any other input, a refusal stops the save, and a hook that fails for any other reason stops it too (a rule that cannot run cannot be
/// trusted); an After hook runs once the change is stored, can read and write other documents as the person who made the change, and
/// can never undo the save: its problems go to the Error Log. A change made by a hook does not start other hooks.
/// </summary>
public sealed class AppHookRunner(AppCatalog catalog, IServiceProvider services, Lazy<DocumentService> documents, ErrorLogService errors)
{
    private static readonly AsyncLocal<bool> Running = new();

    public bool Has(string doctype) => catalog.HookedDocTypes.Contains(doctype, StringComparer.Ordinal);

    public bool HasPermissionRules(string doctype) => catalog.PermissionChecksFor(doctype).Count > 0 || catalog.PermissionQueriesFor(doctype).Count > 0;

    /// <summary>False when an app's check refuses. A check that throws refuses too: a rule that cannot run cannot be trusted.</summary>
    public async Task<bool> AllowsAsync(DocTypeMeta meta, PermAction action, IReadOnlyDictionary<string, object?> doc, IUserContext user, CancellationToken ct)
    {
        foreach (var registered in catalog.PermissionChecksFor(meta.Name))
        {
            try
            {
                if (!registered.Check(new PermissionContext(meta, action, doc, user)))
                    return false;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await errors.RecordAsync("App permission", null, $"{registered.App}: {action} on {meta.Name}", user.User, e);
                return false;
            }
        }

        return true;
    }

    /// <summary>The conditions the apps add to a person's lists of this DocType. A query that fails (or names a column that does not exist) leaves no document visible.</summary>
    public async Task<IReadOnlyList<QueryFilter>> FiltersAsync(DocTypeMeta meta, IUserContext user, CancellationToken ct)
    {
        var columns = meta.ColumnTypes();
        var filters = new List<QueryFilter>();
        foreach (var registered in catalog.PermissionQueriesFor(meta.Name))
        {
            try
            {
                foreach (var filter in registered.Query(user))
                {
                    if (!columns.TryGetValue(filter.Field, out var type) || type == FieldType.Password)
                        throw new InvalidOperationException($"'{filter.Field}' is not a column of {meta.Name}.");
                    filters.Add(new QueryFilter(filter.Field, FilterOp.In, filter.Values.Select(v => FieldValueConverter.ToStorage(type, v)).ToList()));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await errors.RecordAsync("App permission", null, $"{registered.App}: list of {meta.Name}", user.User, e);
                filters.Add(new QueryFilter("name", FilterOp.In, new List<object?>()));
            }
        }

        return filters;
    }

    /// <returns>The fields the hooks changed on the document (for the caller to convert, check and store).</returns>
    public async Task<Dictionary<string, object?>> BeforeAsync(
        DocTypeMeta meta, DocEvent @event, IReadOnlyDictionary<string, object?> doc, IReadOnlyDictionary<string, object?>? existing, bool isNew, IUserContext user, CancellationToken ct)
    {
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (Running.Value)
            return changes;

        var current = new Dictionary<string, object?>(doc, StringComparer.Ordinal);
        foreach (var registered in catalog.HooksFor(meta.Name, @event))
        {
            var before = new Dictionary<string, object?>(current, StringComparer.Ordinal);
            var context = new DocHookContext(@event, meta, current.GetValueOrDefault("name") as string, isNew, current, existing, user, documents.Value, services, ct);
            Running.Value = true;
            try
            {
                await registered.Hook(context);
            }
            catch (Exception e) when (e is not (QahwaException or OperationCanceledException))
            {
                await errors.RecordAsync("App hook", null, $"{registered.App}: {@event} on {meta.Name}", user.User, e);
                throw new InvalidOperationException($"The app '{registered.App}' failed while saving {meta.Name}.", e);
            }
            finally
            {
                Running.Value = false;
            }

            if (@event is DocEvent.BeforeSave or DocEvent.BeforeSubmit)
            {
                foreach (var (key, value) in current)
                    if (!before.TryGetValue(key, out var was) || !Equals(was, value))
                        changes[key] = value;
            }
        }

        return changes;
    }

    public async Task AfterAsync(DocTypeMeta meta, DocEvent @event, string name, bool isNew, IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?> after, IUserContext user, CancellationToken ct)
    {
        if (Running.Value)
            return;
        foreach (var registered in catalog.HooksFor(meta.Name, @event))
        {
            var context = new DocHookContext(@event, meta, name, isNew, new Dictionary<string, object?>(after, StringComparer.Ordinal), before, user, documents.Value, services, ct);
            Running.Value = true;
            try
            {
                await registered.Hook(context);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await errors.RecordAsync("App hook", null, $"{registered.App}: {@event} on {meta.Name} {name}", user.User, e);
            }
            finally
            {
                Running.Value = false;
            }
        }
    }
}

/// <summary>Server functions by name: the ones apps register and the built-in <c>qahwa.*</c> ones (<see cref="BuiltInMethods"/>).</summary>
public sealed partial class MethodService(AppCatalog catalog, IServiceProvider services, Lazy<DocumentService> documents, HitCounter hits)
{
    public const int MaxBodyBytes = 1024 * 1024;

    [GeneratedRegex("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*$")]
    private static partial Regex FullName();

    public IReadOnlyList<string> Names => [.. catalog.Methods.Keys, .. BuiltInMethods.All.Keys];

    public async Task<object?> InvokeAsync(string name, JsonElement args, IUserContext user, bool isGet, CancellationToken ct)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (name.Length > 80 || !FullName().IsMatch(name))
            throw new NotFoundException("There is no such method.");

        var (handler, options) = BuiltInMethods.All.TryGetValue(name, out var builtIn) ? (builtIn, new MethodOptions { PerMinute = 120, AllowGet = name is "qahwa.get_count" or "qahwa.get_value" or "qahwa.get_list" })
            : catalog.Methods.TryGetValue(name, out var registered) ? (registered.Handler, registered.Options)
            : throw new NotFoundException("There is no such method.");

        if (isGet && !options.AllowGet)
            throw new PermissionDeniedException("This method can only be called with POST.");
        if (options.Roles.Count > 0 && !user.Roles.Contains(UserContext.AdministratorRole) && !options.Roles.Any(r => user.Roles.Contains(r)))
            throw new PermissionDeniedException("You are not allowed to call this method.");
        if (!hits.TryHit($"method:{name}:{user.User}", Math.Clamp(options.PerMinute, 1, 6000), TimeSpan.FromMinutes(1)))
            throw new RateLimitedException();

        return await handler(new MethodContext(name, user, args, documents.Value, services, ct));
    }
}

/// <summary>
/// The built-in server functions (Frappe's get_count, get_value, get_list, set_value and rename_doc). They are thin wrappers over the
/// same document service the REST API uses, so every permission, validation and hook applies; none of them can do more than the
/// ordinary calls can.
/// </summary>
public static class BuiltInMethods
{
    private static ListRequest Request(MethodContext c, int defaultLimit)
    {
        var filters = new List<RawFilter>();
        if (c.Value("filters") is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var f in list.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Array || f.GetArrayLength() != 3 || f[0].ValueKind != JsonValueKind.String || f[1].ValueKind != JsonValueKind.String)
                    throw new DocumentValidationException([new FieldError("filters", "A filter looks like [\"field\", \"=\", value].")]);
                filters.Add(new RawFilter(f[0].GetString()!, f[1].GetString()!, Plain(f[2])));
            }
        }

        var fields = c.Value("fields") is { ValueKind: JsonValueKind.Array } fs ? fs.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : null;
        var orderBy = c.Text("order_by");
        var descending = true;
        if (orderBy is not null && orderBy.EndsWith(" asc", StringComparison.OrdinalIgnoreCase)) { orderBy = orderBy[..^4].Trim(); descending = false; }
        else if (orderBy is not null && orderBy.EndsWith(" desc", StringComparison.OrdinalIgnoreCase)) orderBy = orderBy[..^5].Trim();
        var limit = c.Value("limit") is { ValueKind: JsonValueKind.Number } l && l.TryGetInt32(out var n) ? n : defaultLimit;
        return new ListRequest { Filters = filters, Fields = fields, OrderBy = orderBy, Descending = descending, Limit = limit };
    }

    internal static object? Plain(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDecimal(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => value.EnumerateArray().Select(Plain).ToList(),
        _ => throw new DocumentValidationException([new FieldError(string.Empty, "Values must be plain: text, numbers, yes/no or lists of them.")]),
    };

    public static readonly IReadOnlyDictionary<string, MethodHandler> All = new Dictionary<string, MethodHandler>(StringComparer.Ordinal)
    {
        ["qahwa.get_count"] = async c => (await c.Documents.ListAsync(c.RequiredText("doctype"), Request(c, 1) with { Limit = 1, Fields = ["name"] }, c.User, c.Cancellation)).Total,
        ["qahwa.get_list"] = async c => (await c.Documents.ListAsync(c.RequiredText("doctype"), Request(c, 20), c.User, c.Cancellation)).Data,
        ["qahwa.get_value"] = async c =>
        {
            var field = c.RequiredText("fieldname");
            var page = await c.Documents.ListAsync(c.RequiredText("doctype"), Request(c, 1) with { Limit = 1, Fields = [field] }, c.User, c.Cancellation);
            return page.Data.Count == 0 ? null : page.Data[0].GetValueOrDefault(field);
        },
        ["qahwa.set_value"] = async c =>
        {
            var value = c.Value("value") is { } v ? Plain(v) : null;
            var saved = await c.Documents.UpdateAsync(c.RequiredText("doctype"), c.RequiredText("name"), new Dictionary<string, object?> { [c.RequiredText("fieldname")] = value }, c.User, c.Cancellation);
            return saved;
        },
        ["qahwa.rename_doc"] = async c => await c.Documents.RenameAsync(c.RequiredText("doctype"), c.RequiredText("old_name"), c.RequiredText("new_name"), c.Value("merge") is { ValueKind: JsonValueKind.True }, c.User, c.Cancellation),
    };
}

/// <summary>Installs the apps on a site: remembers them (Installed App) and makes their fixtures once.</summary>
public sealed class AppInstaller(AppCatalog catalog, IMetaRegistry registry, IDocumentRepository repository, DocumentService documents, TimeProvider clock)
{
    public const string DocTypeName = "Installed App";
    private static readonly UserContext System = new("System", [UserContext.AdministratorRole]);

    private DateTime Now() { var utc = clock.GetUtcNow().UtcDateTime; return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc); }

    public async Task<IReadOnlyList<InstalledApp>> ListAsync(CancellationToken ct = default)
    {
        var rows = await repository.ListAsync(registry.Get(DocTypeName), new ListQuery { Fields = ["name", "title", "version"], OrderBy = "name", Descending = false, Limit = 1000 }, ct);
        return rows.Select(r => new InstalledApp((string)r["name"]!, r["title"] as string ?? (string)r["name"]!, r["version"] as string ?? string.Empty, true)).ToList();
    }

    /// <summary>Makes sure each registered app is recorded for this site (first time: fixtures are made too) and that its version is current.</summary>
    public async Task InstallAllAsync(CancellationToken ct = default)
    {
        catalog.Validate(registry);
        var meta = registry.Get(DocTypeName);
        foreach (var app in catalog.Apps)
        {
            var existing = await repository.GetAsync(meta, app.Name, ct);
            if (existing is null)
            {
                foreach (var fixture in catalog.Fixtures.Where(f => f.App == app.Name))
                    await MakeFixtureAsync(fixture, ct);
                await documents.InsertAsync(DocTypeName, new Dictionary<string, object?> { ["name"] = app.Name, ["app_name"] = app.Name, ["title"] = app.Title, ["version"] = app.Version, ["installed_on"] = Now() }, System, ct);
            }
            else if (existing["version"] as string != app.Version || existing["title"] as string != app.Title)
            {
                await documents.UpdateAsync(DocTypeName, app.Name, new Dictionary<string, object?> { ["version"] = app.Version, ["title"] = app.Title }, System, ct);
            }
        }
    }

    private async Task MakeFixtureAsync(RegisteredFixture fixture, CancellationToken ct)
    {
        var values = new Dictionary<string, object?>(fixture.Values, StringComparer.Ordinal);
        if (values.GetValueOrDefault("name") is string name && await repository.ExistsAsync(registry.Get(fixture.DocType), name, ct))
            return; // already there: left alone
        await documents.InsertAsync(fixture.DocType, values, System, ct);
    }
}

/// <summary>Runs the scheduled jobs of the apps when they are due (called by the scheduler on each tick).</summary>
public sealed class AppJobRunner(AppCatalog catalog, IServiceProvider services, ErrorLogService errors, TimeProvider clock)
{
    private readonly Dictionary<string, DateTime> _last = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var ran = 0;
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var job in catalog.Jobs)
        {
            var key = $"{job.App}.{job.Name}";
            lock (_gate)
            {
                if (_last.TryGetValue(key, out var last) && now - last < job.Every)
                    continue;
                _last[key] = now;
            }

            try
            {
                await job.Job(services, ct);
                ran++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await errors.RecordAsync("App job", null, key, null, e);
            }
        }

        return ran;
    }
}
