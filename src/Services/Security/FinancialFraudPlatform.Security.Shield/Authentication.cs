using System.Security.Claims;
using FinancialFraudPlatform.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace FinancialFraudPlatform.Security;

public static class TenantAccess
{
    public static string RequireTenant(this ClaimsPrincipal principal)
    {
        string[] values = principal.FindAll("tenant").Select(x => x.Value).ToArray();
        if (principal.Identity?.IsAuthenticated != true || values.Length != 1)
            throw new UnauthorizedAccessException("invalid_tenant_claim");
        return Identity.Tenant(values[0]);
    }
    public static bool HasTenant(ClaimsPrincipal principal)
    {
        try { _ = principal.RequireTenant(); return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ValidationException) { return false; }
    }
}
public static class AuthenticationSetup
{
    public static void AddShield(this WebApplicationBuilder builder, bool dashboard = false)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new SafeLogProvider());
        builder.Services.AddSingleton(TimeProvider.System);
        var settings = builder.Configuration;
        bool development = builder.Environment.IsDevelopment();
        string authority = settings["Identity:Authority"] ?? throw new InvalidOperationException("missing_authority");
        if (!development && !authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("https_identity_required");
        var validation = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = authority.TrimEnd('/'),
            ValidateAudience = true, ValidAudience = settings["Identity:Audience"] ?? "fraud-platform",
            ValidateLifetime = true, ValidateIssuerSigningKey = true,
            RequireSignedTokens = true, RequireExpirationTime = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(15), RoleClaimType = "roles", NameClaimType = "preferred_username"
        };
        HttpMessageHandler Handler() => new IdentityBackchannelHandler(authority,
            development ? settings["Identity:InternalOrigin"] : null) { InnerHandler = new HttpClientHandler() };
        var authentication = builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = dashboard ? CookieAuthenticationDefaults.AuthenticationScheme : JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = dashboard ? OpenIdConnectDefaults.AuthenticationScheme : JwtBearerDefaults.AuthenticationScheme;
        });
        authentication.AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.RequireHttpsMetadata = !development;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = validation;
            options.BackchannelHttpHandler = Handler();
        });
        if (dashboard)
        {
            authentication.AddCookie(options =>
            {
                options.Cookie.Name = "fraud-analyst";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(15);
                options.SlidingExpiration = false;
            }).AddOpenIdConnect(options =>
            {
                options.Authority = authority;
                options.ClientId = settings["Identity:ClientId"] ?? "fraud-dashboard";
                options.ClientSecret = settings["Identity:ClientSecret"] ?? throw new InvalidOperationException("missing_oidc_secret");
                options.ResponseType = "code";
                options.ResponseMode = "query";
                if (development)
                {
                    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                    options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    options.NonceCookie.SameSite = SameSiteMode.Lax;
                }
                options.UsePkce = true;
                options.RequireHttpsMetadata = !development;
                options.MapInboundClaims = false;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.Scope.Clear(); options.Scope.Add("openid"); options.Scope.Add("profile");
                options.TokenValidationParameters = validation.Clone();
                options.TokenValidationParameters.ValidAudience = options.ClientId;
                options.BackchannelHttpHandler = Handler();
                options.Events.OnTokenValidated = context =>
                {
                    if (context.Principal?.Identity is ClaimsIdentity identity)
                        identity.AddClaim(new Claim("app_expires", DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    return Task.CompletedTask;
                };
            });
        }
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("Writer", policy => policy.RequireAuthenticatedUser().RequireRole("transaction_writer").RequireAssertion(c => TenantAccess.HasTenant(c.User)));
            options.AddPolicy("Analyst", policy => policy.RequireAuthenticatedUser().RequireRole("analyst", "auditor").RequireAssertion(c => TenantAccess.HasTenant(c.User)));
            options.AddPolicy("Auditor", policy => policy.RequireAuthenticatedUser().RequireRole("auditor").RequireAssertion(c => TenantAccess.HasTenant(c.User)));
        });
    }
}
public sealed class IdentityBackchannelHandler(string publicAuthority, string? internalOrigin) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (internalOrigin is not null && request.RequestUri is { } target)
        {
            var external = new Uri(publicAuthority);
            if (target.Scheme == external.Scheme && target.Authority == external.Authority)
            {
                var origin = new Uri(internalOrigin);
                request.RequestUri = new UriBuilder(target) { Scheme = origin.Scheme, Host = origin.Host, Port = origin.Port }.Uri;
            }
        }
        return base.SendAsync(request, cancellationToken);
    }
}
