using System.Security.Cryptography;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.EventBus;
using FinancialFraudPlatform.Ingestion.API;
using FinancialFraudPlatform.Ingestion.Application;
using FinancialFraudPlatform.Security;
using FinancialFraudPlatform.Audit.Ledger;
using FinancialFraudPlatform.Detection.FeatureStore;
using FinancialFraudPlatform.Detection.Worker;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MassTransit;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace FinancialFraudPlatform.IntegrationTests;

[Collection("infrastructure")]
public sealed class PersistenceTests(InfrastructureFixture infrastructure)
{
    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private IDocumentStore Store(string schema, Action<StoreOptions>? extra = null) => DocumentStore.For(options =>
    {
        Infrastructure.ConfigureStore(options, infrastructure.Postgres.GetConnectionString(), schema, true);
        extra?.Invoke(options);
    });
    [Fact]
    public async Task Concurrent_ingestion_is_idempotent_and_encrypted_at_rest()
    {
        using var store = Store("ingest_" + Guid.NewGuid().ToString("N"), options => { options.Schema.For<AcceptedTransaction>(); options.Schema.For<CardVaultRecord>(); });
        using var encryption = new AesPayloadProtector(new KeyRing("test", new Dictionary<string,string> { ["test"] = Key() }));
        using var tokens = new CardTokenizer(Key()); using var budget = new TenantBudget();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Merchants:shop:Risk"] = "0.2" }).Build();
        var repository = new TransactionRepository(store, encryption, tokens, TimeProvider.System, configuration, budget);
        var command = Command();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => repository.AcceptAsync(Command(command.TransactionId, command.OccurredAt, i % 2 == 0 ? 10m : 10.00m), CancellationToken.None)));
        results.Select(x => x.AcceptedAt).Distinct().Should().ContainSingle();
        await using var session = store.QuerySession();
        (await session.Query<OutboxEnvelope>().CountAsync()).Should().Be(1);
        (await session.Query<AcceptedTransaction>().CountAsync()).Should().Be(1);
        var vault = await session.LoadAsync<CardVaultRecord>(Identity.Scoped(command.TenantId, command.TransactionId));
        Json.Encode(vault).Should().NotContain(command.CardNumber).And.NotContain(command.CardholderName);
        var persisted = await session.LoadAsync<AcceptedTransaction>(Identity.Scoped(command.TenantId, command.TransactionId));
        persisted!.FingerprintVersion.Should().Be(TransactionFingerprint.CurrentVersion);
        var outgoing = (await session.Query<OutboxEnvelope>().ToListAsync()).Single();
        outgoing.Payload.Should().NotContain(command.CardNumber).And.NotContain(command.CardholderName);
        (await repository.GetAsync("other", command.TransactionId, CancellationToken.None)).Should().BeNull();
        var changed = Command(command.TransactionId, command.OccurredAt, 99);
        Func<Task> conflict = () => repository.AcceptAsync(changed, CancellationToken.None);
        await conflict.Should().ThrowAsync<ConflictException>();
    }
    [Fact]
    public async Task Redis_atomic_snapshot_prevents_duplicate_velocity_and_preserves_latest_geolocation()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(infrastructure.Redis.GetConnectionString());
        var store = new RedisFeatureStore(redis, FeatureStoreSetup.CreatePipeline());
        var first = Input(Guid.NewGuid());
        var vectors = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => store.ObserveAsync(first, CancellationToken.None)));
        vectors.Should().OnlyContain(v => v.Velocity1m == 1);
        vectors.Distinct().Should().ContainSingle();
        Func<Task> changedPayload = () => store.ObserveAsync(first with { Amount = 101 }, CancellationToken.None);
        await changedPayload.Should().ThrowAsync<ConflictException>();
        var next = first with { EventId = Guid.NewGuid(), TransactionId = Guid.NewGuid(), OccurredAt = first.OccurredAt.AddSeconds(1), Longitude = 10 };
        var second = await store.ObserveAsync(next, CancellationToken.None);
        second.Velocity1m.Should().Be(2); second.DistanceKm.Should().BeGreaterThan(1000);
        var late = first with { EventId = Guid.NewGuid(), TransactionId = Guid.NewGuid(), OccurredAt = first.OccurredAt.AddSeconds(-1), Longitude = -30 };
        (await store.ObserveAsync(late, CancellationToken.None)).IsOutOfOrder.Should().Be(1);
        var newest = next with { EventId = Guid.NewGuid(), TransactionId = Guid.NewGuid(), OccurredAt = next.OccurredAt.AddSeconds(1) };
        (await store.ObserveAsync(newest, CancellationToken.None)).DistanceKm.Should().Be(0);
        var otherTenant = first with { TenantId = "other" };
        (await store.ObserveAsync(otherTenant, CancellationToken.None)).Velocity1m.Should().Be(1);
    }
    [Fact]
    public async Task Audit_commit_is_deduplicated_signed_chained_and_rejects_event_mutation()
    {
        using var store = Store("audit", options =>
        {
            options.Schema.For<LedgerHead>().UseOptimisticConcurrency(true);
            options.Schema.For<AuditReceipt>(); options.Events.AddEventType<LedgerEntry>();
        });
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await AppendOnlyGuard.InstallAsync(infrastructure.Postgres.GetConnectionString());
        using var signer = new AuditSigner(new KeyRing("test", new Dictionary<string,string> { ["test"] = Key() }));
        var consumer = new AuditConsumer(store, signer);
        var decision = Decision();
        await consumer.ProcessAsync(decision, CancellationToken.None);
        await consumer.ProcessAsync(decision, CancellationToken.None);
        var verifier = new LedgerVerifier(store, signer);
        var result = await verifier.VerifyAsync(decision.TenantId, LedgerHash.Shard(decision.TransactionId), 0, 1000, CancellationToken.None);
        result.Valid.Should().BeTrue(); result.CheckedThrough.Should().Be(1);
        await using var query = store.QuerySession();
        (await query.Query<AuditReceipt>().CountAsync()).Should().Be(1);
        (await query.Query<OutboxEnvelope>().CountAsync()).Should().Be(2);
        var receipt = await query.LoadAsync<AuditReceipt>(Identity.Scoped(decision.TenantId, decision.TransactionId));
        var altered = receipt!.Entry.Payload with { Sequence = 88 };
        signer.Verify(altered.KeyId, LedgerHash.Canonical(altered), receipt.Entry.Hash).Should().BeFalse();
        await using var connection = new NpgsqlConnection(infrastructure.Postgres.GetConnectionString()); await connection.OpenAsync();
        await using var change = new NpgsqlCommand("UPDATE audit.mt_events SET data = '{}'::jsonb", connection);
        Func<Task> mutation = async () => { await change.ExecuteNonQueryAsync(); };
        await mutation.Should().ThrowAsync<PostgresException>();
    }
    [Fact]
    public async Task Outbox_survives_dispatcher_restart_and_publishes_stable_message_id()
    {
        var received = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bus = Bus.Factory.CreateUsingRabbitMq(config =>
        {
            config.Host(new Uri(infrastructure.Rabbit.GetConnectionString()), host =>
            { host.Username("fraudtest"); host.Password("integration-test-only-password"); });
            config.ReceiveEndpoint("test-" + Guid.NewGuid().ToString("N"), endpoint => endpoint.Handler<TransactionReceivedEvent>(context =>
            { received.TrySetResult(context.MessageId); return Task.CompletedTask; }));
        });
        await bus.StartAsync();
        try
        {
            using var store = Store("outbox_" + Guid.NewGuid().ToString("N"));
            var message = Input(Guid.NewGuid());
            await using (var session = store.LightweightSession())
            {
                var envelope = Outbox.Enqueue(session, message);
                envelope.LeaseId = Guid.NewGuid();
                envelope.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
                await session.SaveChangesAsync();
            }
            // A new dispatcher instance reads the committed work; no process-local publication state is required.
            var restarted = new OutboxDispatcher(store, bus, NullLogger<OutboxDispatcher>.Instance);
            (await restarted.DispatchOnceAsync(CancellationToken.None)).Should().Be(1);
            (await received.Task.WaitAsync(TimeSpan.FromSeconds(15))).Should().Be(message.EventId);
            (await restarted.DispatchOnceAsync(CancellationToken.None)).Should().Be(0);
        }
        finally { await bus.StopAsync(); }
    }
    [Fact]
    public async Task Detection_restarts_reuse_the_persisted_decision_without_rescoring()
    {
        using var store = Store("detect_" + Guid.NewGuid().ToString("N"), options => options.Schema.For<DetectionDecision>());
        using var redis = await ConnectionMultiplexer.ConnectAsync(infrastructure.Redis.GetConnectionString());
        var features = new RedisFeatureStore(redis, FeatureStoreSetup.CreatePipeline());
        var scorer = new CountingScorer();
        var consumer = new DetectionConsumer(store, features, scorer);
        var input = Input(Guid.NewGuid());
        await consumer.ProcessAsync(input, CancellationToken.None);
        var afterRestart = new CountingScorer();
        await new DetectionConsumer(store, features, afterRestart).ProcessAsync(input, CancellationToken.None);
        scorer.Calls.Should().Be(1); afterRestart.Calls.Should().Be(0);
        await using var query = store.QuerySession();
        (await query.Query<DetectionDecision>().CountAsync()).Should().Be(1);
        (await query.Query<OutboxEnvelope>().CountAsync()).Should().Be(1);
    }
    private sealed class CountingScorer : IFraudScorer
    {
        public int Calls { get; private set; }
        public RiskAssessment Score(FeatureVector features)
        { Calls++; return new(.8f, RiskTier.Medium, "test-model", true, 1); }
    }
    private static SubmitTransaction Command(Guid? id = null, DateTimeOffset? time = null, decimal amount = 10) => new()
    {
        TenantId = "demo", TransactionId = id ?? Guid.NewGuid(), CardNumber = "4111111111111111", CardholderName = "Test Person",
        Amount = amount, Currency = "USD", MerchantId = "shop", Latitude = 0, Longitude = 0, OccurredAt = time ?? DateTimeOffset.UtcNow
    };
    private static TransactionReceivedEvent Input(Guid id) => new(Guid.NewGuid(), "tenant" + Guid.NewGuid().ToString("N")[..8], id,
        "tk1_" + new string('A', 64), 100, "USD", "shop", .2f, 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static RiskScoredEvent Decision() => new(Guid.NewGuid(), "audit-test", Guid.NewGuid(),
        new(100, 10, 20, 50, 1000, 2000, 5, .8f, 8, 1, 0), new(.95f, RiskTier.Critical, "model-test", true, 1), DateTimeOffset.UtcNow, "input-digest");
}
