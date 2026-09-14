using FinancialFraudPlatform.Core;

namespace FinancialFraudPlatform.Ingestion.Domain;

public readonly record struct TransactionId
{
    public Guid Value { get; }
    public TransactionId(Guid value)
    {
        if (value == Guid.Empty) throw new ValidationException("invalid_transaction_id");
        Value = value;
    }
}

public sealed record Money
{
    public decimal Amount { get; }
    public string Currency { get; }
    public long MinorUnits => decimal.ToInt64(Amount * Scale(Currency));
    public Money(decimal amount, string currency)
    {
        Currency = currency?.ToUpperInvariant() ?? throw new ValidationException("invalid_currency");
        int digits = MinorDigits(Currency);
        if (amount <= 0 || amount > 100_000_000m || decimal.Round(amount, digits) != amount)
            throw new ValidationException("invalid_amount");
        Amount = amount;
    }
    public static int MinorDigits(string currency) => currency switch
    {
        "USD" or "EUR" or "GBP" or "LKR" => 2,
        "JPY" => 0, "KWD" => 3,
        _ => throw new ValidationException("unsupported_currency")
    };
    public static Money FromMinor(long minor, string currency)
    {
        string code = currency?.ToUpperInvariant() ?? throw new ValidationException("invalid_currency");
        return new Money(minor / Scale(code), code);
    }
    private static decimal Scale(string currency) => MinorDigits(currency) switch
    { 0 => 1m, 2 => 100m, 3 => 1000m, _ => throw new ValidationException("unsupported_currency") };
}

public sealed class CardNumber
{
    private readonly string digits;
    public string LastFour => digits[^4..];
    public CardNumber(string value)
    {
        if (!IsValid(value.AsSpan())) throw new ValidationException("invalid_card");
        digits = value;
    }
    public static bool IsValid(ReadOnlySpan<char> value)
    {
        if (value.Length is < 13 or > 19) return false;
        int sum = 0;
        bool doubleDigit = false, anyNonzero = false;
        for (int i = value.Length - 1; i >= 0; i--)
        {
            int digit = value[i] - '0';
            if (digit is < 0 or > 9) return false;
            anyNonzero |= digit != 0;
            if (doubleDigit) { digit *= 2; if (digit > 9) digit -= 9; }
            sum += digit;
            doubleDigit = !doubleDigit;
        }
        return anyNonzero && sum % 10 == 0;
    }
    public ReadOnlySpan<char> AsSpan() => digits.AsSpan();
    public override string ToString() => $"************{LastFour}";
}

public sealed record GeoPoint
{
    public double Latitude { get; }
    public double Longitude { get; }
    public GeoPoint(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude)
            || latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new ValidationException("invalid_location");
        Latitude = latitude; Longitude = longitude;
    }
}
