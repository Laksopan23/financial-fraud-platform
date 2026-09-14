using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.Ingestion.Domain;
using MediatR;

namespace FinancialFraudPlatform.Ingestion.Application;

public sealed class SubmitTransaction : IRequest<TransactionReceiptView>
{
    public required string TenantId { get; init; }
    public required Guid TransactionId { get; init; }
    public required string CardNumber { get; init; }
    public string CardholderName { get; init; } = "";
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required string MerchantId { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public override string ToString() => "SubmitTransaction [REDACTED]";
}
public sealed record TransactionReceiptView(Guid TransactionId, DateTimeOffset AcceptedAt, string Status);
public sealed record GetTransaction(string TenantId, Guid TransactionId) : IRequest<TransactionReceiptView?>;
public interface ITransactionRepository
{
    Task<TransactionReceiptView> AcceptAsync(SubmitTransaction request, CancellationToken cancellationToken);
    Task<TransactionReceiptView?> GetAsync(string tenant, Guid id, CancellationToken cancellationToken);
}
public sealed class SubmitTransactionHandler(ITransactionRepository repository)
    : IRequestHandler<SubmitTransaction, TransactionReceiptView>
{
    public Task<TransactionReceiptView> Handle(SubmitTransaction request, CancellationToken cancellationToken)
    {
        _ = new TransactionId(request.TransactionId);
        _ = Identity.Tenant(request.TenantId);
        _ = Identity.Merchant(request.MerchantId);
        _ = new Money(request.Amount, request.Currency);
        _ = new CardNumber(request.CardNumber);
        _ = new GeoPoint(request.Latitude, request.Longitude);
        if (request.CardholderName is null || request.CardholderName.Length > 120) throw new ValidationException("invalid_name_length");
        return repository.AcceptAsync(request, cancellationToken);
    }
}
public sealed class GetTransactionHandler(ITransactionRepository repository)
    : IRequestHandler<GetTransaction, TransactionReceiptView?>
{
    public Task<TransactionReceiptView?> Handle(GetTransaction request, CancellationToken cancellationToken)
        => repository.GetAsync(Identity.Tenant(request.TenantId), request.TransactionId, cancellationToken);
}
