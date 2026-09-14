using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.Ingestion.Application;
using FinancialFraudPlatform.Ingestion.Domain;
using FinancialFraudPlatform.Ingestion.Grpc;
using FinancialFraudPlatform.Security;
using Grpc.Core;
using MediatR;
using Microsoft.AspNetCore.Authorization;

namespace FinancialFraudPlatform.Ingestion.API;

[Authorize(Policy = "Writer")]
public sealed class GrpcGateway(ISender sender) : TransactionGateway.TransactionGatewayBase
{
    public override async Task Stream(IAsyncStreamReader<TransactionInput> requestStream,
        IServerStreamWriter<TransactionReply> responseStream, ServerCallContext context)
    {
        string tenant = context.GetHttpContext().User.RequireTenant();
        int count = 0;
        while (await requestStream.MoveNext(context.CancellationToken))
        {
            if (++count > 500) throw new RpcException(new Status(StatusCode.ResourceExhausted, "stream_limit"));
            var input = requestStream.Current;
            string idText = "";
            var reply = new TransactionReply();
            try
            {
                if (!Guid.TryParse(input.TransactionId, out Guid id) || id == Guid.Empty)
                    throw new ValidationException("invalid_transaction_id");
                idText = id.ToString();
                var money = Money.FromMinor(input.AmountMinorUnits, input.Currency);
                await sender.Send(new SubmitTransaction
                {
                    TenantId = tenant, TransactionId = id, CardNumber = input.CardNumber,
                    CardholderName = input.CardholderName, Amount = money.Amount, Currency = money.Currency,
                    MerchantId = input.MerchantId, Latitude = input.Latitude, Longitude = input.Longitude,
                    OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds(input.OccurredAtUnixMs)
                }, context.CancellationToken);
                reply.Status = "accepted";
            }
            catch (ValidationException error) { reply.Status = "rejected"; reply.ErrorCode = error.Message; }
            catch (ConflictException error) { reply.Status = "conflict"; reply.ErrorCode = error.Message; }
            catch (ArgumentOutOfRangeException) { reply.Status = "rejected"; reply.ErrorCode = "invalid_timestamp"; }
            catch (RateLimitExceededException) { throw new RpcException(new Status(StatusCode.ResourceExhausted, "rate_limited")); }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
            catch { throw new RpcException(new Status(StatusCode.Unavailable, "temporarily_unavailable")); }
            reply.TransactionId = idText;
            await responseStream.WriteAsync(reply);
        }
    }
}
