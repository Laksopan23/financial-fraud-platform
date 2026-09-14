using Marten.Events;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using FinancialFraudPlatform.Core;
using JasperFx;
using JasperFx.Events;
using Marten;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

namespace FinancialFraudPlatform.EventBus;

public static class Telemetry
{
    public const string Name = "FinancialFraudPlatform";
    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);
    public static readonly Counter<long> Published = Meter.CreateCounter<long>("fraud.outbox.published");
    public static readonly Counter<long> Accepted = Meter.CreateCounter<long>("fraud.transactions.accepted");
    public static readonly Histogram<double> Inference = Meter.CreateHistogram<double>("fraud.inference.duration", "ms");
    public static readonly Histogram<double> Pipeline = Meter.CreateHistogram<double>("fraud.pipeline.duration", "ms");
}
public static class Infrastructure
{
    public static void AddPlatform(this WebApplicationBuilder builder, string schema,
        Action<StoreOptions> configureStore, Action<IBusRegistrationConfigurator>? consumers = null)
    {
        var connection = builder.Configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("missing_postgres_connection");
        builder.Services.AddMarten(options =>
        {
            ConfigureStore(options, connection, schema, builder.Environment.IsDevelopment());
            configureStore(options);
        }).UseLightweightSessions();
        builder.Services.AddMassTransit(registration =>
        {
            registration.SetKebabCaseEndpointNameFormatter();
            consumers?.Invoke(registration);
            registration.UsingRabbitMq((context, rabbit) =>
            {
                bool tls = builder.Configuration.GetValue<bool>("RabbitMQ:Tls");
                ushort port = builder.Configuration.GetValue<ushort?>("RabbitMQ:Port") ?? (ushort)(tls ? 5671 : 5672);
                rabbit.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", port, "/", host =>
                {
                    host.Username(builder.Configuration["RabbitMQ:Username"] ?? throw new InvalidOperationException("missing_broker_user"));
                    host.Password(builder.Configuration["RabbitMQ:Password"] ?? throw new InvalidOperationException("missing_broker_password"));
                    if (tls) host.UseSsl();
                });
                rabbit.PrefetchCount = 64;
                rabbit.ConcurrentMessageLimit = 32;
                rabbit.UseMessageRetry(retry =>
                {
                    retry.Ignore<ValidationException>();
                    retry.Ignore<ConflictException>();
                    retry.Exponential(5, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));
                });
                rabbit.ConfigureEndpoints(context);
            });
        });
        builder.Services.AddOptions<MassTransitHostOptions>().Configure(options =>
        {
            options.WaitUntilStarted = true;
            options.StartTimeout = TimeSpan.FromSeconds(60);
            options.StopTimeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHostedService<OutboxDispatcher>();
        builder.Services.AddHealthChecks().AddCheck<PostgresHealth>("postgres", tags: ["ready"]);
        builder.Services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService(schema))
            .WithTracing(tracing => tracing.AddSource(Telemetry.Name).AddOtlpExporter())
            .WithMetrics(metrics => metrics.AddMeter(Telemetry.Name)
                .AddView("fraud.inference.duration", new ExplicitBucketHistogramConfiguration { Boundaries = [.1, .5, 1, 2, 5, 10, 15, 25, 50, 100, 250] })
                .AddView("fraud.pipeline.duration", new ExplicitBucketHistogramConfiguration { Boundaries = [5, 10, 25, 50, 100, 250, 500, 1000, 5000, 30000] })
                .AddOtlpExporter());
    }
    public static void ConfigureStore(StoreOptions options, string connection, string schema, bool allowDdl)
    {
        options.Connection(connection);
        options.DatabaseSchemaName = schema;
        options.Events.DatabaseSchemaName = schema;
        options.Events.StreamIdentity = StreamIdentity.AsString;
        options.AutoCreateSchemaObjects = allowDdl ? AutoCreate.CreateOrUpdate : AutoCreate.None;
        options.Schema.For<OutboxEnvelope>().UseOptimisticConcurrency(true);
        options.Schema.For<OutboxEnvelope>().Index(x => x.NextAttemptAt);
    }
}
public sealed class PostgresHealth(IDocumentStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = store.QuerySession();
            _ = await session.Query<OutboxEnvelope>().AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch { return HealthCheckResult.Unhealthy("postgres_unavailable"); }
    }
}
