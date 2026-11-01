using System.Text.RegularExpressions;

namespace Qahwa.Core;

/// <summary>
/// A one-time data change that goes with a new version: move values to a new field, fill in a default, tidy old rows. A patch has a
/// unique, never-changing name (use a date prefix such as <c>2026_03_01_fill_customer_group</c>) and runs once per site, in name order.
/// Write patches so running one twice does no harm: if a run is interrupted it is tried again.
/// </summary>
public interface IPatch
{
    string Name { get; }

    Task RunAsync(IServiceProvider services, CancellationToken ct = default);
}

/// <summary>A registered patch and what the Patch Log says about it (<c>Pending</c> when it has never run).</summary>
public sealed record PatchInfo(string Name, string Status, DateTime? Started, DateTime? Finished, string? Error);

public sealed record PatchResult(IReadOnlyList<string> Ran, IReadOnlyList<string> Skipped);

public sealed class PatchFailedException(string patch, Exception inner)
    : Exception($"The patch '{patch}' failed: {inner.Message}", inner);

/// <summary>
/// Runs the patches that have not run on this site yet. The Patch Log (a DocType, so System Managers can read it) is the record: a patch
/// is claimed by inserting its row (the name is unique, so on several servers exactly one runs it), and marked Done or Failed. A failed
/// patch stops the run, because later patches may rely on it, and is tried again on the next start; a run that was claimed but never
/// finished (the server died) is taken over after <see cref="StaleAfter"/>.
/// </summary>
public sealed partial class PatchRunner(
    IMetaRegistry registry, IDocumentRepository repository, IEnumerable<IPatch> patches, IServiceProvider services, TimeProvider clock, IEnumerable<IQahwaApp>? apps = null)
{
    public const string DocTypeName = "Patch Log";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{0,100}$")]
    private static partial Regex NamePattern();

    public static bool ValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    private DocTypeMeta Log => registry.Get(DocTypeName);

    private DateTime Now() { var utc = clock.GetUtcNow().UtcDateTime; return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc); }

    /// <summary>The registered patches in the order they run: see <see cref="PatchOrder.Sort"/>.</summary>
    private IReadOnlyList<IPatch> Ordered => PatchOrder.Sort(patches, apps ?? []);

    /// <summary>Checks the patches that were registered (names are valid and unique). Call at start-up so a mistake is found at once.</summary>
    public void Validate()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var patch in patches)
        {
            if (!ValidName(patch.Name))
                throw new InvalidOperationException($"'{patch.Name}' is not a valid patch name (lowercase letters, digits, _ . -, up to 101 characters).");
            if (!seen.Add(patch.Name))
                throw new InvalidOperationException($"The patch name '{patch.Name}' is used twice.");
        }
    }

    public async Task<PatchResult> RunPendingAsync(CancellationToken ct = default)
    {
        Validate();
        var ran = new List<string>();
        var skipped = new List<string>();
        foreach (var patch in Ordered)
        {
            if (!await TryClaimAsync(patch.Name, ct))
            {
                skipped.Add(patch.Name);
                continue;
            }

            try
            {
                await patch.RunAsync(services, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await FinishAsync(patch.Name, "Failed", Cut(ex.ToString(), 4000), ct);
                throw new PatchFailedException(patch.Name, ex);
            }

            await FinishAsync(patch.Name, "Done", null, ct);
            ran.Add(patch.Name);
        }

        return new PatchResult(ran, skipped);
    }

    /// <summary>Every registered patch in the order they run, with its state from the Patch Log (and logged patches that are no longer registered).</summary>
    public async Task<IReadOnlyList<PatchInfo>> ListAsync(CancellationToken ct = default)
    {
        Validate();
        var rows = (await repository.ListAsync(Log, new ListQuery { OrderBy = "name", Descending = false, Limit = 2000 }, ct))
            .ToDictionary(r => (string)r["name"]!, StringComparer.Ordinal);
        PatchInfo Info(string name) => rows.TryGetValue(name, out var r)
            ? new PatchInfo(name, r["status"] as string ?? "Pending", r["started"] as DateTime?, r["finished"] as DateTime?, r["error"] as string)
            : new PatchInfo(name, "Pending", null, null, null);
        var registered = Ordered.Select(p => p.Name).ToList();
        return registered.Select(Info).Concat(rows.Keys.Except(registered, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).Select(Info)).ToList();
    }

    /// <summary>
    /// Runs one patch by name, now. A patch that is pending or failed just runs; one that is Done needs <paramref name="force"/> (it runs
    /// again, so it must be safe to repeat). A patch that another server is running right now is refused. A failure is recorded and thrown.
    /// </summary>
    public async Task<PatchInfo> RunOneAsync(string name, bool force, CancellationToken ct = default)
    {
        Validate();
        var patch = patches.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))
            ?? throw new NotFoundException($"There is no patch named '{name}'.");
        var existing = await repository.GetAsync(Log, patch.Name, ct);
        if (existing is not null && existing["status"] as string == "Done" && !force)
            throw new ConflictException($"'{patch.Name}' has already run. Run it again only if it is safe to repeat.");
        if (!await TryClaimAsync(patch.Name, ct, force))
            throw new ConflictException($"'{patch.Name}' is being run by another server right now.");

        try
        {
            await patch.RunAsync(services, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FinishAsync(patch.Name, "Failed", Cut(ex.ToString(), 4000), ct);
            throw new PatchFailedException(patch.Name, ex);
        }

        await FinishAsync(patch.Name, "Done", null, ct);
        return (await ListAsync(ct)).First(p => p.Name == patch.Name);
    }

    /// <summary>True when this server now owns the run: the patch never ran, failed earlier, or was claimed long ago and never finished. With <paramref name="force"/> a Done patch is taken again too.</summary>
    private async Task<bool> TryClaimAsync(string name, CancellationToken ct, bool force = false)
    {
        var now = Now();
        var row = await repository.GetAsync(Log, name, ct);
        if (row is null)
        {
            var fresh = HistoryRecorder.Standard("system@qahwa", now);
            fresh["name"] = name;
            fresh["patch"] = name;
            fresh["status"] = "Running";
            fresh["started"] = now;
            try
            {
                await repository.InsertAsync(Log, fresh, null, ct);
                return true;
            }
            catch (ConflictException)
            {
                return false; // another server claimed it first
            }
        }

        var status = row["status"] as string;
        var started = row["started"] as DateTime? ?? DateTime.MinValue;
        if ((status == "Done" && !force) || (status == "Running" && now - started < StaleAfter))
            return false;

        // Failed before, or abandoned: take it over, but only if nobody else changed the row meanwhile.
        var modified = (DateTime)row["modified"]!;
        return await repository.UpdateAsync(Log, name,
            new Dictionary<string, object?> { ["status"] = "Running", ["started"] = now, ["finished"] = null, ["error"] = null, ["modified"] = modified.AddTicks(10) > now ? modified.AddTicks(10) : now }, modified, null, ct);
    }

    private async Task FinishAsync(string name, string status, string? error, CancellationToken ct)
    {
        var row = await repository.GetAsync(Log, name, ct);
        var modified = row?["modified"] as DateTime?;
        var now = Now();
        await repository.UpdateAsync(Log, name,
            new Dictionary<string, object?> { ["status"] = status, ["finished"] = now, ["error"] = error, ["modified"] = modified is { } m && m >= now ? m.AddTicks(10) : now }, null, null, ct);
    }

    private static string Cut(string text, int length) => text.Length > length ? text[..length] : text;
}

