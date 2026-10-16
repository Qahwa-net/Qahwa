using Qahwa.Core;
using Qahwa.Testing;

namespace Qahwa.Data.Tests;

public class PostgresPatchTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private sealed class Counting(string name, int[] counter) : IPatch
    {
        public string Name => name;

        public async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
        {
            Interlocked.Increment(ref counter[0]);
            await Task.Delay(150, ct);
        }
    }

    [SkippableFact]
    public async Task Servers_starting_at_the_same_moment_run_a_patch_exactly_once_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        var counter = new int[1];
        var runners = Enumerable.Range(0, 5).Select(_ => new PatchRunner(db.Registry, db.Repository, [new Counting("2026_03_one", counter)], new EmptyServices(), db.Clock)).ToList();

        var results = await Task.WhenAll(runners.Select(r => r.RunPendingAsync()));

        Assert.Equal(1, counter[0]);
        Assert.Equal(1, results.Sum(r => r.Ran.Count));
        Assert.Equal("Done", (await db.Documents.GetAsync("Patch Log", "2026_03_one", TestUsers.Admin))["status"]);
    }
}
