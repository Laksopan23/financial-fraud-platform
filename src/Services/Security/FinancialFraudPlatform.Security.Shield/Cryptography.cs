using System.Security.Cryptography;
using System.Text;
using FinancialFraudPlatform.Core;
using Microsoft.Extensions.Configuration;

namespace FinancialFraudPlatform.Security;

public sealed class KeyRing : IDisposable
{
    private readonly Dictionary<string, byte[]> keys;
    public string ActiveId { get; }
    public KeyRing(string activeId, IReadOnlyDictionary<string, string> versions)
    {
        ActiveId = activeId;
        keys = versions.ToDictionary(pair => pair.Key, pair => Convert.FromBase64String(pair.Value));
        if (!keys.ContainsKey(activeId) || keys.Values.Any(value => value.Length != 32))
            throw new InvalidOperationException("invalid_aes256_keyring");
    }
    public static KeyRing Read(IConfiguration section) => new(
        section["ActiveId"] ?? throw new InvalidOperationException("missing_active_key_id"),
        section.GetSection("Versions").GetChildren().ToDictionary(x => x.Key,
            x => x.Value ?? throw new InvalidOperationException("missing_key")));
    public ReadOnlySpan<byte> Get(string id) => keys.TryGetValue(id, out var key)
        ? key : throw new CryptographicException("unknown_key_version");
    public void Dispose() { foreach (var key in keys.Values) CryptographicOperations.ZeroMemory(key); }
}
public sealed record EncryptedPayload(string KeyId, string Nonce, string Ciphertext, string Tag);
public sealed class AesPayloadProtector(KeyRing keys) : IDisposable
{
    public EncryptedPayload Encrypt(ReadOnlySpan<byte> cleartext, string associatedContext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], ciphertext = new byte[cleartext.Length];
        using var aes = new AesGcm(keys.Get(keys.ActiveId), 16);
        aes.Encrypt(nonce, cleartext, ciphertext, tag, Encoding.UTF8.GetBytes(associatedContext));
        return new(keys.ActiveId, Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }
    public byte[] Decrypt(EncryptedPayload payload, string associatedContext)
    {
        byte[] ciphertext = Convert.FromBase64String(payload.Ciphertext), cleartext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(keys.Get(payload.KeyId), 16);
            aes.Decrypt(Convert.FromBase64String(payload.Nonce), ciphertext, Convert.FromBase64String(payload.Tag),
                cleartext, Encoding.UTF8.GetBytes(associatedContext));
            return cleartext;
        }
        catch { CryptographicOperations.ZeroMemory(cleartext); throw; }
    }
    public void Dispose() => keys.Dispose();
}
public sealed class CardTokenizer : IDisposable
{
    private readonly byte[] key;
    public CardTokenizer(string base64Key)
    {
        key = Convert.FromBase64String(base64Key);
        if (key.Length != 32) throw new InvalidOperationException("invalid_tokenization_key");
    }
    public string Tokenize(string tenant, ReadOnlySpan<char> pan)
    {
        string validatedTenant = Identity.Tenant(tenant);
        Span<byte> input = stackalloc byte[80];
        int size = Encoding.ASCII.GetBytes(validatedTenant.AsSpan(), input);
        input[size++] = (byte)':';
        size += Encoding.ASCII.GetBytes(pan, input[size..]);
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(key, input[..size], hash);
        CryptographicOperations.ZeroMemory(input);
        return "tk1_" + Convert.ToHexString(hash);
    }
    public string Fingerprint(string canonicalPayload)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(canonicalPayload);
        try { return Convert.ToHexString(HMACSHA256.HashData(key, bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}
public sealed class AuditSigner(KeyRing keys) : IDisposable
{
    public string ActiveKeyId => keys.ActiveId;
    public string Sign(string keyId, ReadOnlySpan<byte> payload) => Convert.ToHexString(HMACSHA256.HashData(keys.Get(keyId), payload));
    public bool Verify(string keyId, ReadOnlySpan<byte> payload, string expected)
    {
        try
        {
            byte[] actual = HMACSHA256.HashData(keys.Get(keyId), payload);
            return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected));
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }
    public void Dispose() => keys.Dispose();
}
