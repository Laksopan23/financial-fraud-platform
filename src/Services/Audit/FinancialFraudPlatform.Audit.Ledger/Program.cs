using MassTransit;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.EventBus;
using FinancialFraudPlatform.Security;
using Marten;

namespace FinancialFraudPlatform.Audit.Ledger;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddShield();
        builder.AddPlatform("audit", options =>
        {
            options.Schema.For<LedgerHead>().UseOptimisticConcurrency(true);
            options.Schema.For<AuditReceipt>();
            options.Events.AddEventType<LedgerEntry>();
        }, bus => bus.AddConsumer<AuditConsumer>());
        builder.Services.AddSingleton(_ => new AuditSigner(KeyRing.Read(builder.Configuration.GetSection("Keys:Audit"))));
        builder.Services.AddSingleton<LedgerVerifier>();
        var app = builder.Build();
        _ = app.Services.GetRequiredService<AuditSigner>();
        if (builder.Environment.IsDevelopment())
        {
            await app.Services.GetRequiredService<IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await AppendOnlyGuard.InstallAsync(builder.Configuration.GetConnectionString("Postgres")!);
        }
        app.UseMiddleware<SafeRequestMiddleware>();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapHealthChecks("/health/ready");
        app.MapGet("/api/decisions/{id:guid}", async (Guid id, HttpContext context, IDocumentStore store, AuditSigner signer, CancellationToken ct) =>
        {
            await using var session = store.QuerySession();
            var receipt = await session.LoadAsync<AuditReceipt>(Identity.Scoped(context.User.RequireTenant(), id), ct);
            if (receipt is null) return Results.NotFound();
            bool valid = signer.Verify(receipt.Entry.Payload.KeyId, LedgerHash.Canonical(receipt.Entry.Payload), receipt.Entry.Hash);
            return Results.Ok(new { entry = receipt.Entry, signatureValid = valid });
        }).RequireAuthorization("Auditor");
        app.MapGet("/api/ledger/verify", async (int shard, long? after, int? limit, HttpContext context, LedgerVerifier verifier, CancellationToken ct) =>
            Results.Ok(await verifier.VerifyAsync(context.User.RequireTenant(), shard, after ?? 0, limit ?? 1000, ct))).RequireAuthorization("Auditor");
        await app.RunAsync();
    }
}
