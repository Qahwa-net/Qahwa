using Qahwa.Core;

namespace Qahwa.Testing;

/// <summary>A mailbox held in memory: tests put messages in it and read what the system asked for. Can be told to fail.</summary>
public sealed class FakeMailbox : IMailboxClient
{
    private readonly List<ReceivedMail> _messages = [];
    public long UidValidity { get; set; } = 1;
    public bool Broken { get; set; }
    public ImapSettings? LastSettings { get; private set; }
    public long LastAfterUid { get; private set; }

    public void Add(ReceivedMail mail) => _messages.Add(mail);

    public Task<MailboxBatch> FetchAsync(ImapSettings settings, long afterUid, int max, int maxBytes, CancellationToken ct = default)
    {
        if (Broken) throw new InvalidOperationException("server down");
        LastSettings = settings;
        LastAfterUid = afterUid;
        var newer = _messages.Where(m => m.Uid > afterUid).OrderBy(m => m.Uid).ToList();
        var taken = newer.Take(max).ToList();
        // Like the real one: with nothing to download the highest number is reported; otherwise only what was read counts as seen.
        var seen = max == 0 ? newer : taken;
        return Task.FromResult(new MailboxBatch(taken, UidValidity, Math.Max(afterUid, seen.Count == 0 ? 0 : seen.Max(m => m.Uid))));
    }

    public Task<int> TestAsync(ImapSettings settings, CancellationToken ct = default)
    {
        if (Broken) throw new InvalidOperationException("server down");
        LastSettings = settings;
        return Task.FromResult(_messages.Count);
    }
}
