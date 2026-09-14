using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.EventBus;
using FinancialFraudPlatform.Ingestion.Application;
using FinancialFraudPlatform.Security;
using MediatR;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace FinancialFraudPlatform.Ingestion.API;

public sealed class TransactionInputDto
{
    public Guid TransactionId { get; set; }
    public string CardNumber { get; set; } = "";
    public string CardholderName { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string MerchantId { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public SubmitTransaction Command(string tenant) => new()
    {
        TenantId = tenant, TransactionId = TransactionId, CardNumber = CardNumber,
        CardholderName = CardholderName, Amount = Amount, Currency = Currency,
        MerchantId = MerchantId, Latitude = Latitude, Longitude = Longitude, OccurredAt = OccurredAt
    };
    public override string ToString() => "TransactionInputDto [REDACTED]";
}
public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddShield();
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 131072);
        builder.Services.AddGrpc(options => { options.MaxReceiveMessageSize = 4096; options.EnableDetailedErrors = false; });
        builder.Services.AddMediatR(options => options.RegisterServicesFromAssemblyContaining<SubmitTransactionHandler>());
        builder.Services.AddScoped<ITransactionRepository, TransactionRepository>();
        builder.Services.AddSingleton<TenantBudget>();
        builder.Services.AddSingleton(_ => new AesPayloadProtector(KeyRing.Read(builder.Configuration.GetSection("Keys:Encryption"))));
        builder.Services.AddSingleton(_ => new CardTokenizer(builder.Configuration["Keys:Tokenization"] ?? throw new InvalidOperationException("missing_tokenization_key")));
        builder.AddPlatform("ingestion", options =>
        {
            options.Schema.For<AcceptedTransaction>();
            options.Schema.For<CardVaultRecord>().Index(x => x.ExpiresAt);
        });
        builder.Services.AddHostedService<VaultRetentionWorker>();
        var app = builder.Build();
        _ = app.Services.GetRequiredService<AesPayloadProtector>();
        _ = app.Services.GetRequiredService<CardTokenizer>();
        app.UseMiddleware<SafeRequestMiddleware>();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapHealthChecks("/health/ready");
        app.MapPost("/api/transactions", async (TransactionInputDto input, HttpContext context, ISender sender, CancellationToken ct) =>
        {
            var receipt = await sender.Send(input.Command(context.User.RequireTenant()), ct);
            return Results.Accepted($"/api/transactions/{receipt.TransactionId}", receipt);
        }).RequireAuthorization("Writer");
        app.MapGet("/api/transactions/{id:guid}", async (Guid id, HttpContext context, ISender sender, CancellationToken ct) =>
        {
            var receipt = await sender.Send(new GetTransaction(context.User.RequireTenant(), id), ct);
            return receipt is null ? Results.NotFound() : Results.Ok(receipt);
        }).RequireAuthorization("Writer");
        app.MapPost("/api/transactions/binary", ReadBinaryAsync).RequireAuthorization("Writer");
        app.MapGrpcService<GrpcGateway>();
        await app.RunAsync();
    }
    private static async Task<IResult> ReadBinaryAsync(HttpContext context, ISender sender, CancellationToken ct)
    {
        if (context.Request.ContentType != "application/octet-stream") return Results.StatusCode(415);
        string tenant = context.User.RequireTenant();
        var pipe = context.Request.BodyReader;
        int accepted = 0;
        while (true)
        {
            var result = await pipe.ReadAsync(ct);
            var remaining = result.Buffer;
            try
            {
                while (BinaryTransactionParser.TryRead(ref remaining, out var input))
                {
                    if (++accepted > 500) throw new ValidationException("batch_limit");
                    var item = input!;
                    await sender.Send(new SubmitTransaction
                    {
                        TenantId = tenant, TransactionId = item.Id, CardNumber = item.Pan,
                        Amount = item.Value.Amount, Currency = item.Value.Currency, MerchantId = item.MerchantId,
                        Latitude = item.Latitude, Longitude = item.Longitude, OccurredAt = item.OccurredAt
                    }, ct);
                }
                if (result.IsCompleted)
                {
                    if (!remaining.IsEmpty) throw new ValidationException("truncated_frame");
                    break;
                }
            }
            finally { pipe.AdvanceTo(remaining.Start, remaining.End); }
        }
        return Results.Accepted(value: new { accepted });
    }
}