/// <summary>
/// The order patches run in. Patches that belong to no app (registered directly by the host) run first, in name order. Then the patches
/// of each app, one app at a time, in the order the apps are installed (an app after every app it depends on), each app's own
/// patches in name order. So a patch of an app can rely on its dependencies' data being up to date.
/// </summary>
public static class PatchOrder
{
    public static IReadOnlyList<IPatch> Sort(IEnumerable<IPatch> patches, IEnumerable<IQahwaApp> apps)
    {
        var owner = new Dictionary<IPatch, IQahwaApp>();
        var appList = apps.ToList();
        foreach (var app in appList)
            foreach (var patch in app.Patches)
                owner.TryAdd(patch, app);

        var rank = Rank(appList);
        return patches
            .OrderBy(p => owner.TryGetValue(p, out var app) ? rank[app.Name] : -1)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A position for every app: the order the apps are installed in (after their dependencies, otherwise as added). Apps whose
    /// dependencies cannot be resolved (installing refuses those) just go by name, so ordering patches never fails.
    /// </summary>
    private static Dictionary<string, int> Rank(IReadOnlyList<IQahwaApp> apps)
    {
        IEnumerable<IQahwaApp> order;
        try
        {
            order = new AppCatalog(apps).InstallOrder();
        }
        catch (InvalidOperationException)
        {
            order = apps.OrderBy(a => a.Name, StringComparer.Ordinal);
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var app in order)
            rank.TryAdd(app.Name, rank.Count);
        return rank;
    }
}
