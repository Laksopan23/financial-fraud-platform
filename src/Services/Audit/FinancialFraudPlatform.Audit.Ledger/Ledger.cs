using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.EventBus;
using FinancialFraudPlatform.Security;
using Marten;
using MassTransit;

namespace FinancialFraudPlatform.Audit.Ledger;

public sealed record LedgerPayload(string StreamId, long Sequence, string PreviousHash, string KeyId,
    RiskScoredEvent Decision, DateTimeOffset RecordedAt);
public sealed record LedgerEntry(LedgerPayload Payload, string Hash);
public sealed class AuditTrail { }
public sealed class LedgerHead
{
    public string Id { get; set; } = "";
    public long Sequence { get; set; }
    public string Hash { get; set; } = "";
}
public sealed class AuditReceipt
{
    public string Id { get; set; } = "";
    public string DecisionDigest { get; set; } = "";
    public LedgerEntry Entry { get; set; } = null!;
}
public static class LedgerHash
{
    public static readonly string Genesis = new('0', 64);
    public static int Shard(Guid transactionId) => SHA256.HashData(transactionId.ToByteArray())[0] % 64;
    public static string Stream(string tenant, int shard)
    {
        if (shard is < 0 or >= 64) throw new ValidationException("invalid_shard");
        return $"{Identity.Tenant(tenant)}:audit:{shard:D2}";
    }
    public static byte[] Canonical(LedgerPayload payload) => Encoding.UTF8.GetBytes(Json.Encode(payload));
}
public sealed class AuditConsumer(IDocumentStore store, AuditSigner signer) : IConsumer<RiskScoredEvent>
{
    public Task Consume(ConsumeContext<RiskScoredEvent> context) => ProcessAsync(context.Message, context.CancellationToken, context.Headers.Get<string>("ff-trace"));
    public async Task ProcessAsync(RiskScoredEvent decision, CancellationToken ct, string? parentId = null)
    {
        using var activity = Telemetry.Source.StartActivity("audit.commit", ActivityKind.Consumer, parentId);
        string id = Identity.Scoped(decision.TenantId, decision.TransactionId);
        string digest = Json.Digest(decision);
        await using var session = store.LightweightSession();
        var prior = await session.LoadAsync<AuditReceipt>(id, ct);
        if (prior is not null)
        {
            if (prior.DecisionDigest != digest) throw new ConflictException("scoring_decision_conflict");
            return;
        }
        string stream = LedgerHash.Stream(decision.TenantId, LedgerHash.Shard(decision.TransactionId));
        var head = await session.LoadAsync<LedgerHead>(stream, ct);
        var now = DateTimeOffset.UtcNow;
        var payload = new LedgerPayload(stream, (head?.Sequence ?? 0) + 1, head?.Hash ?? LedgerHash.Genesis,
            signer.ActiveKeyId, decision, now);
        var entry = new LedgerEntry(payload, signer.Sign(payload.KeyId, LedgerHash.Canonical(payload)));
        if (head is null)
        {
            session.Events.StartStream<AuditTrail>(stream, entry);
            head = new LedgerHead { Id = stream, Sequence = payload.Sequence, Hash = entry.Hash };
            session.Insert(head);
        }
        else
        {
            session.Events.Append(stream, entry);
            head.Sequence = payload.Sequence; head.Hash = entry.Hash;
            session.Store(head);
        }
        session.Insert(new AuditReceipt { Id = id, DecisionDigest = digest, Entry = entry });
        Outbox.Enqueue(session, new AuditLogCreatedEvent(Guid.NewGuid(), decision.TenantId, decision.TransactionId,
            stream, payload.Sequence, entry.Hash, now));
        if (decision.Assessment.Tier >= RiskTier.Medium)
            Outbox.Enqueue(session, new FraudFlaggedEvent(Guid.NewGuid(), decision.TenantId, decision.TransactionId,
                decision.Assessment.Probability, decision.Assessment.Tier, decision.Assessment.ModelVersion,
                decision.Assessment.SyntheticDemo, decision.ScoredAt, entry.Hash));
        await session.SaveChangesAsync(ct);
    }
}
public sealed record LedgerVerification(bool Valid, long CheckedThrough, bool HasMore, string LastHash);
public sealed class LedgerVerifier(IDocumentStore store, AuditSigner signer)
{
    public async Task<LedgerVerification> VerifyAsync(string tenant, int shard, long after, int limit, CancellationToken ct)
    {
        if (after < 0 || limit is < 1 or > 1000) throw new ValidationException("invalid_page");
        string stream = LedgerHash.Stream(tenant, shard);
        await using var session = store.QuerySession();
        var head = await session.LoadAsync<LedgerHead>(stream, ct);
        if (head is null && await session.Events.QueryRawEventDataOnly<LedgerEntry>()
            .Where(e => e.Payload.StreamId == stream).AnyAsync(ct))
        {
            // The first append may have committed between the preceding two reads.
            head = await session.LoadAsync<LedgerHead>(stream, ct);
            if (head is null) return new(false, after, false, LedgerHash.Genesis);
        }
        long expectedHead = head?.Sequence ?? 0;
        if (after > expectedHead) return new(false, after, false, LedgerHash.Genesis);
        string previous = LedgerHash.Genesis;
        if (after > 0)
        {
            var anchor = await session.Events.QueryRawEventDataOnly<LedgerEntry>()
                .Where(e => e.Payload.StreamId == stream && e.Payload.Sequence == after).FirstOrDefaultAsync(ct);
            if (anchor is null || !signer.Verify(anchor.Payload.KeyId, LedgerHash.Canonical(anchor.Payload), anchor.Hash))
                return new(false, after, false, previous);
            previous = anchor.Hash;
        }
        var entries = await session.Events.QueryRawEventDataOnly<LedgerEntry>()
            .Where(e => e.Payload.StreamId == stream && e.Payload.Sequence > after && e.Payload.Sequence <= expectedHead)
            .OrderBy(e => e.Payload.Sequence).Take(limit).ToListAsync(ct);
        long checkedThrough = after;
        foreach (var entry in entries)
        {
            if (entry.Payload.Sequence != checkedThrough + 1 || entry.Payload.PreviousHash != previous
                || !signer.Verify(entry.Payload.KeyId, LedgerHash.Canonical(entry.Payload), entry.Hash))
                return new(false, checkedThrough, true, previous);
            checkedThrough++; previous = entry.Hash;
        }
        bool more = (head?.Sequence ?? 0) > checkedThrough;
        bool valid = more ? entries.Count == limit : checkedThrough == (head?.Sequence ?? 0)
            && previous == (head?.Hash ?? LedgerHash.Genesis);
        return new(valid, checkedThrough, more, previous);
    }
}
