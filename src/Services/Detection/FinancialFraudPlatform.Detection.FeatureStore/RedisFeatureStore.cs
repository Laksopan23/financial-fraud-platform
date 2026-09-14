using System.Globalization;
using FinancialFraudPlatform.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using StackExchange.Redis;
using System.Threading.RateLimiting;

namespace FinancialFraudPlatform.Detection.FeatureStore;

public static class FeatureMath
{
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double radians = Math.PI / 180;
        double dLat = (lat2 - lat1) * radians, dLon = (lon2 - lon1) * radians;
        double a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * radians) * Math.Cos(lat2 * radians) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 6371.0088 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
    public static float SpendingZ(double amount, double count, double mean, double m2)
    {
        if (count < 2) return 0;
        double variance = Math.Max(0, m2 / (count - 1));
        if (variance < 1e-9) return Math.Abs(amount - mean) < 1e-9 ? 0 : 20;
        return (float)Math.Min(20, Math.Abs(amount - mean) / Math.Sqrt(variance));
    }
}
public sealed class RedisFeatureStore(IConnectionMultiplexer redis, ResiliencePipeline pipeline) : IFeatureStore
{
    private static readonly string Script = LoadScript();
    private static string LoadScript()
    {
        var assembly = typeof(RedisFeatureStore).Assembly;
        string name = assembly.GetManifestResourceNames().Single(x => x.EndsWith("Scripts.features.lua", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("missing_feature_script");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public async Task<FeatureVector> ObserveAsync(TransactionReceivedEvent value, CancellationToken ct)
    {
        if (value.ReceivedAt < DateTimeOffset.UtcNow.AddHours(-6) || value.ReceivedAt > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new ValidationException("outside_live_feature_replay_horizon");
        Identity.Tenant(value.TenantId);
        Identity.Merchant(value.MerchantId);
        if (value.Currency != "USD" || decimal.Round(value.Amount, 2) != value.Amount
            || value.OccurredAt < value.ReceivedAt.AddMinutes(-5) || value.OccurredAt > value.ReceivedAt.AddSeconds(30))
            throw new ValidationException("invalid_feature_input");
        if (value.TransactionId == Guid.Empty || value.EventId == Guid.Empty || string.IsNullOrEmpty(value.CardToken) || value.CardToken.Length != 68 || !value.CardToken.StartsWith("tk1_", StringComparison.Ordinal)
            || value.CardToken[4..].Any(c => !Uri.IsHexDigit(c))) throw new ValidationException("invalid_card_token");
        if (value.Amount <= 0 || value.Amount > 100_000_000m || !float.IsFinite(value.MerchantRisk) || value.MerchantRisk is < 0 or > 1 || !double.IsFinite(value.Latitude) || !double.IsFinite(value.Longitude)
            || value.Latitude is < -90 or > 90 || value.Longitude is < -180 or > 180)
            throw new ValidationException("invalid_feature_input");
        string prefix = $"ff:{{{value.TenantId}:{value.CardToken}}}:";
        RedisKey[] keys = [prefix + "velocity", prefix + "geo", prefix + "baseline:" + value.Currency,
            prefix + "snapshot:" + value.TransactionId.ToString("N")];
        RedisValue[] args = [value.TransactionId.ToString("N"), value.Amount.ToString(CultureInfo.InvariantCulture),
            value.OccurredAt.ToUnixTimeMilliseconds(), value.Latitude.ToString("R", CultureInfo.InvariantCulture),
            value.Longitude.ToString("R", CultureInfo.InvariantCulture), Json.Digest(value)];
        RedisResult result;
        try
        {
            result = await pipeline.ExecuteAsync(async cancellation =>
                await redis.GetDatabase().ScriptEvaluateAsync(Script, keys, args).WaitAsync(cancellation), ct);
        }
        catch (RedisServerException error) when (error.Message.Contains("FF_SNAPSHOT_", StringComparison.Ordinal))
        {
            throw new ConflictException("feature_snapshot_conflict");
        }
        catch (RedisServerException error) when (error.Message.Contains("FF_FEATURE_KEY_TYPE", StringComparison.Ordinal))
        {
            throw new InvalidDataException("invalid_feature_key_type");
        }
        double[] prior = Json.Decode<double[]>(result.ToString());
        if (prior.Length != 10) throw new InvalidDataException("invalid_feature_snapshot");
        bool history = prior[5] > 0, late = prior[9] > 0;
        double distance = history && !late ? FeatureMath.DistanceKm(prior[3], prior[4], value.Latitude, value.Longitude) : 0;
        double elapsedHours = history ? Math.Max(1d / 3600, (value.OccurredAt.ToUnixTimeMilliseconds() - prior[5]) / 3_600_000) : 1;
        double deltaHours = history ? Math.Abs(value.OccurredAt.UtcDateTime.TimeOfDay.TotalHours - DateTimeOffset.FromUnixTimeMilliseconds((long)prior[5]).UtcDateTime.TimeOfDay.TotalHours) : 0;
        return new((float)value.Amount, (float)prior[0], (float)prior[1], (float)prior[2],
            (float)distance, (float)Math.Min(100_000, distance / elapsedHours),
            FeatureMath.SpendingZ((double)value.Amount, prior[6], prior[7], prior[8]), value.MerchantRisk,
            (float)Math.Min(deltaHours, 24 - deltaHours), history && prior[6] >= 2 ? 1 : 0, late ? 1 : 0);
    }
}
public static class FeatureStoreSetup
{
    public static void AddFeatureStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis") ?? throw new InvalidOperationException("missing_redis"));
            options.AbortOnConnectFail = false; options.AsyncTimeout = 2500; options.ConnectTimeout = 5000;
            return ConnectionMultiplexer.Connect(options);
        });
        services.AddSingleton(CreatePipeline());
        services.AddSingleton<IFeatureStore, RedisFeatureStore>();
        services.AddHealthChecks().AddCheck<RedisHealth>("redis");
    }
    public static ResiliencePipeline CreatePipeline() => new ResiliencePipelineBuilder()
        .AddConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 64, QueueLimit = 128 })
        .AddTimeout(TimeSpan.FromSeconds(8))
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<RedisConnectionException>().Handle<RedisTimeoutException>(),
            MaxRetryAttempts = 2, Delay = TimeSpan.FromMilliseconds(100), BackoffType = DelayBackoffType.Exponential, UseJitter = true
        })
        .AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<RedisConnectionException>().Handle<RedisTimeoutException>(),
            FailureRatio = 0.5, MinimumThroughput = 20, SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(15)
        }).Build();
}
public sealed class RedisHealth(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try { await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken); return HealthCheckResult.Healthy(); }
        catch { return HealthCheckResult.Unhealthy("redis_unavailable"); }
    }
}
