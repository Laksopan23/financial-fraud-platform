using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace FinancialFraudPlatform.Dashboard;

public sealed class ExpiringAuthenticationStateProvider(ILoggerFactory loggerFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        string? expiry = authenticationState.User.FindFirst("app_expires")?.Value;
        return Task.FromResult(long.TryParse(expiry, out long unix) && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < unix);
    }
}
