using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Qahwa.Core.Expressions;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

// Run all:   dotnet run -c Release --project benchmarks/Qahwa.Benchmarks -- --filter "*"
// Run some:  dotnet run -c Release --project benchmarks/Qahwa.Benchmarks -- --filter "*Expression*"
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>The expression language: the cost of evaluating a rule on each save, and of the first parse.</summary>
[MemoryDiagnoser]
public class ExpressionBenchmarks
{
    private ExpressionContext _context = null!;
    private int _unique;

    [GlobalSetup]
    public void Setup() => _context = new ExpressionContext
    {
        Fields = new Dictionary<string, object?>
        {
            ["qty"] = 12d,
            ["rate"] = 4.5d,
            ["status"] = "Open",
            ["lines"] = new List<object?> { new Dictionary<string, object?> { ["amount"] = 10d }, new Dictionary<string, object?> { ["amount"] = 32d } },
        },
        User = "clerk@example.com",
        Roles = ["Clerk"],
    };

    [Benchmark]
    public object? Arithmetic() => Expression.Parse("qty * rate + 3").Evaluate(_context);

    [Benchmark]
    public object? Condition() => Expression.Parse("status == 'Open' && qty > 10 ? 'big' : 'small'").Evaluate(_context);

    [Benchmark]
    public object? SumOverRows() => Expression.Parse("sum(lines, 'amount')").Evaluate(_context);

    [Benchmark]
    public Expression ParseUncached() => Expression.Parse($"qty * {_unique++} + rate");
}

/// <summary>Saving and reading documents through the whole engine (validation, permissions, hooks), on the in-memory store, so the numbers are the engine's own cost and not the database's.</summary>
[MemoryDiagnoser]
public class DocumentBenchmarks
{
    private TestEngine _engine = null!;
    private string _name = null!;
    private int _counter;

    [GlobalSetup]
    public async Task Setup()
    {
        _engine = new TestEngine();
        await _engine.Documents.InsertAsync("Territory", Values(("territory_name", "North")), TestUsers.Manager);
        var saved = await _engine.Documents.InsertAsync("Customer", Values(("customer_name", "Seed"), ("territory", "North")), TestUsers.Manager);
        _name = (string)saved["name"]!;
    }

    [Benchmark]
    public Task Insert() =>
        _engine.Documents.InsertAsync("Customer", Values(("customer_name", $"Customer {Interlocked.Increment(ref _counter)}"), ("territory", "North"), ("credit_limit", 1500m)), TestUsers.Manager);

    [Benchmark]
    public Task Get() => _engine.Documents.GetAsync("Customer", _name, TestUsers.Manager);

    [Benchmark]
    public Task Update() => _engine.Documents.UpdateAsync("Customer", _name, Values(("credit_limit", 2000m + Interlocked.Increment(ref _counter))), TestUsers.Manager);

    [Benchmark]
    public async Task<int> ListFifty() =>
        (await _engine.Documents.ListAsync("Customer", new Qahwa.Core.ListRequest { Limit = 50 }, TestUsers.Manager)).Data.Count;
}
