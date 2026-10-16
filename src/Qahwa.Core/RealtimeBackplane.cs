using System.Globalization;
using System.Threading.Channels;
using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>
/// Lets several servers share live events through the site's database (ADR-086): an event published on one server is written to the
/// <c>Realtime Event</c> table, and every server polls the table about once a second and hands the events of the other servers to its
/// own connections. Events carry no data of documents, only where to look, so the table holds nothing private. Rows are removed after
/// two minutes. It is optional (<c>Qahwa:Realtime:Backplane = database</c>): with one server the in-memory hub alone is enough.
/// </summary>
public sealed class RealtimeBackplane(IMetaRegistry registry, IDocumentRepository repository, RealtimeHub hub, TimeProvider clock) : IRealtimePublisher
{
    public const string DocTypeName = "Realtime Event";
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(2);
    private const int MaxQueue = 1000;
    private const int MaxPerPoll = 500;

    private readonly string _origin = Guid.NewGuid().ToString("N");
    private readonly Channel<RealtimeEvent> _outbox = Channel.CreateBounded<RealtimeEvent>(new BoundedChannelOptions(MaxQueue) { FullMode = BoundedChannelFullMode.DropOldest });
    private string? _last;

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    /// <summary>Delivers to this server's connections at once and queues the event for the other servers.</summary>
    public void Publish(RealtimeEvent message)
    {
        hub.Publish(message);
        _outbox.Writer.TryWrite(message);
    }

    /// <summary>Writes the queued events to the database. Returns how many.</summary>
    public async Task<int> FlushAsync(CancellationToken ct = default)
    {
        var written = 0;
        while (_outbox.Reader.TryRead(out var message))
        {
            var row = HistoryRecorder.Standard("Administrator", clock.GetUtcNow().UtcDateTime);
            // A name that sorts in the order the events happened, so every server reads them in that order.
            row["name"] = NamingService.FormatSeries("RT-", 10, await repository.NextSeriesAsync("RT-", ct));
            row["origin"] = _origin;
            row["event_type"] = message.Type;
            row["event_user"] = message.User;
            row["ref_doctype"] = message.DocType;
            row["ref_docname"] = message.Name;
            row["deleted"] = message.Deleted;
            await repository.InsertAsync(Meta, row, null, ct);
            written++;
        }

        return written;
    }

    /// <summary>
    /// Hands the events other servers wrote since the last poll to this server's connections, and removes old rows. The first poll
    /// only notes where the table ends (a server that has just started does not replay what happened before it).
    /// </summary>
    public async Task<int> PollAsync(CancellationToken ct = default)
    {
        var meta = Meta;
        if (_last is null)
        {
            var newest = await repository.ListAsync(meta, new ListQuery { Fields = ["name"], OrderBy = "name", Descending = true, Limit = 1 }, ct);
            _last = newest.Count == 0 ? string.Empty : (string)newest[0]["name"]!;
            return 0;
        }

        var filters = new List<QueryFilter>();
        if (_last.Length > 0)
            filters.Add(new QueryFilter("name", FilterOp.Gt, _last));
        var rows = await repository.ListAsync(meta, new ListQuery { Filters = filters, OrderBy = "name", Descending = false, Limit = MaxPerPoll }, ct);
        var delivered = 0;
        foreach (var row in rows)
        {
            _last = (string)row["name"]!;
            if (string.Equals(row.GetValueOrDefault("origin") as string, _origin, StringComparison.Ordinal))
                continue; // this server's own event: already delivered when it was published
            hub.Publish(new RealtimeEvent(
                row.GetValueOrDefault("event_type") as string ?? string.Empty, row.GetValueOrDefault("event_user") as string ?? string.Empty,
                row.GetValueOrDefault("ref_doctype") as string, row.GetValueOrDefault("ref_docname") as string,
                row.GetValueOrDefault("deleted") is true or 1 or 1L or "1"));
            delivered++;
        }

        await PurgeAsync(ct);
        return delivered;
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - Retention;
        var old = await repository.ListAsync(Meta, new ListQuery { Filters = [new("creation", FilterOp.Lt, cutoff)], Fields = ["name"], Limit = 200 }, ct);
        foreach (var row in old)
            await repository.DeleteAsync(Meta, (string)row["name"]!, null, ct);
    }
}
