using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FinancialFraudPlatform.Core;

public sealed class ValidationException(string code) : Exception(code) { }
public sealed class RateLimitExceededException() : Exception("rate_limited") { }
public sealed class ConflictException(string code) : Exception(code) { }

public static partial class Identity
{
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_-]{0,47}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TenantPattern();
    [GeneratedRegex(@"\A[a-zA-Z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MerchantPattern();
    public static string Tenant(string value) => value is not null && TenantPattern().IsMatch(value)
        ? value : throw new ValidationException("invalid_tenant");
    public static string Merchant(string value) => value is not null && MerchantPattern().IsMatch(value)
        ? value : throw new ValidationException("invalid_merchant");
    public static string Scoped(string tenant, Guid id)
    {
        if (id == Guid.Empty) throw new ValidationException("invalid_transaction_id");
        return $"{Tenant(tenant)}:{id:N}";
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Decode<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("empty_json");
    public static string Digest<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Encode(value))));
}

public interface IDomainEvent { DateTimeOffset OccurredAt { get; } }
public abstract class AggregateRoot
{
    private readonly List<IDomainEvent> events = [];
    public IReadOnlyList<IDomainEvent> DomainEvents => events.AsReadOnly();
    protected void Raise(IDomainEvent value) => events.Add(value);
    public void ClearEvents() => events.Clear();
}
