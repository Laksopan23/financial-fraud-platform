using System.Collections.Concurrent;
using System.Threading.Channels;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.Security;
using Marten;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FinancialFraudPlatform.Dashboard;

public sealed class AlertReadModel
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public Guid TransactionId { get; set; }
    public float Probability { get; set; }
    public RiskTier Tier { get; set; }
    public string ModelVersion { get; set; } = "";
    public bool SyntheticDemo { get; set; }
    public DateTimeOffset ScoredAt { get; set; }
    public string AuditHash { get; set; } = "";
}
public sealed record AlertsQuery(string Tenant, int Limit = 100) : IRequest<IReadOnlyList<AlertReadModel>>;
public sealed class AlertsQueryHandler(IDocumentStore store) : IRequestHandler<AlertsQuery, IReadOnlyList<AlertReadModel>>
{
    public async Task<IReadOnlyList<AlertReadModel>> Handle(AlertsQuery request, CancellationToken ct)
    {
        string tenant = Identity.Tenant(request.Tenant);
        await using var session = store.QuerySession();
        return await session.Query<AlertReadModel>().Where(x => x.TenantId == tenant)
            .OrderByDescending(x => x.ScoredAt).Take(Math.Clamp(request.Limit, 1, 100)).ToListAsync(ct);
    }
}
public sealed class AlertFeed
{
    private readonly ConcurrentDictionary<Guid, (string Tenant, Channel<AlertReadModel> Channel)> listeners = new();
    public (Guid Id, ChannelReader<AlertReadModel> Reader) Subscribe(string tenant)
    {
        var channel = Channel.CreateBounded<AlertReadModel>(new BoundedChannelOptions(100)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        Guid id = Guid.NewGuid(); listeners[id] = (Identity.Tenant(tenant), channel);
        return (id, channel.Reader);
    }
    public void Unsubscribe(Guid id)
    {
        if (listeners.TryRemove(id, out var listener)) listener.Channel.Writer.TryComplete();
    }
    public void Publish(AlertReadModel value)
    {
        foreach (var listener in listeners.Values)
            if (listener.Tenant == value.TenantId) listener.Channel.Writer.TryWrite(value);
    }
}
[Authorize(Policy = "Analyst")]
public sealed class AlertsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Context.User!.RequireTenant());
        await base.OnConnectedAsync();
    }
}
public sealed class AlertConsumer(IDocumentStore store, AlertFeed feed, IHubContext<AlertsHub> hub) : IConsumer<FraudFlaggedEvent>
{
    public async Task Consume(ConsumeContext<FraudFlaggedEvent> context)
    {
        var value = context.Message;
        string id = Identity.Scoped(value.TenantId, value.TransactionId);
        await using var session = store.LightweightSession();
        var alert = await session.LoadAsync<AlertReadModel>(id, context.CancellationToken);
        if (alert is null)
        {
            alert = new AlertReadModel { Id = id, TenantId = value.TenantId, TransactionId = value.TransactionId,
                Probability = value.Probability, Tier = value.Tier, ModelVersion = value.ModelVersion,
                SyntheticDemo = value.SyntheticDemo, ScoredAt = value.ScoredAt, AuditHash = value.AuditHash };
            session.Insert(alert);
            await session.SaveChangesAsync(context.CancellationToken);
        }
        else if (alert.AuditHash != value.AuditHash) throw new ConflictException("alert_conflict");
        feed.Publish(alert);
        await hub.Clients.Group(value.TenantId).SendAsync("RiskAlert", alert, context.CancellationToken);
    }
}

public sealed class AuditCheckpoint
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string StreamId { get; set; } = "";
    public long Sequence { get; set; }
    public string Hash { get; set; } = "";
}
public sealed class AuditCheckpointConsumer(IDocumentStore store) : IConsumer<AuditLogCreatedEvent>
{
    public async Task Consume(ConsumeContext<AuditLogCreatedEvent> context)
    {
        var value = context.Message;
        await using var session = store.LightweightSession();
        string id = Identity.Scoped(value.TenantId, value.TransactionId);
        var existing = await session.LoadAsync<AuditCheckpoint>(id, context.CancellationToken);
        if (existing is not null)
        {
            if (existing.Hash != value.Hash) throw new ConflictException("checkpoint_conflict");
            return;
        }
        session.Insert(new AuditCheckpoint { Id = id, TenantId = value.TenantId,
            StreamId = value.StreamId, Sequence = value.Sequence, Hash = value.Hash });
        await session.SaveChangesAsync(context.CancellationToken);
    }
}
