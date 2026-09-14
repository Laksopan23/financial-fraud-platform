using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.EventBus;
using FinancialFraudPlatform.Ingestion.Application;
using FinancialFraudPlatform.Ingestion.Domain;
using FinancialFraudPlatform.Security;
using Marten;

namespace FinancialFraudPlatform.Ingestion.API;

public sealed class AcceptedTransaction
{
    public string Id { get; set; } = "";
    public Guid TransactionId { get; set; }
    public string Fingerprint { get; set; } = "";
    public int FingerprintVersion { get; set; } = TransactionFingerprint.LegacyVersion;
    public DateTimeOffset AcceptedAt { get; set; }
    public TransactionReceiptView View() => new(TransactionId, AcceptedAt, "accepted");
}
public sealed class CardVaultRecord
{
    public string Id { get; set; } = "";
    public EncryptedPayload Payload { get; set; } = new("", "", "", "");
    public DateTimeOffset ExpiresAt { get; set; }
}
public sealed class TenantBudget : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter = PartitionedRateLimiter.Create<string, string>(
        tenant => RateLimitPartition.GetFixedWindowLimiter(tenant, _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 200, Window = TimeSpan.FromSeconds(1), QueueLimit = 0, AutoReplenishment = true }));
    public bool Accept(string tenant) { using var lease = limiter.AttemptAcquire(tenant); return lease.IsAcquired; }
    public void Dispose() => limiter.Dispose();
}
public sealed class TransactionRepository(IDocumentStore store, AesPayloadProtector protector,
    CardTokenizer tokenizer, TimeProvider clock, IConfiguration configuration, TenantBudget budget) : ITransactionRepository
{
    public async Task<TransactionReceiptView> AcceptAsync(SubmitTransaction request, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("ingestion.accept", ActivityKind.Server);
        string key = Identity.Scoped(request.TenantId, request.TransactionId);
        var money = new Money(request.Amount, request.Currency);
        if (money.Currency != "USD") throw new ValidationException("unsupported_scoring_currency");
        var card = new CardNumber(request.CardNumber);
        string token = tokenizer.Tokenize(request.TenantId, card.AsSpan());
        string fingerprint = tokenizer.Fingerprint(TransactionFingerprint.CanonicalPayload(request, token));
        await using (var existingSession = store.QuerySession())
        {
            var existing = await existingSession.LoadAsync<AcceptedTransaction>(key, ct);
            if (existing is not null) return Compare(existing, request, token, fingerprint);
        }
        if (!budget.Accept(request.TenantId)) throw new RateLimitExceededException();
        var now = clock.GetUtcNow();
        var transaction = Transaction.Accept(request.TransactionId, request.TenantId, money,
            request.MerchantId, new(request.Latitude, request.Longitude), request.OccurredAt, now);
        string riskText = configuration[$"Merchants:{transaction.MerchantId}:Risk"]
            ?? throw new ValidationException("unknown_merchant");
        if (!float.TryParse(riskText, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float risk) || !float.IsFinite(risk) || risk is < 0 or > 1)
            throw new InvalidOperationException("invalid_merchant_catalog");
        byte[] privateBytes = Encoding.UTF8.GetBytes(Json.Encode(new { pan = request.CardNumber, name = request.CardholderName }));
        EncryptedPayload encrypted;
        try { encrypted = protector.Encrypt(privateBytes, "vault:v1:" + key); }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
        var receipt = new AcceptedTransaction { Id = key, TransactionId = transaction.Id.Value, Fingerprint = fingerprint,
            FingerprintVersion = TransactionFingerprint.CurrentVersion, AcceptedAt = now };
        try
        {
            await using var session = store.LightweightSession();
            session.Insert(receipt);
            session.Insert(new CardVaultRecord { Id = key, Payload = encrypted, ExpiresAt = now.AddHours(24) });
            var accepted = transaction.DomainEvents.OfType<TransactionAccepted>().Single();
            Outbox.Enqueue(session, new TransactionReceivedEvent(Guid.NewGuid(), transaction.TenantId,
                transaction.Id.Value, token, money.Amount, money.Currency, transaction.MerchantId, risk,
                transaction.Location.Latitude, transaction.Location.Longitude, transaction.OccurredAt, accepted.OccurredAt));
            await session.SaveChangesAsync(ct);
            transaction.ClearEvents();
            Telemetry.Accepted.Add(1);
            return receipt.View();
        }
        catch (Exception error) when (DatabaseErrors.IsUniqueViolation(error))
        {
            await using var retry = store.QuerySession();
            var existing = await retry.LoadAsync<AcceptedTransaction>(key, ct);
            if (existing is null) throw;
            return Compare(existing, request, token, fingerprint);
        }
    }
    private TransactionReceiptView Compare(AcceptedTransaction receipt, SubmitTransaction request, string token, string currentFingerprint)
    {
        string expected = receipt.FingerprintVersion == TransactionFingerprint.CurrentVersion
            ? currentFingerprint
            : tokenizer.Fingerprint(TransactionFingerprint.CanonicalPayload(request, token, receipt.FingerprintVersion));
        return receipt.Fingerprint == expected ? receipt.View() : throw new ConflictException("idempotency_key_reused");
    }
    public async Task<TransactionReceiptView?> GetAsync(string tenant, Guid id, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        return (await session.LoadAsync<AcceptedTransaction>(Identity.Scoped(tenant, id), ct))?.View();
    }
}
public sealed class VaultRetentionWorker(IDocumentStore store, ILogger<VaultRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var session = store.LightweightSession();
                var now = DateTimeOffset.UtcNow;
                session.DeleteWhere<CardVaultRecord>(x => x.ExpiresAt < now);
                await session.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogError(new EventId(1201), error, "vault_retention_failed"); }
        }
    }
}
