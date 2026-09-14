using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FinancialFraudPlatform.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace FinancialFraudPlatform.UnitTests;

public sealed class SecurityTests
{
    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    [Fact]
    public void Gcm_uses_fresh_nonces_and_binds_ciphertext_to_tenant_and_transaction()
    {
        using var protector = new AesPayloadProtector(new KeyRing("k1", new Dictionary<string, string> { ["k1"] = Key() }));
        byte[] input = Encoding.UTF8.GetBytes("4111111111111111");
        var first = protector.Encrypt(input, "tenant1:tx1"); var second = protector.Encrypt(input, "tenant1:tx1");
        first.Nonce.Should().NotBe(second.Nonce);
        protector.Decrypt(first, "tenant1:tx1").Should().Equal(input);
        Action swapped = () => protector.Decrypt(first, "tenant2:tx1");
        swapped.Should().Throw<CryptographicException>();
        byte[] tag = Convert.FromBase64String(first.Tag); tag[0] ^= 1;
        Action tampered = () => protector.Decrypt(first with { Tag = Convert.ToBase64String(tag) }, "tenant1:tx1");
        tampered.Should().Throw<CryptographicException>();
    }
    [Fact]
    public void Rotation_retains_decryption_for_old_key_versions()
    {
        string oldKey = Key(), newKey = Key();
        using var old = new AesPayloadProtector(new KeyRing("old", new Dictionary<string,string> { ["old"] = oldKey }));
        var envelope = old.Encrypt([1, 2, 3], "context");
        using var rotated = new AesPayloadProtector(new KeyRing("new", new Dictionary<string,string> { ["old"] = oldKey, ["new"] = newKey }));
        rotated.Decrypt(envelope, "context").Should().Equal([1, 2, 3]);
        rotated.Encrypt([1], "context").KeyId.Should().Be("new");
    }
    [Fact]
    public void Tokenization_is_stable_and_tenant_separated()
    {
        using var tokens = new CardTokenizer(Key());
        tokens.Tokenize("one", "4111111111111111").Should().Be(tokens.Tokenize("one", "4111111111111111"));
        tokens.Tokenize("one", "4111111111111111").Should().NotBe(tokens.Tokenize("two", "4111111111111111"));
    }
    [Fact]
    public void Logging_drops_pii_free_text_scopes_and_exception_messages()
    {
        using var output = new StringWriter(); using var provider = new SafeLogProvider(output);
        var logger = provider.CreateLogger("Test");
        logger.LogError(new EventId(101), new Exception("Jane Smith 192.0.2.7 4111111111111111"),
            "{CardNumber} {Name} {IPAddress} {status_code}", "4111111111111111", "Jane Smith", "192.0.2.7", 400);
        string log = output.ToString();
        log.Should().NotContain("4111111111111111").And.NotContain("Jane Smith").And.NotContain("192.0.2.7");
        log.Should().Contain("400").And.Contain("101");
    }
    [Fact]
    public async Task Authorization_requires_both_role_and_single_tenant()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["Identity:Authority"] = "http://issuer/realms/fraud" });
        builder.AddShield(); await using var app = builder.Build();
        var authorization = app.Services.GetRequiredService<IAuthorizationService>();
        ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test", "name", "roles"));
        var allowed = Principal(new("roles", "analyst"), new("tenant", "one"));
        (await authorization.AuthorizeAsync(allowed, null, "Analyst")).Succeeded.Should().BeTrue();
        (await authorization.AuthorizeAsync(allowed, null, "Writer")).Succeeded.Should().BeFalse();
        var noTenant = Principal(new("roles", "analyst"));
        (await authorization.AuthorizeAsync(noTenant, null, "Analyst")).Succeeded.Should().BeFalse();
        var duplicateTenant = Principal(new("roles", "analyst"), new("tenant", "one"), new("tenant", "two"));
        (await authorization.AuthorizeAsync(duplicateTenant, null, "Analyst")).Succeeded.Should().BeFalse();
    }
    [Fact]
    public async Task Jwt_policy_rejects_wrong_audience_wrong_signature_and_expiry()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["Identity:Authority"] = "http://issuer/realms/fraud" });
        builder.AddShield(); await using var app = builder.Build();
        var validation = app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters.Clone();
        using var rsa = RSA.Create(2048); using var wrongRsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa); validation.IssuerSigningKey = signingKey;
        string Token(string audience, DateTime expiry, RsaSecurityKey key) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "http://issuer/realms/fraud", audience: audience, claims: [new Claim("tenant", "one")],
            notBefore: DateTime.UtcNow.AddHours(-2), expires: expiry, signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
        var handler = new JwtSecurityTokenHandler();
        handler.ValidateToken(Token("fraud-platform", DateTime.UtcNow.AddMinutes(5), signingKey), validation, out _).Identity!.IsAuthenticated.Should().BeTrue();
        Action audience = () => handler.ValidateToken(Token("wrong", DateTime.UtcNow.AddMinutes(5), signingKey), validation, out _);
        Action signature = () => handler.ValidateToken(Token("fraud-platform", DateTime.UtcNow.AddMinutes(5), new RsaSecurityKey(wrongRsa)), validation, out _);
        Action expired = () => handler.ValidateToken(Token("fraud-platform", DateTime.UtcNow.AddMinutes(-5), signingKey), validation, out _);
        audience.Should().Throw<SecurityTokenException>(); signature.Should().Throw<SecurityTokenException>(); expired.Should().Throw<SecurityTokenException>();
    }
}
