using MassTransit;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.Detection.FeatureStore;
using FinancialFraudPlatform.Detection.ML;
using FinancialFraudPlatform.EventBus;
using FinancialFraudPlatform.Security;

namespace FinancialFraudPlatform.Detection.Worker;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders(); builder.Logging.AddProvider(new SafeLogProvider());
        builder.AddPlatform("detection", options => options.Schema.For<DetectionDecision>(),
            bus => bus.AddConsumer<DetectionConsumer>());
        builder.Services.AddFeatureStore(builder.Configuration);
        builder.Services.AddSingleton<IFraudScorer>(_ =>
        {
            bool allowSynthetic = builder.Configuration.GetValue<bool>("Model:AllowSynthetic");
            string? expectedManifest = builder.Configuration["Model:ExpectedManifestSha256"];
            if (!builder.Environment.IsDevelopment() && (allowSynthetic || string.IsNullOrWhiteSpace(expectedManifest)))
                throw new InvalidOperationException("production_requires_approved_manifest");
            return new OnnxFraudScorer(builder.Configuration["Model:Path"] ?? "/models/model.onnx", allowSynthetic, expectedManifest);
        });
        var app = builder.Build();
        _ = app.Services.GetRequiredService<IFraudScorer>();
        app.UseMiddleware<SafeRequestMiddleware>();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapHealthChecks("/health/ready");
        await app.RunAsync();
    }
}
