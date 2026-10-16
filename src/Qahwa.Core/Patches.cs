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

public sealed record PatchResult(IReadOnlyList<string> Ran, IReadOnlyList<string> Skipped);

public sealed class PatchFailedException(string patch, Exception inner)
    : Exception($"The patch '{patch}' failed: {inner.Message}", inner);

/// <summary>
/// Runs the patches that have not run on this site yet. The Patch Log (a DocType, so System Managers can read it) is the record: a patch
/// is claimed by inserting its row (the name is unique, so on several servers exactly one runs it), and marked Done or Failed. A failed
/// patch stops the run, because later patches may rely on it, and is tried again on the next start; a run that was claimed but never
/// finished (the server died) is taken over after <see cref="StaleAfter"/>.
/// </summary>
public sealed partial class PatchRunner(IMetaRegistry registry, IDocumentRepository repository, IEnumerable<IPatch> patches, IServiceProvider services, TimeProvider clock)
{
    public const string DocTypeName = "Patch Log";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{0,100}$")]
    private static partial Regex NamePattern();

    public static bool ValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    private DocTypeMeta Log => registry.Get(DocTypeName);

    private DateTime Now() { var utc = clock.GetUtcNow().UtcDateTime; return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc); }

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
        foreach (var patch in patches.OrderBy(p => p.Name, StringComparer.Ordinal))
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

    /// <summary>True when this server now owns the run: the patch never ran, failed earlier, or was claimed long ago and never finished.</summary>
    private async Task<bool> TryClaimAsync(string name, CancellationToken ct)
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
        if (status == "Done" || (status == "Running" && now - started < StaleAfter))
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
