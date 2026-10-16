using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Qahwa.Core;

/// <summary>
/// A short message about something that just happened. It carries no data of the document, only where to look: the Desk asks the server
/// for the rest, and the server checks permissions then as for any request.
/// </summary>
/// <param name="Type"><c>notification</c> (a person has a new notification) or <c>doc</c> (a document was saved or deleted).</param>
/// <param name="User">For a notification, the person it is for. For a document, the person who changed it.</param>
public sealed record RealtimeEvent(string Type, string User, string? DocType = null, string? Name = null, bool Deleted = false);

/// <summary>Where the engine says that something happened (the real one is <see cref="RealtimeHub"/>; tests may use a recorder).</summary>
public interface IRealtimePublisher
{
    void Publish(RealtimeEvent message);
}

/// <summary>
/// Keeps the open connections of signed-in people and hands each of them the events meant for it: its person's notifications, and,
/// when it is watching a document, changes to that document. Everything is in this server's memory (with several servers, an event
/// reaches only the people connected to the server where it happened). A slow reader loses its oldest events instead of holding the
/// server's memory, and a person may have only a few connections open.
/// </summary>
public sealed class RealtimeHub : IRealtimePublisher
{
    public const int MaxConnectionsPerUser = 6;
    public const int MaxConnections = 5000;
    private const int QueueSize = 64;

    private readonly ConcurrentDictionary<long, Subscription> _subscriptions = new();
    private long _next;

    public int Connections => _subscriptions.Count;

    /// <summary>Opens a connection for <paramref name="user"/>, optionally watching one document. Dispose it to close it.</summary>
    /// <returns>Null when there are too many connections (this person's, or the server's).</returns>
    public Subscription? Subscribe(string user, string? doctype = null, string? name = null)
    {
        if (_subscriptions.Count >= MaxConnections || _subscriptions.Values.Count(s => string.Equals(s.User, user, StringComparison.Ordinal)) >= MaxConnectionsPerUser)
            return null;

        var id = Interlocked.Increment(ref _next);
        var subscription = new Subscription(this, id, user, doctype, name);
        _subscriptions[id] = subscription;
        return subscription;
    }

    public void Publish(RealtimeEvent message)
    {
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.Wants(message))
                subscription.Writer.TryWrite(message);
        }
    }

    private void Remove(long id) => _subscriptions.TryRemove(id, out _);

    public sealed class Subscription : IDisposable
    {
        private readonly RealtimeHub _hub;
        private readonly long _id;
        private readonly Channel<RealtimeEvent> _channel = Channel.CreateBounded<RealtimeEvent>(
            new BoundedChannelOptions(QueueSize) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        internal Subscription(RealtimeHub hub, long id, string user, string? doctype, string? name)
        {
            _hub = hub;
            _id = id;
            User = user;
            DocType = doctype;
            Name = name;
        }

        public string User { get; }

        public string? DocType { get; }

        public string? Name { get; }

        public ChannelReader<RealtimeEvent> Reader => _channel.Reader;

        internal ChannelWriter<RealtimeEvent> Writer => _channel.Writer;

        internal bool Wants(RealtimeEvent message) => message.Type switch
        {
            "notification" => DocType is null && string.Equals(message.User, User, StringComparison.Ordinal),
            "doc" => DocType is not null && string.Equals(message.DocType, DocType, StringComparison.Ordinal) && string.Equals(message.Name, Name, StringComparison.Ordinal),
            _ => false,
        };

        public void Dispose()
        {
            _hub.Remove(_id);
            _channel.Writer.TryComplete();
        }
    }
}
