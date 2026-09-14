using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Testcontainers.RabbitMq;
using Xunit;

namespace FinancialFraudPlatform.IntegrationTests;

public sealed class InfrastructureFixture : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
    public RedisContainer Redis { get; } = new RedisBuilder().WithImage("redis:7.4-alpine").Build();
    public RabbitMqContainer Rabbit { get; } = new RabbitMqBuilder().WithImage("rabbitmq:4-management").WithUsername("fraudtest").WithPassword("integration-test-only-password").Build();
    public async Task InitializeAsync() => await Task.WhenAll(Postgres.StartAsync(), Redis.StartAsync(), Rabbit.StartAsync());
    public async Task DisposeAsync()
    {
        await Rabbit.DisposeAsync(); await Redis.DisposeAsync(); await Postgres.DisposeAsync();
    }
}
[CollectionDefinition("infrastructure", DisableParallelization = true)]
public sealed class InfrastructureCollection : ICollectionFixture<InfrastructureFixture> { }
