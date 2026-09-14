namespace FinancialFraudPlatform.Core;

public interface IIntegrationEvent
{
    Guid EventId { get; }
    string TenantId { get; }
    Guid TransactionId { get; }
}
public sealed record TransactionReceivedEvent(
    Guid EventId, string TenantId, Guid TransactionId, string CardToken,
    decimal Amount, string Currency, string MerchantId, float MerchantRisk,
    double Latitude, double Longitude, DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt) : IIntegrationEvent;

public enum RiskTier { Low, Medium, Critical }

public sealed record FeatureVector(
    float Amount, float Velocity1m, float Velocity5m, float Velocity1h,
    float DistanceKm, float SpeedKph, float SpendingZScore, float MerchantRisk,
    float TimeOfDayDeltaHours, float HistoryAvailable, float IsOutOfOrder)
{
    public const int Count = 11;
    public const string SchemaVersion = "fraud-features-v1";
    public static readonly string[] Names =
    [
        "amount", "velocity_1m", "velocity_5m", "velocity_1h",
        "distance_km", "speed_kph", "spending_z", "merchant_risk",
        "time_delta_hours", "history_available", "out_of_order"
    ];
    public float[] ToArray()
    {
        float[] values = [Amount, Velocity1m, Velocity5m, Velocity1h, DistanceKm,
            SpeedKph, SpendingZScore, MerchantRisk, TimeOfDayDeltaHours,
            HistoryAvailable, IsOutOfOrder];
        if (values.Any(x => !float.IsFinite(x))) throw new ValidationException("nonfinite_features");
        return values;
    }
}

public sealed record RiskAssessment(float Probability, RiskTier Tier, string ModelVersion,
    bool SyntheticDemo, double InferenceMilliseconds);
public sealed record RiskScoredEvent(Guid EventId, string TenantId, Guid TransactionId,
    FeatureVector Features, RiskAssessment Assessment, DateTimeOffset ScoredAt,
    string InputDigest) : IIntegrationEvent;
public sealed record FraudFlaggedEvent(Guid EventId, string TenantId, Guid TransactionId,
    float Probability, RiskTier Tier, string ModelVersion, bool SyntheticDemo,
    DateTimeOffset ScoredAt, string AuditHash) : IIntegrationEvent;
public sealed record AuditLogCreatedEvent(Guid EventId, string TenantId, Guid TransactionId,
    string StreamId, long Sequence, string Hash, DateTimeOffset RecordedAt) : IIntegrationEvent;

public interface IFeatureStore
{
    Task<FeatureVector> ObserveAsync(TransactionReceivedEvent transaction, CancellationToken cancellationToken);
}
public interface IFraudScorer { RiskAssessment Score(FeatureVector features); }
public static class RiskThresholds
{
    public static RiskTier Classify(float probability, float medium, float critical)
    {
        if (!float.IsFinite(probability) || probability is < 0 or > 1
            || !float.IsFinite(medium) || !float.IsFinite(critical) || medium <= 0 || medium >= critical || critical > 1)
            throw new ValidationException("invalid_risk_thresholds");
        return probability >= critical ? RiskTier.Critical :
            probability >= medium ? RiskTier.Medium : RiskTier.Low;
    }
}
