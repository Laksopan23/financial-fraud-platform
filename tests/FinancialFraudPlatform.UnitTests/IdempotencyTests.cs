using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.Ingestion.Application;
using FinancialFraudPlatform.Ingestion.Domain;
using FinancialFraudPlatform.Security;
using FluentAssertions;
using System.Security.Claims;
using Xunit;

namespace FinancialFraudPlatform.UnitTests;

public sealed class IdempotencyTests
{
    private const string Token = "tk1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly DateTimeOffset Time = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Equivalent_decimal_scales_time_offsets_and_signed_zero_have_one_fingerprint()
    {
        string initial = TransactionFingerprint.CanonicalPayload(Command(10m), Token);
        string retry = TransactionFingerprint.CanonicalPayload(Command(10.00m, "usd", Time.ToOffset(TimeSpan.FromHours(5.5)), -0d), Token);
        retry.Should().Be(initial);
    }

    [Fact]
    public void Different_payloads_do_not_share_a_fingerprint()
    {
        string initial = TransactionFingerprint.CanonicalPayload(Command(10m), Token);
        TransactionFingerprint.CanonicalPayload(Command(10.01m), Token).Should().NotBe(initial);
        TransactionFingerprint.CanonicalPayload(Command(10m, name: "Different Name"), Token).Should().NotBe(initial);
        TransactionFingerprint.CanonicalPayload(Command(10m, time: Time.AddTicks(1)), Token).Should().NotBe(initial);
        TransactionFingerprint.CanonicalPayload(Command(10m), Token.Replace('A', 'B')).Should().NotBe(initial);
    }

    [Fact]
    public void Legacy_fingerprint_preserves_the_preceding_wire_shape()
    {
        var request = Command(10.00m);
        var money = new Money(request.Amount, request.Currency);
        string original = Json.Encode(new
        {
            request.TenantId, request.TransactionId, CardToken = Token, money.Amount, money.Currency,
            request.MerchantId, request.Latitude, request.Longitude,
            OccurredAt = request.OccurredAt.ToUniversalTime(), request.CardholderName
        });
        TransactionFingerprint.CanonicalPayload(request, Token, TransactionFingerprint.LegacyVersion).Should().Be(original);
        Action unknown = () => TransactionFingerprint.CanonicalPayload(request, Token, 999);
        unknown.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("demo\n")]
    [InlineData("demo\r\n")]
    [InlineData("demo\0")]
    [InlineData("demo shop")]
    [InlineData("demo:other")]
    [InlineData("")]
    public void Tenant_and_merchant_identifiers_reject_extra_characters(string input)
    {
        Action tenant = () => Identity.Tenant(input);
        Action merchant = () => Identity.Merchant(input);
        tenant.Should().Throw<ValidationException>();
        merchant.Should().Throw<ValidationException>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant", input)], "test"));
        TenantAccess.HasTenant(principal).Should().BeFalse();
    }

    [Fact]
    public void Valid_identifier_length_boundaries_are_preserved()
    {
        Identity.Tenant(new string('a', 48)).Should().HaveLength(48);
        Identity.Merchant(new string('A', 64)).Should().HaveLength(64);
        Action tooLong = () => Identity.Tenant(new string('a', 49));
        tooLong.Should().Throw<ValidationException>();
        Action emptyId = () => Identity.Scoped("demo", Guid.Empty);
        emptyId.Should().Throw<ValidationException>();
    }

    [Theory]
    [InlineData(12345, "USD")]
    [InlineData(12345, "JPY")]
    [InlineData(12345, "KWD")]
    [InlineData(10000000000, "USD")]
    public void Money_minor_units_round_trip_without_rounding(long units, string currency) =>
        Money.FromMinor(units, currency).MinorUnits.Should().Be(units);

    private static SubmitTransaction Command(decimal amount, string currency = "USD", DateTimeOffset? time = null,
        double latitude = 0, string name = "Test Person") => new()
    {
        TenantId = "demo", TransactionId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
        CardNumber = "4111111111111111", CardholderName = name, Amount = amount, Currency = currency,
        MerchantId = "shop", Latitude = latitude, Longitude = 0, OccurredAt = time ?? Time
    };
}
