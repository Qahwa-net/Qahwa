using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed record PublicField(string Name, string Label, string Type, bool Required, IReadOnlyList<string> Options, string? Description);

public sealed record PublicForm(string Route, string Title, string? Intro, IReadOnlyList<PublicField> Fields);

/// <summary>Remembers recent hits per key so a person (or a script) cannot flood a form. In memory: each server counts for itself.</summary>
public sealed class HitCounter(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _hits = new(StringComparer.Ordinal);
    private const int MaxKeys = 20000;

    /// <summary>Counts a hit and says whether it is within <paramref name="limit"/> hits in the last <paramref name="window"/>.</summary>
    public bool TryHit(string key, int limit, TimeSpan window)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (_hits.Count > MaxKeys)
            Prune(now);
        if (_hits.Count > MaxKeys && !_hits.ContainsKey(key))
            return false; // under a flood from very many sources, new ones wait

        var queue = _hits.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > window)
                queue.Dequeue();
            if (queue.Count >= limit)
                return false;
            queue.Enqueue(now);
            return true;
        }
    }

    private void Prune(DateTime now)
    {
        foreach (var (key, queue) in _hits)
            lock (queue)
                if (queue.Count == 0 || now - queue.Peek() > TimeSpan.FromHours(2))
                    _hits.TryRemove(key, out _);
    }
}

/// <summary>Too many submissions from one source or to one form.</summary>
public sealed class RateLimitedException() : QahwaException("Too many submissions. Please wait a while and try again.", 429, "rate_limited");

/// <summary>
/// Public forms: anyone, without signing in, can send answers that become documents. This is the most exposed door in the system,
/// so it does very little: only the DocType fields the form lists (and that are plain, writable, ordinary fields) are accepted,
/// anything else is refused, values are plain text, numbers or yes/no of limited size, creation is rate limited per source and
/// per form, a hidden field catches simple bots, and nothing about the stored document is ever sent back.
/// </summary>
public sealed partial class WebFormService(IMetaRegistry registry, IDocumentRepository repository, DocumentService documents, HitCounter hits)
{
    public const string DocTypeName = "Web Form";
    public const string WebUser = "web-form@guest";
    public const int MaxValueLength = 5000;
    public const int MaxFields = 60;
    public const string Honeypot = "website";

    private static readonly HashSet<FieldType> Allowed =
    [
        FieldType.Data, FieldType.Text, FieldType.LongText, FieldType.Int, FieldType.Float, FieldType.Decimal, FieldType.Currency, FieldType.Percent,
        FieldType.Check, FieldType.Select, FieldType.Date, FieldType.Datetime, FieldType.Time, FieldType.Email, FieldType.Phone, FieldType.Url,
    ];

    private static readonly HashSet<FieldType> Layout = [FieldType.SectionBreak, FieldType.ColumnBreak, FieldType.TabBreak];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,60}$")]
    private static partial Regex RoutePattern();

    public static bool IsRoute(string? route) => route is not null && RoutePattern().IsMatch(route);

    private static NotFoundException Unavailable() => new("This form is not available.");

    /// <summary>The form as visitors see it. Anything wrong with how it was set up makes it unavailable rather than half working.</summary>
    public async Task<PublicForm> GetAsync(string route, CancellationToken ct = default)
    {
        var (row, _, offered) = await LoadAsync(route, ct);
        var fields = offered.Select(f => new PublicField(
            f.Name, f.Label ?? f.Name, f.Type.ToString(), f.Required,
            f.Type == FieldType.Select ? (f.Options ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [],
            f.Description)).ToList();
        return new PublicForm(route, (string)row["title"]!, row["intro"] as string, fields);
    }

    private async Task<(Dictionary<string, object?> Row, DocTypeMeta Meta, IReadOnlyList<DocFieldMeta> Offered)> LoadAsync(string route, CancellationToken ct)
    {
        if (!IsRoute(route))
            throw Unavailable();
        var row = await repository.GetAsync(registry.Get(DocTypeName), route, ct);
        if (row is null || !IsTrue(row["published"]))
            throw Unavailable();

        var name = row["doc_type"] as string ?? string.Empty;
        if (registry.SourceOf(name) is not { } source || source == DocTypeSource.Core)
            throw Unavailable();
        var meta = registry.Get(name);
        if (meta.IsChildTable || meta.IsSingle || meta.Module == "Core")
            throw Unavailable();

        var wanted = (row["fields"] as string ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0 || wanted.Count > MaxFields)
            throw Unavailable();

        var offered = new List<DocFieldMeta>();
        foreach (var field in wanted)
        {
            var found = meta.Fields.FirstOrDefault(f => f.Name == field);
            if (found is null || !Allowed.Contains(found.Type) || found.Hidden || found.ReadOnly || found.PermLevel != 0
                || found.Formula is not null || found.FetchFrom is not null || found.ReadOnlyDependsOn is not null)
                throw Unavailable(); // a field that cannot be asked for: fail closed rather than quietly drop it
            offered.Add(found);
        }

        // Every field the DocType insists on must be asked, or nobody could ever submit.
        if (meta.Fields.Any(f => f.Required && f.Default is null && !Layout.Contains(f.Type) && offered.All(o => o.Name != f.Name)))
            throw Unavailable();
        return (row, meta, offered);
    }

    private static bool IsTrue(object? value) => value is true or 1 or 1L || value is string { Length: > 0 } s && (s == "1" || s == "true");

    /// <summary>Takes one submission. Returns the message to show; it never says what was created.</summary>
    public async Task<string> SubmitAsync(string route, IReadOnlyDictionary<string, object?> input, string source, CancellationToken ct = default)
    {
        var (row, meta, offered) = await LoadAsync(route, ct);
        var message = row["success_message"] as string is { Length: > 0 } m ? m : "Thank you. We have received your answers.";

        var perHour = Math.Clamp(row["max_per_hour"] is null ? 10 : Convert.ToInt32(row["max_per_hour"]), 1, 1000);
        if (!hits.TryHit($"form:{route}:{source}", perHour, TimeSpan.FromHours(1)) || !hits.TryHit($"form:{route}", perHour * 50, TimeSpan.FromHours(1)))
            throw new RateLimitedException();

        // A person never sees this field; a bot that fills in everything does. It looks like success so the bot learns nothing.
        if (input.TryGetValue(Honeypot, out var trap) && trap is string { Length: > 0 })
            return message;

        var names = offered.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in input)
        {
            if (key == Honeypot)
                continue;
            if (!names.Contains(key))
                throw new DocumentValidationException([new FieldError(key, "This form does not ask for that.")]);
            if (value is string { Length: > MaxValueLength })
                throw new DocumentValidationException([new FieldError(key, $"Please keep this under {MaxValueLength} characters.")]);
            if (value is not (null or string or bool or int or long or double or decimal))
                throw new DocumentValidationException([new FieldError(key, "That is not a valid answer.")]);
            values[key] = value;
        }

        try
        {
            await documents.InsertAsync(meta.Name, values, new UserContext(WebUser, [UserContext.AdministratorRole]), ct);
        }
        catch (DocumentValidationException e)
        {
            var mine = e.Errors.Where(x => names.Contains(x.Field)).ToList();
            throw mine.Count > 0 ? new DocumentValidationException(mine) : new DocumentValidationException("This form could not be sent. Please try again later.");
        }

        return message;
    }
}
