using System.Security.Cryptography;
using FinancialFraudPlatform.Audit.Ledger;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.Detection.FeatureStore;
using FinancialFraudPlatform.EventBus;
using FinancialFraudPlatform.Ingestion.API;
using FinancialFraudPlatform.Ingestion.Application;
using FinancialFraudPlatform.Security;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using Xunit;

namespace FinancialFraudPlatform.IntegrationTests;

[Collection("infrastructure")]
public sealed class RecoveryTests(InfrastructureFixture infrastructure)
{
    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private IDocumentStore Store(Action<StoreOptions> configure) => DocumentStore.For(options =>
    {
        Infrastructure.ConfigureStore(options, infrastructure.Postgres.GetConnectionString(), "recovery_" + Guid.NewGuid().ToString("N"), true);
        configure(options);
    });

    [Fact]
    public async Task Legacy_receipt_accepts_its_original_request_after_the_live_window_without_republishing()
    {
        using var store = Store(options => { options.Schema.For<AcceptedTransaction>(); options.Schema.For<CardVaultRecord>(); });
        using var encryption = new AesPayloadProtector(new KeyRing("test", new Dictionary<string, string> { ["test"] = Key() }));
        using var tokens = new CardTokenizer(Key());
        using var budget = new TenantBudget();
        var request = new SubmitTransaction
        {
            TenantId = "demo", TransactionId = Guid.NewGuid(), CardNumber = "4111111111111111", CardholderName = "Test Person",
            Amount = 10.00m, Currency = "USD", MerchantId = "shop", Latitude = 0, Longitude = 0, OccurredAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        var receipt = new AcceptedTransaction
        {
            Id = Identity.Scoped(request.TenantId, request.TransactionId), TransactionId = request.TransactionId,
            Fingerprint = tokens.Fingerprint(TransactionFingerprint.CanonicalPayload(request,
                tokens.Tokenize(request.TenantId, request.CardNumber.AsSpan()), TransactionFingerprint.LegacyVersion)),
            AcceptedAt = request.OccurredAt
        };
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await using (var session = store.LightweightSession()) { session.Insert(receipt); await session.SaveChangesAsync(); }
        var repository = new TransactionRepository(store, encryption, tokens, TimeProvider.System, new ConfigurationBuilder().Build(), budget);
        (await repository.AcceptAsync(request, CancellationToken.None)).Should().Be(receipt.View());
        await using var query = store.QuerySession();
        (await query.Query<OutboxEnvelope>().CountAsync()).Should().Be(0);
        (await query.Query<CardVaultRecord>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Redis_rejects_a_corrupt_key_type_before_changing_other_features()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(infrastructure.Redis.GetConnectionString());
        var store = new RedisFeatureStore(redis, FeatureStoreSetup.CreatePipeline());
        var input = Input();
        string prefix = Prefix(input);
        await redis.GetDatabase().StringSetAsync(prefix + "baseline:USD", "invalid-baseline-type");
        Func<Task> corrupt = () => store.ObserveAsync(input, CancellationToken.None);
        await corrupt.Should().ThrowAsync<InvalidDataException>();
        (await redis.GetDatabase().KeyExistsAsync(prefix + "velocity")).Should().BeFalse();
        (await redis.GetDatabase().KeyExistsAsync(prefix + "geo")).Should().BeFalse();
        (await redis.GetDatabase().KeyExistsAsync(prefix + "snapshot:" + input.TransactionId.ToString("N"))).Should().BeFalse();
        await redis.GetDatabase().KeyDeleteAsync(prefix + "baseline:USD");
        (await store.ObserveAsync(input, CancellationToken.None)).Velocity1m.Should().Be(1);
    }

    [Fact]
    public async Task Legacy_feature_snapshot_is_quarantined_without_recounting_the_transaction()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(infrastructure.Redis.GetConnectionString());
        var store = new RedisFeatureStore(redis, FeatureStoreSetup.CreatePipeline());
        var input = Input();
        string prefix = Prefix(input);
        await redis.GetDatabase().StringSetAsync(prefix + "snapshot:" + input.TransactionId.ToString("N"),
            "[1,1,1,0,0,0,0,0,0,0]", TimeSpan.FromHours(48));
        Func<Task> replay = () => store.ObserveAsync(input, CancellationToken.None);
        await replay.Should().ThrowAsync<ConflictException>();
        (await redis.GetDatabase().KeyExistsAsync(prefix + "velocity")).Should().BeFalse();
        (await redis.GetDatabase().KeyExistsAsync(prefix + "baseline:USD")).Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_event_timing_is_rejected_before_feature_mutation()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(infrastructure.Redis.GetConnectionString());
        var store = new RedisFeatureStore(redis, FeatureStoreSetup.CreatePipeline());
        var input = Input();
        Func<Task> future = () => store.ObserveAsync(input with { OccurredAt = input.ReceivedAt.AddSeconds(31) }, CancellationToken.None);
        Func<Task> stale = () => store.ObserveAsync(input with { OccurredAt = input.ReceivedAt.AddMinutes(-6) }, CancellationToken.None);
        await future.Should().ThrowAsync<ValidationException>();
        await stale.Should().ThrowAsync<ValidationException>();
        (await redis.GetDatabase().KeyExistsAsync(Prefix(input) + "velocity")).Should().BeFalse();
    }

    [Fact]
    public async Task Missing_ledger_head_is_reported_as_corruption_when_signed_events_still_exist()
    {
        using var store = Store(options =>
        {
            options.Schema.For<LedgerHead>().UseOptimisticConcurrency(true);
            options.Schema.For<AuditReceipt>(); options.Events.AddEventType<LedgerEntry>();
        });
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        using var signer = new AuditSigner(new KeyRing("test", new Dictionary<string, string> { ["test"] = Key() }));
        var verifier = new LedgerVerifier(store, signer);
        var decision = new RiskScoredEvent(Guid.NewGuid(), "demo", Guid.NewGuid(),
            new(100, 1, 1, 1, 0, 0, 0, .2f, 0, 0, 0), new(.2f, RiskTier.Low, "test", true, 1), DateTimeOffset.UtcNow, "digest");
        int shard = LedgerHash.Shard(decision.TransactionId);
        (await verifier.VerifyAsync("demo", shard, 0, 1000, CancellationToken.None)).Valid.Should().BeTrue();
        await new AuditConsumer(store, signer).ProcessAsync(decision, CancellationToken.None);
        (await verifier.VerifyAsync("demo", shard, 0, 1000, CancellationToken.None)).Valid.Should().BeTrue();
        await using (var session = store.LightweightSession())
        {
            session.Delete<LedgerHead>(LedgerHash.Stream("demo", shard));
            await session.SaveChangesAsync();
        }
        (await verifier.VerifyAsync("demo", shard, 0, 1000, CancellationToken.None)).Valid.Should().BeFalse();
    }

    private static string Prefix(TransactionReceivedEvent input) => $"ff:{{{input.TenantId}:{input.CardToken}}}:";
    private static TransactionReceivedEvent Input()
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), "test" + Guid.NewGuid().ToString("N"), Guid.NewGuid(), "tk1_" + new string('A', 64),
            100, "USD", "shop", .2f, 0, 0, now, now);
    }
}
