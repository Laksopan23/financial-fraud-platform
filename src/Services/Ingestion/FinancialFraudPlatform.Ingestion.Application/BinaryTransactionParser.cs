using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using FinancialFraudPlatform.Core;
using FinancialFraudPlatform.Ingestion.Domain;

namespace FinancialFraudPlatform.Ingestion.Application;

public sealed record BinaryTransaction(Guid Id, Money Value, string MerchantId, string Pan,
    double Latitude, double Longitude, DateTimeOffset OccurredAt)
{
    public override string ToString() => "BinaryTransaction [REDACTED]";
}
public static class BinaryTransactionParser
{
    public const int MaximumFrameBytes = 256;
    public static bool TryRead(ref ReadOnlySequence<byte> buffer, out BinaryTransaction? transaction)
    {
        transaction = null;
        var reader = new SequenceReader<byte>(buffer);
        if (!reader.TryReadBigEndian(out int length)) return false;
        if (length is < 60 or > MaximumFrameBytes) throw new ValidationException("invalid_frame_length");
        if (reader.Remaining < length) return false;
        var frame = buffer.Slice(reader.Position, length);
        Span<byte> scratch = stackalloc byte[MaximumFrameBytes];
        if (frame.IsSingleSegment) transaction = Parse(frame.FirstSpan);
        else { frame.CopyTo(scratch); transaction = Parse(scratch[..length]); }
        buffer = buffer.Slice(frame.End);
        return true;
    }
    private static BinaryTransaction Parse(ReadOnlySpan<byte> data)
    {
        if (data[0] != 1) throw new ValidationException("unsupported_frame_version");
        data = data[1..];
        Guid id = new(data[..16], bigEndian: true); data = data[16..];
        long minor = BinaryPrimitives.ReadInt64BigEndian(data); data = data[8..];
        string currency = Ascii(data[..3]); data = data[3..];
        long unixMs = BinaryPrimitives.ReadInt64BigEndian(data); data = data[8..];
        double latitude = BinaryPrimitives.ReadInt32BigEndian(data) / 1_000_000d; data = data[4..];
        double longitude = BinaryPrimitives.ReadInt32BigEndian(data) / 1_000_000d; data = data[4..];
        string merchant = ReadString(ref data, 1, 64);
        string pan = ReadString(ref data, 13, 19);
        if (!data.IsEmpty) throw new ValidationException("frame_trailing_bytes");
        try { return new(id, Money.FromMinor(minor, currency), merchant, pan, latitude, longitude,
            DateTimeOffset.FromUnixTimeMilliseconds(unixMs)); }
        catch (ArgumentOutOfRangeException) { throw new ValidationException("invalid_timestamp"); }
    }
    private static string ReadString(ref ReadOnlySpan<byte> data, int min, int max)
    {
        if (data.IsEmpty) throw new ValidationException("truncated_frame");
        int length = data[0]; data = data[1..];
        if (length < min || length > max || data.Length < length)
            throw new ValidationException("invalid_field_length");
        string value = Ascii(data[..length]); data = data[length..]; return value;
    }
    private static string Ascii(ReadOnlySpan<byte> value)
    {
        foreach (byte b in value) if (b is < 32 or > 126) throw new ValidationException("invalid_ascii");
        return Encoding.ASCII.GetString(value);
    }
}
