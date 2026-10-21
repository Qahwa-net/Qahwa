namespace Qahwa.Core.Tests;

public class DbStatsTests
{
    [Fact]
    public async Task Statements_and_time_are_counted_for_the_current_request_only()
    {
        Assert.Null(DbStats.Current);
        using (DbStats.Measure()) { } // not counting: nothing happens

        var stats = DbStats.Begin();
        using (DbStats.Measure())
            await Task.Delay(20);
        using (DbStats.Measure())
            await Task.Delay(5);

        Assert.Equal(2, stats.Statements);
        Assert.True(stats.Milliseconds >= 15, stats.Milliseconds.ToString());
    }

    [Fact]
    public async Task Work_started_by_the_request_is_counted_too_and_a_new_request_starts_from_zero()
    {
        var first = DbStats.Begin();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            using (DbStats.Measure())
                await Task.Yield();
        })));
        Assert.Equal(20, first.Statements);

        var second = await Task.Run(DbStats.Begin);
        Assert.Equal(0, second.Statements);
        Assert.Equal(20, first.Statements);
    }
}
