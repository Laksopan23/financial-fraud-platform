using System.Diagnostics;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.EventBus;
using Marten;
using MassTransit;

namespace FinancialFraudPlatform.Detection.Worker;

public sealed class DetectionDecision
{
    public string Id { get; set; } = "";
    public string InputDigest { get; set; } = "";
    public RiskScoredEvent Decision { get; set; } = null!;
}
public sealed class DetectionConsumer(IDocumentStore store, IFeatureStore features, IFraudScorer scorer) : IConsumer<TransactionReceivedEvent>
{
    public async Task Consume(ConsumeContext<TransactionReceivedEvent> context) =>
        await ProcessAsync(context.Message, context.CancellationToken, context.Headers.Get<string>("ff-trace"));
    public async Task ProcessAsync(TransactionReceivedEvent input, CancellationToken ct, string? parentId = null)
    {
        using var activity = Telemetry.Source.StartActivity("detection.score", ActivityKind.Consumer, parentId);
        string key = Identity.Scoped(input.TenantId, input.TransactionId);
        string digest = Json.Digest(input);
        await using var session = store.LightweightSession();
        var existing = await session.LoadAsync<DetectionDecision>(key, ct);
        if (existing is not null)
        {
            if (existing.InputDigest != digest) throw new ConflictException("transaction_event_conflict");
            return;
        }
        if (input.Currency != "USD") throw new ValidationException("unsupported_scoring_currency");
        FeatureVector vector = await features.ObserveAsync(input, ct);
        RiskAssessment risk = scorer.Score(vector);
        var decision = new RiskScoredEvent(Guid.NewGuid(), input.TenantId, input.TransactionId, vector, risk, DateTimeOffset.UtcNow, digest);
        session.Insert(new DetectionDecision { Id = key, InputDigest = digest, Decision = decision });
        Outbox.Enqueue(session, decision);
        await session.SaveChangesAsync(ct);
        Telemetry.Inference.Record(risk.InferenceMilliseconds);
        Telemetry.Pipeline.Record((decision.ScoredAt - input.ReceivedAt).TotalMilliseconds);
    }
}
