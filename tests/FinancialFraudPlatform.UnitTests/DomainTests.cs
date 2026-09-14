using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.Ingestion.Application;
using FinancialFraudPlatform.Ingestion.Domain;
using FinancialFraudPlatform.Detection.FeatureStore;
using FluentAssertions;
using Xunit;

namespace FinancialFraudPlatform.UnitTests;

public sealed class DomainTests
{
    [Theory]
    [InlineData("4111111111111111", true)]
    [InlineData("5555555555554444", true)]
    [InlineData("4111111111111112", false)]
    [InlineData("0000000000000000", false)]
    [InlineData("4111 1111 1111 1111", false)]
    [InlineData("４１１１１１１１１１１１１１１１", false)]
    public void Luhn_enforces_ascii_length_and_check_digit(string pan, bool expected) =>
        CardNumber.IsValid(pan.AsSpan()).Should().Be(expected);
    [Fact]
    public void Card_string_representation_masks_pan() =>
        new CardNumber("4111111111111111").ToString().Should().Be("************1111");
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1.001)] [InlineData(100000001)]
    public void Money_rejects_invalid_amounts(double amount)
    { Action action = () => new Money((decimal)amount, "USD"); action.Should().Throw<ValidationException>(); }
    [Fact]
    public void Currency_minor_units_are_preserved()
    {
        Money.FromMinor(12345, "usd").Amount.Should().Be(123.45m);
        Money.FromMinor(12345, "JPY").Amount.Should().Be(12345m);
        Money.FromMinor(12345, "KWD").Amount.Should().Be(12.345m);
    }
    [Fact]
    public void Aggregate_enforces_live_timestamp_and_emits_event()
    {
        var now = DateTimeOffset.UtcNow;
        var tx = Transaction.Accept(Guid.NewGuid(), "tenant-one", new Money(10, "USD"), "shop", new(0, 0), now, now);
        tx.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<TransactionAccepted>();
        Action stale = () => Transaction.Accept(Guid.NewGuid(), "tenant-one", new Money(10, "USD"), "shop", new(0, 0), now.AddMinutes(-6), now);
        stale.Should().Throw<ValidationException>();
    }
    [Theory]
    [InlineData(0.49, RiskTier.Low)] [InlineData(0.5, RiskTier.Medium)] [InlineData(0.85, RiskTier.Critical)]
    public void Risk_boundaries_are_inclusive(float probability, RiskTier tier) =>
        RiskThresholds.Classify(probability, .5f, .85f).Should().Be(tier);
    [Fact]
    public void Nonfinite_model_outputs_are_withheld()
    {
        Action action = () => RiskThresholds.Classify(float.NaN, .5f, .85f);
        action.Should().Throw<ValidationException>();
        Action threshold = () => RiskThresholds.Classify(.4f, float.NaN, .85f);
        threshold.Should().Throw<ValidationException>();
    }
    [Fact]
    public void Haversine_handles_antipodes_and_identical_locations()
    {
        FeatureMath.DistanceKm(0, 0, 0, 0).Should().Be(0);
        FeatureMath.DistanceKm(0, 0, 0, 180).Should().BeApproximately(20015.1, 1);
        FeatureMath.SpendingZ(100, 0, 0, 0).Should().Be(0);
        FeatureMath.SpendingZ(110, 10, 100, 0).Should().Be(20);
    }
    [Fact]
    public void Binary_parser_retains_incomplete_data_and_parses_segmented_frame()
    {
        byte[] frame = Frame();
        var partial = new ReadOnlySequence<byte>(frame.AsMemory(0, frame.Length - 1));
        BinaryTransactionParser.TryRead(ref partial, out _).Should().BeFalse();
        partial.Length.Should().Be(frame.Length - 1);
        var first = new Segment(frame.AsMemory(0, 7)); var last = first.Append(frame.AsMemory(7));
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        BinaryTransactionParser.TryRead(ref sequence, out var transaction).Should().BeTrue();
        transaction!.Value.Amount.Should().Be(123.45m); transaction.MerchantId.Should().Be("shop");
        sequence.IsEmpty.Should().BeTrue();
    }
    [Fact]
    public void Binary_parser_rejects_oversize_frames_before_buffering()
    {
        byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, 1_000_000);
        Action action = () => { var sequence = new ReadOnlySequence<byte>(bytes); BinaryTransactionParser.TryRead(ref sequence, out _); };
        action.Should().Throw<ValidationException>();
    }
    public static byte[] Frame()
    {
        using var body = new MemoryStream();
        body.WriteByte(1); body.Write(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef").ToByteArray(bigEndian: true));
        Span<byte> numbers = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(numbers, 12345); body.Write(numbers);
        body.Write(Encoding.ASCII.GetBytes("USD"));
        BinaryPrimitives.WriteInt64BigEndian(numbers, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); body.Write(numbers);
        body.Write(new byte[8]); body.WriteByte(4); body.Write(Encoding.ASCII.GetBytes("shop"));
        body.WriteByte(16); body.Write(Encoding.ASCII.GetBytes("4111111111111111"));
        var result = new byte[body.Length + 4]; BinaryPrimitives.WriteInt32BigEndian(result, (int)body.Length);
        body.ToArray().CopyTo(result, 4); return result;
    }
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next; return next;
        }
    }
}
