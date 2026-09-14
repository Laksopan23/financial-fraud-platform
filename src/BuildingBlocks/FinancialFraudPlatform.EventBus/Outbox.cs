using System.Diagnostics;
using FinancialFraudPlatform.Core;
using Marten;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FinancialFraudPlatform.EventBus;

public sealed class OutboxEnvelope
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "";
    public string? TraceParent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset LeaseUntil { get; set; } = DateTimeOffset.UnixEpoch;
    public Guid LeaseId { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public int Attempts { get; set; }
}
public static class Outbox
{
    public static OutboxEnvelope Enqueue<T>(IDocumentSession session, T message) where T : IIntegrationEvent
    {
        var envelope = new OutboxEnvelope
        {
            Id = message.EventId, Kind = typeof(T).Name, Payload = Json.Encode(message),
            CreatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow,
            TraceParent = Activity.Current?.Id
        };
        session.Insert(envelope);
        return envelope;
    }
    public static object Decode(OutboxEnvelope envelope) => envelope.Kind switch
    {
        nameof(TransactionReceivedEvent) => Json.Decode<TransactionReceivedEvent>(envelope.Payload),
        nameof(RiskScoredEvent) => Json.Decode<RiskScoredEvent>(envelope.Payload),
        nameof(FraudFlaggedEvent) => Json.Decode<FraudFlaggedEvent>(envelope.Payload),
        nameof(AuditLogCreatedEvent) => Json.Decode<AuditLogCreatedEvent>(envelope.Payload),
        _ => throw new InvalidDataException("unknown_message_contract")
    };
}
public static class DatabaseErrors
{
    public static bool IsUniqueViolation(Exception e) =>
        e is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
        || e.InnerException is not null && IsUniqueViolation(e.InnerException);
}

public sealed class OutboxDispatcher(IDocumentStore store, IBus bus, ILogger<OutboxDispatcher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int published = await DispatchOnceAsync(stoppingToken);
                await Task.Delay(published == 32 ? 1 : 10, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(new EventId(2101), ex, "outbox_cycle_failed");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
    private Task PublishAsync<T>(T message, Guid id, CancellationToken ct) where T : class =>
        bus.Publish(message, context =>
        {
            context.MessageId = id;
            context.Headers.Set("ff-trace", Activity.Current?.Id);
        }, ct);
    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var query = store.QuerySession();
        var candidates = await query.Query<OutboxEnvelope>()
            .Where(x => x.DeliveredAt == null && x.NextAttemptAt <= now && x.LeaseUntil <= now)
            .OrderBy(x => x.CreatedAt).Take(32).ToListAsync(ct);
        int delivered = 0;
        foreach (var candidate in candidates)
        {
            OutboxEnvelope? leased;
            try
            {
                await using var claim = store.LightweightSession();
                leased = await claim.LoadAsync<OutboxEnvelope>(candidate.Id, ct);
                if (leased is null || leased.DeliveredAt is not null || leased.NextAttemptAt > DateTimeOffset.UtcNow
                    || leased.LeaseUntil > DateTimeOffset.UtcNow)
                    continue;
                leased.LeaseId = Guid.NewGuid();
                leased.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(60);
                leased.Attempts++;
                claim.Store(leased);
                await claim.SaveChangesAsync(ct);
            }
            catch (Marten.Exceptions.ConcurrencyException) { continue; }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var activity = Telemetry.Source.StartActivity("outbox.publish", ActivityKind.Producer, leased.TraceParent);
                var message = Outbox.Decode(leased);
                await (message switch
                {
                    TransactionReceivedEvent value => PublishAsync(value, leased.Id, timeout.Token),
                    RiskScoredEvent value => PublishAsync(value, leased.Id, timeout.Token),
                    FraudFlaggedEvent value => PublishAsync(value, leased.Id, timeout.Token),
                    AuditLogCreatedEvent value => PublishAsync(value, leased.Id, timeout.Token),
                    _ => throw new InvalidDataException("unknown_message_contract")
                });
                await using var complete = store.LightweightSession();
                var current = await complete.LoadAsync<OutboxEnvelope>(leased.Id, ct);
                if (current is not null && current.LeaseId == leased.LeaseId)
                {
                    current.DeliveredAt = DateTimeOffset.UtcNow;
                    complete.Store(current);
                    await complete.SaveChangesAsync(ct);
                    Telemetry.Published.Add(1);
                    delivered++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(new EventId(2102), ex, "outbox_delivery_failed");
                await using var failed = store.LightweightSession();
                var current = await failed.LoadAsync<OutboxEnvelope>(leased.Id, ct);
                if (current is not null && current.LeaseId == leased.LeaseId && current.DeliveredAt is null)
                {
                    current.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(current.Attempts, 8))) + Random.Shared.NextDouble());
                    current.LeaseUntil = DateTimeOffset.UnixEpoch;
                    failed.Store(current);
                    await failed.SaveChangesAsync(ct);
                }
            }
        }
        return delivered;
    }
}
