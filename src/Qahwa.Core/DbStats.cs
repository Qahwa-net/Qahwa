using System.Diagnostics;

namespace Qahwa.Core;

/// <summary>
/// How many statements the current request sent to the database and how long it spent in them (ADR-112). It counts only: no statement
/// text or value is kept. A request that sends hundreds of statements is the usual cause of a slow page, and this makes that visible.
/// </summary>
public sealed class DbStats
{
    private static readonly AsyncLocal<DbStats?> Holder = new();
    private int _statements;
    private long _ticks;

    public int Statements => Volatile.Read(ref _statements);

    public long Milliseconds => Interlocked.Read(ref _ticks) * 1000 / Stopwatch.Frequency;

    public static DbStats? Current => Holder.Value;

    /// <summary>Starts counting for the current request (and what it starts).</summary>
    public static DbStats Begin()
    {
        var stats = new DbStats();
        Holder.Value = stats;
        return stats;
    }

    /// <summary>Times one statement; dispose it when the statement is done. Does nothing when no request is being counted.</summary>
    public static Measurement Measure() => new(Holder.Value);

    public readonly struct Measurement : IDisposable
    {
        private readonly DbStats? _stats;
        private readonly long _start;

        internal Measurement(DbStats? stats)
        {
            _stats = stats;
            _start = stats is null ? 0 : Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_stats is null)
                return;
            Interlocked.Increment(ref _stats._statements);
            Interlocked.Add(ref _stats._ticks, Stopwatch.GetTimestamp() - _start);
        }
    }
}
