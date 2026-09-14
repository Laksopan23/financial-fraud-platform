using MassTransit;
using FinancialFraudPlatform.Dashboard.Components;
using FinancialFraudPlatform.EventBus;
using FinancialFraudPlatform.Security;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;

namespace FinancialFraudPlatform.Dashboard;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddShield(dashboard: true);
        builder.Services.AddDataProtection().SetApplicationName("FinancialFraudPlatform.Dashboard")
            .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:Path"] ?? "/keys"));
        builder.AddPlatform("dashboard", options =>
        {
            options.Schema.For<AuditCheckpoint>();
            options.Schema.For<AlertReadModel>().Index(x => x.TenantId);
            options.Schema.For<AlertReadModel>().Index(x => x.ScoredAt);
        }, bus => { bus.AddConsumer<AlertConsumer>(); bus.AddConsumer<AuditCheckpointConsumer>(); });
        builder.Services.AddMediatR(options => options.RegisterServicesFromAssemblyContaining<AlertsQueryHandler>());
        builder.Services.AddSingleton<AlertFeed>();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<AuthenticationStateProvider, ExpiringAuthenticationStateProvider>();
        builder.Services.AddSignalR(options => { options.MaximumReceiveMessageSize = 4096; options.EnableDetailedErrors = false; });
        var app = builder.Build();
        app.UseMiddleware<SafeRequestMiddleware>();
        app.UseStaticFiles(); app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        app.MapHealthChecks("/health/ready");
        app.MapGet("/login", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]));
        app.MapGet("/api/alerts", async (HttpContext context, ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new AlertsQuery(context.User.RequireTenant()), ct))).RequireAuthorization("Analyst");
        app.MapHub<AlertsHub>("/hubs/alerts", options => options.CloseOnAuthenticationExpiration = true).RequireAuthorization("Analyst");
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization("Analyst");
        await app.RunAsync();
    }
}
