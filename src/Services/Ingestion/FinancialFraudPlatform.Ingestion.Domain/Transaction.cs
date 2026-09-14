using FinancialFraudPlatform.Core;

namespace FinancialFraudPlatform.Ingestion.Domain;

public sealed record TransactionAccepted(Guid TransactionId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed class Transaction : AggregateRoot
{
    public TransactionId Id { get; }
    public string TenantId { get; }
    public Money Value { get; }
    public string MerchantId { get; }
    public GeoPoint Location { get; }
    public DateTimeOffset OccurredAt { get; }
    private Transaction(TransactionId id, string tenant, Money value, string merchant,
        GeoPoint location, DateTimeOffset occurredAt)
    {
        Id = id; TenantId = tenant; Value = value; MerchantId = merchant;
        Location = location; OccurredAt = occurredAt.ToUniversalTime();
    }
    public static Transaction Accept(Guid id, string tenant, Money value, string merchant,
        GeoPoint location, DateTimeOffset occurredAt, DateTimeOffset now)
    {
        if (occurredAt < now.AddMinutes(-5) || occurredAt > now.AddSeconds(30))
            throw new ValidationException("event_time_out_of_bounds");
        var transaction = new Transaction(new(id), Identity.Tenant(tenant), value,
            Identity.Merchant(merchant), location, occurredAt);
        transaction.Raise(new TransactionAccepted(id, now));
        return transaction;
    }
}
