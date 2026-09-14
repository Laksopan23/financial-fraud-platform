using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.Ingestion.Domain;

namespace FinancialFraudPlatform.Ingestion.Application;

public static class TransactionFingerprint
{
    public const int LegacyVersion = 1;
    public const int CurrentVersion = 2;

    // Contains cardholder name: pass only to the keyed fingerprint function, never to a logger.
    public static string CanonicalPayload(SubmitTransaction request, string cardToken, int version = CurrentVersion)
    {
        var money = new Money(request.Amount, request.Currency);
        return version switch
        {
            LegacyVersion => Json.Encode(new
            {
                request.TenantId, request.TransactionId, CardToken = cardToken, money.Amount, money.Currency,
                request.MerchantId, request.Latitude, request.Longitude,
                OccurredAt = request.OccurredAt.ToUniversalTime(), request.CardholderName
            }),
            CurrentVersion => Json.Encode(new
            {
                Version = CurrentVersion, request.TenantId, request.TransactionId, CardToken = cardToken,
                AmountMinor = money.MinorUnits, money.Currency, request.MerchantId,
                Latitude = request.Latitude == 0d ? 0d : request.Latitude,
                Longitude = request.Longitude == 0d ? 0d : request.Longitude,
                OccurredAt = request.OccurredAt.ToUniversalTime(), request.CardholderName
            }),
            _ => throw new InvalidOperationException("unsupported_fingerprint_version")
        };
    }
}
