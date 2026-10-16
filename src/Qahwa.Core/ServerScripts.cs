using System.Globalization;
using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Qahwa.Abstractions;
using Qahwa.Core.Expressions;

namespace Qahwa.Core;

/// <summary>What a script produced: the fields it changed on the document, what it logged, and how many reads and writes it made.</summary>
public sealed record ScriptOutcome(IReadOnlyDictionary<string, object?> Changes, IReadOnlyList<string> Log, int Reads, int Writes);

/// <summary>What a script is given.</summary>
public sealed record ScriptInput(DocTypeMeta Meta, string Event, bool IsNew, IReadOnlyDictionary<string, object?> Doc, IReadOnlyDictionary<string, object?>? Old, IUserContext User, bool AllowWrites);

/// <summary>
/// Runs one script in a sandbox: an interpreter with no access to the machine, the network, files, the clock beyond <c>qahwa.now()</c>,
/// or any .NET type. The script sees only copies of the document (<c>doc</c>, <c>old</c>), who is acting (<c>user</c>) and a small
/// <c>qahwa</c> object. It is stopped after a number of statements, a time, an amount of memory and a depth of calls, and everything it
/// reads or writes goes through the same permission checks as the person who made the change.
/// </summary>
public sealed class ScriptSandbox(DocumentService documents, TimeProvider clock)
{
    public const int MaxSourceLength = 20_000;
    public const int MaxStatements = 100_000;
    public const int MaxMemoryBytes = 8 * 1024 * 1024;
    public const int MaxRecursion = 100;
    public const int MaxReads = 20;
    public const int MaxWrites = 5;
    public const int MaxLogLines = 50;
    public const int MaxLogLength = 500;
    public const int MaxResultBytes = 200_000;
    public const int MaxListRows = 50;
    public static readonly TimeSpan MaxTime = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal) { "name", "owner", "creation", "modified", "modified_by", "docstatus", "idx", "doctype" };

    /// <summary>A syntax problem in the source, or null when it parses.</summary>
    public static string? SyntaxProblem(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return "The script is empty.";
        if (source.Length > MaxSourceLength)
            return $"The script is too long (at most {MaxSourceLength} characters).";
        try
        {
            Engine.PrepareScript(source);
            return null;
        }
        catch (Exception e) when (e is Acornima.ParseErrorException or ScriptPreparationException or JavaScriptException)
        {
            return "The script has a mistake: " + Short(e.Message);
        }
    }

    private static string Short(string text) => text.Length > 200 ? text[..200] + "…" : text;

    /// <exception cref="DocumentValidationException">The script refused the change (<c>qahwa.error</c>), failed, or ran out of its limits.</exception>
    public async Task<ScriptOutcome> RunAsync(string source, ScriptInput input, CancellationToken ct)
    {
        if (SyntaxProblem(source) is { } problem)
            throw Fail(problem);

        // The interpreter is synchronous and calls back into async services, so it runs on its own pool thread and waits there.
        return await Task.Run(() => Execute(source, input, ct), ct);
    }

    private static DocumentValidationException Fail(string message) => new([new FieldError(string.Empty, message)]);

    private static string Plain(DocTypeMeta meta, IReadOnlyDictionary<string, object?> doc)
    {
        var plain = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in doc)
        {
            var field = meta.Fields.FirstOrDefault(f => f.Name == key);
            if (field?.Type == FieldType.Table || field?.Type == FieldType.Password)
                continue;
            plain[key] = Values.FromStorage(value);
        }

        return JsonSerializer.Serialize(plain);
    }

    private ScriptOutcome Execute(string source, ScriptInput input, CancellationToken ct)
    {
        var log = new List<string>();
        var reads = 0;
        var writes = 0;
        var engine = new Engine(o =>
        {
            o.Strict();
            o.TimeoutInterval(MaxTime);
            o.RegexTimeoutInterval(MaxTime);
            o.MaxStatements(MaxStatements);
            o.LimitMemory(MaxMemoryBytes);
            o.LimitRecursion(MaxRecursion);
            o.CancellationToken(ct);
        });

        var parser = new JsonParser(engine);
        var serializer = new Jint.Native.Json.JsonSerializer(engine);

        string ToJson(JsValue value)
        {
            var text = value.IsUndefined() ? "null" : serializer.Serialize(value).ToString();
            return text.Length > MaxResultBytes ? throw Fail("A script passed too much data.") : text;
        }

        JsValue Parsed(object? value)
        {
            var text = JsonSerializer.Serialize(value);
            return text.Length > MaxResultBytes ? throw Fail("A script read too much data.") : parser.Parse(text);
        }

        string Text(JsValue[] args, int index, string what)
        {
            var text = args.Length > index && args[index].IsString() ? args[index].AsString() : null;
            return string.IsNullOrWhiteSpace(text) || text.Length > 140 ? throw Fail($"qahwa: {what} must be a name.") : text;
        }

        Dictionary<string, object?> Values_(JsValue[] args, int index)
        {
            if (args.Length <= index || !args[index].IsObject())
                throw Fail("qahwa: a set of values is needed.");
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ToJson(args[index]))!.ToDictionary(kv => kv.Key, kv => Convert(kv.Value), StringComparer.Ordinal);
        }

        void Read()
        {
            if (++reads > MaxReads)
                throw Fail($"A script may read at most {MaxReads} times.");
        }

        void Write()
        {
            if (!input.AllowWrites)
                throw Fail("This script may not create or change documents.");
            if (++writes > MaxWrites)
                throw Fail($"A script may write at most {MaxWrites} times.");
        }

        T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

        var api = engine.Evaluate("({})").AsObject();
        api.Set("error", new ClrFunction(engine, "error", (_, a) => throw Fail(a.Length > 0 && a[0].IsString() ? Short(a[0].AsString()) : "The change was refused.")));
        api.Set("log", new ClrFunction(engine, "log", (_, a) =>
        {
            if (log.Count < MaxLogLines)
                log.Add(string.Join(' ', a.Select(x => x.IsString() ? x.AsString() : ToJson(x))) is { Length: > MaxLogLength } text ? text[..MaxLogLength] : string.Join(' ', a.Select(x => x.IsString() ? x.AsString() : ToJson(x))));
            return JsValue.Undefined;
        }));
        api.Set("today", new ClrFunction(engine, "today", (_, _) => clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        api.Set("now", new ClrFunction(engine, "now", (_, _) => clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        api.Set("get", new ClrFunction(engine, "get", (_, a) =>
        {
            Read();
            try
            {
                var row = Wait(documents.GetAsync(Text(a, 0, "the DocType"), Text(a, 1, "the document name"), input.User, ct));
                return Parsed(row.ToDictionary(kv => kv.Key, kv => (object?)Values.FromStorage(kv.Value)));
            }
            catch (NotFoundException)
            {
                return JsValue.Null;
            }
        }));
        api.Set("list", new ClrFunction(engine, "list", (_, a) =>
        {
            Read();
            var doctype = Text(a, 0, "the DocType");
            var options = a.Length > 1 && a[1].IsObject() ? JsonSerializer.Deserialize<ScriptListOptions>(ToJson(a[1]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new() : new();
            var filters = (options.Filters ?? []).Select(f => f.Count == 3 && f[0].ValueKind == JsonValueKind.String && f[1].ValueKind == JsonValueKind.String
                ? new RawFilter(f[0].GetString()!, f[1].GetString()!, Convert(f[2])) : throw Fail("qahwa.list: a filter looks like [\"field\", \"=\", value].")).ToList();
            var result = Wait(documents.ListAsync(doctype, new ListRequest
            {
                Filters = filters, Fields = options.Fields, OrderBy = options.OrderBy, Descending = options.Descending ?? false, Limit = Math.Clamp(options.Limit ?? 20, 1, MaxListRows),
            }, input.User, ct));
            return Parsed(result.Data.Select(r => r.ToDictionary(kv => kv.Key, kv => (object?)Values.FromStorage(kv.Value))).ToList());
        }));
        api.Set("insert", new ClrFunction(engine, "insert", (_, a) =>
        {
            Write();
            var created = Wait(documents.InsertAsync(Text(a, 0, "the DocType"), Values_(a, 1), input.User, ct));
            return (string?)created.GetValueOrDefault("name") ?? string.Empty;
        }));
        api.Set("update", new ClrFunction(engine, "update", (_, a) =>
        {
            Write();
            Wait(documents.UpdateAsync(Text(a, 0, "the DocType"), Text(a, 1, "the document name"), Values_(a, 2), input.User, ct));
            return JsValue.Undefined;
        }));
        engine.SetValue("qahwa", api);
        engine.Execute("Object.freeze(qahwa);");

        var original = Plain(input.Meta, input.Doc);
        engine.SetValue("doc", parser.Parse(original));
        engine.SetValue("old", input.Old is null ? JsValue.Null : parser.Parse(Plain(input.Meta, input.Old)));
        engine.SetValue("isNew", input.IsNew);
        engine.SetValue("event", input.Event);
        engine.SetValue("user", parser.Parse(JsonSerializer.Serialize(new { name = input.User.User, roles = input.User.Roles })));

        try
        {
            engine.Execute(source);
        }
        catch (JavaScriptException e)
        {
            throw Fail("The script stopped: " + Short(e.Message));
        }
        catch (Exception e) when (e is TimeoutException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            throw Fail($"The script took too long (more than {MaxTime.TotalSeconds:0.#} second).");
        }
        catch (StatementsCountOverflowException)
        {
            throw Fail("The script did too much work.");
        }
        catch (MemoryLimitExceededException)
        {
            throw Fail("The script used too much memory.");
        }
        catch (RecursionDepthOverflowException)
        {
            throw Fail("The script called itself too deeply.");
        }
        catch (ExecutionCanceledException)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Acornima.ParseErrorException e)
        {
            throw Fail("The script has a mistake: " + Short(e.Message));
        }

        var finalDoc = engine.Evaluate("JSON.stringify(doc)");
        var after = finalDoc.IsString() ? finalDoc.AsString() : "{}";
        if (after.Length > MaxResultBytes)
            throw Fail("The script made the document too large.");

        var before = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(original)!;
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        var errors = new List<FieldError>();
        foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(after) ?? [])
        {
            if (before.TryGetValue(key, out var was) && was.GetRawText() == value.GetRawText())
                continue;
            if (Hidden.Contains(key))
                errors.Add(new FieldError(key, "A script cannot change this."));
            else if (input.Meta.Fields.FirstOrDefault(f => f.Name == key) is not { } field || field.Type is FieldType.Table or FieldType.Password)
                errors.Add(new FieldError(key, "A script set a field this DocType does not have."));
            else
                changes[key] = Convert(value);
        }

        if (errors.Count > 0)
            throw new DocumentValidationException(errors);
        if (changes.Count > 0 && !ServerScriptEngine.MayChange(input.Event))
            throw Fail("Only a Validate or Before Submit script may change the document; use qahwa.update in an After script.");
        return new ScriptOutcome(changes, log, reads, writes);
    }

    private static object? Convert(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDecimal(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(), // an object or list is passed on as JSON text (for Json fields)
    };

    private sealed class ScriptListOptions
    {
        public List<List<JsonElement>>? Filters { get; set; }
        public List<string>? Fields { get; set; }
        public string? OrderBy { get; set; }
        public bool? Descending { get; set; }
        public int? Limit { get; set; }
    }
}

/// <summary>
/// Server Scripts: short pieces of JavaScript that an administrator attaches to a DocType. A <b>Validate</b> script runs before a
/// document is saved and can change its fields or refuse it with <c>qahwa.error("…")</c>; an <b>After Save</b> script runs once it is
/// saved and can read and write other documents. Scripts act as the person who made the change and never with more rights, and a
/// change made by a script does not start other scripts (so they cannot loop).
///
/// A broken <b>Validate</b> script stops saves of its DocType (a rule that cannot run cannot be trusted to hold); the problem is written
/// on the script so it can be fixed or switched off. A broken <b>After Save</b> script never undoes the save.
/// </summary>
public sealed class ServerScriptEngine(IMetaRegistry registry, IDocumentRepository repository, ScriptSandbox sandbox, TimeProvider clock)
{
    public const string DocTypeName = "Server Script";
    public const string ValidateEvent = "Validate";
    public const string AfterSaveEvent = "After Save";
    public const string BeforeSubmitEvent = "Before Submit";
    public const string BeforeCancelEvent = "Before Cancel";
    public const string BeforeDeleteEvent = "Before Delete";
    public const string AfterSubmitEvent = "After Submit";
    public const string AfterCancelEvent = "After Cancel";

    public static readonly IReadOnlyList<string> Events = [ValidateEvent, BeforeSubmitEvent, BeforeCancelEvent, BeforeDeleteEvent, AfterSaveEvent, AfterSubmitEvent, AfterCancelEvent];

    /// <summary>Before events run before the change is stored (they can refuse it); only Validate and Before Submit may also change the document.</summary>
    public static bool MayChange(string kind) => kind is ValidateEvent or BeforeSubmitEvent;
    public const int MaxPerEvent = 10;
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    private static readonly AsyncLocal<bool> Running = new();

    private record Snapshot(DateTime At, ILookup<string, Dictionary<string, object?>> Scripts);

    private volatile Snapshot? _snapshot;

    /// <summary>Forget the cached scripts (called when one is saved or deleted on this server; others catch up within seconds).</summary>
    public void Invalidate() => _snapshot = null;

    private async Task<IReadOnlyList<Dictionary<string, object?>>> ScriptsAsync(string doctype, string kind, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var snapshot = _snapshot;
        if (snapshot is null || now - snapshot.At > CacheFor)
        {
            var rows = await repository.ListAsync(registry.Get(DocTypeName), new ListQuery
            {
                Filters = [new QueryFilter("active", FilterOp.Eq, true)], OrderBy = "creation", Descending = false, Limit = 1000,
            }, ct);
            snapshot = new Snapshot(now, rows.ToLookup(r => $"{r["reference_doctype"]}|{r["event"]}", StringComparer.Ordinal));
            _snapshot = snapshot;
        }

        return snapshot.Scripts[$"{doctype}|{kind}"].Take(MaxPerEvent).ToList();
    }

    private async Task<List<(string Kind, Dictionary<string, object?> Script)>> ScriptsForAsync(string doctype, IEnumerable<string> kinds, CancellationToken ct)
    {
        var all = new List<(string, Dictionary<string, object?>)>();
        foreach (var kind in kinds)
            foreach (var script in await ScriptsAsync(doctype, kind, ct))
                all.Add((kind, script));
        return all;
    }

    /// <summary>Runs the Before Delete scripts: any of them can refuse the deletion.</summary>
    public async Task BeforeDeleteAsync(DocTypeMeta meta, IReadOnlyDictionary<string, object?> existing, IUserContext user, CancellationToken ct) =>
        await ValidateAsync(meta, existing, existing, isNew: false, user, ct, BeforeDeleteEvent);

    /// <summary>Checks a Server Script document when it is saved: the DocType must exist and the source must parse.</summary>
    public void CheckScript(IReadOnlyDictionary<string, object?> script, List<FieldError> errors)
    {
        if (script.GetValueOrDefault("script") is string source && ScriptSandbox.SyntaxProblem(source) is { } problem)
            errors.Add(new FieldError("script", problem));
    }

    /// <summary>
    /// Runs the Validate scripts of the DocType on a document about to be saved.
    /// </summary>
    /// <returns>The fields the scripts changed (to be checked and stored with the rest).</returns>
    /// <exception cref="DocumentValidationException">A script refused the change or could not run.</exception>
    public async Task<Dictionary<string, object?>> ValidateAsync(
        DocTypeMeta meta, IReadOnlyDictionary<string, object?> doc, IReadOnlyDictionary<string, object?>? existing, bool isNew, IUserContext user, CancellationToken ct, string kind = ValidateEvent)
    {
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (Running.Value || !AssignmentRuleManager.Applies(meta))
            return changes;

        var current = new Dictionary<string, object?>(doc, StringComparer.Ordinal);
        foreach (var script in await ScriptsAsync(meta.Name, kind, ct))
        {
            ScriptOutcome outcome;
            try
            {
                outcome = await sandbox.RunAsync((string)script["script"]!, new ScriptInput(meta, kind, isNew, current, existing, user, AllowWrites: false), ct);
            }
            catch (DocumentValidationException e) when (e.Errors.Any(x => x.Field.Length == 0))
            {
                await RecordAsync(script, e, ct);
                throw;
            }

            await RecordAsync(script, null, ct);
            foreach (var (key, value) in outcome.Changes)
            {
                changes[key] = value;
                current[key] = value;
            }
        }

        return changes;
    }

    public async Task AfterSaveAsync(DocTypeMeta meta, string docName, bool isNew, IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?> after, IUserContext user, CancellationToken ct)
    {
        if (Running.Value || !AssignmentRuleManager.Applies(meta))
            return;

        var oldStatus = Convert.ToInt32(before?.GetValueOrDefault("docstatus") ?? 0, CultureInfo.InvariantCulture);
        var newStatus = Convert.ToInt32(after.GetValueOrDefault("docstatus") ?? 0, CultureInfo.InvariantCulture);
        var kinds = new List<string> { AfterSaveEvent };
        if (!isNew && oldStatus == 0 && newStatus == 1)
            kinds.Add(AfterSubmitEvent);
        else if (!isNew && oldStatus == 1 && newStatus == 2)
            kinds.Add(AfterCancelEvent);

        Running.Value = true;
        try
        {
            foreach (var (kind, script) in await ScriptsForAsync(meta.Name, kinds, ct))
            {
                try
                {
                    await sandbox.RunAsync((string)script["script"]!, new ScriptInput(meta, kind, isNew, after, before, user, AllowWrites: true), ct);
                    await RecordAsync(script, null, ct);
                }
                catch (DocumentValidationException e)
                {
                    await RecordAsync(script, e, ct);
                }
                catch (QahwaException e)
                {
                    await RecordAsync(script, new DocumentValidationException([new FieldError(string.Empty, e.Message)]), ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The document is already saved; a problem with scripts must not turn that into an error.
        }
        finally
        {
            Running.Value = false;
        }
    }

    /// <summary>Tries a script on a sample document without saving anything and without being allowed to write.</summary>
    public async Task<ScriptOutcome> TestAsync(DocTypeMeta meta, string kind, string source, IReadOnlyDictionary<string, object?> doc, bool isNew, IUserContext user, CancellationToken ct) =>
        await sandbox.RunAsync(source, new ScriptInput(meta, kind, isNew, doc, null, user, AllowWrites: false), ct);

    private async Task RecordAsync(Dictionary<string, object?> script, DocumentValidationException? problem, CancellationToken ct)
    {
        var text = problem is null ? null : string.Join(" ", problem.Errors.Select(x => x.Message));
        text = text is { Length: > 500 } ? text[..500] : text;
        var previous = script.GetValueOrDefault("last_error") as string;
        var runs = Convert.ToInt32(script.GetValueOrDefault("run_count") ?? 0, CultureInfo.InvariantCulture) + (problem is null ? 1 : 0);
        script["run_count"] = runs;
        // Status is only written when it changes (and now and then for the count), so a busy DocType does not rewrite the script on every save.
        if (problem is null ? previous is null && runs % 25 != 1 : previous == text)
            return;
        script["last_error"] = text;
        try
        {
            var utc = clock.GetUtcNow().UtcDateTime;
            await repository.UpdateAsync(registry.Get(DocTypeName), (string)script["name"]!,
                new Dictionary<string, object?> { ["last_error"] = text, ["run_count"] = runs, ["modified"] = new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc) }, null, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keeping status is a convenience.
        }
    }
}
