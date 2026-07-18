using System.Text.Json;
using FellowCore.Api.Extensions;
using FellowCore.Api.Hubs;
using FellowCore.Api.Middlewares;
using FellowCore.Api.Startup;
using FellowCore.Application.Common.Interfaces;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using QuestPDF.Infrastructure;
using Serilog;

QuestPDF.Settings.License = LicenseType.Community;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj} {Properties:j}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "FellowCore")
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {SourceContext}{NewLine}  {Message:lj}{NewLine}{Exception}"));

builder.Services.AddWebApi(builder.Configuration);
builder.Services.AddJwtAuth(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplicationServices();
builder.Services.AddPaymentGateways(builder.Environment);
builder.Services.AddSecurityConfig(builder.Configuration);
builder.Services.AddEmail(builder.Configuration);
builder.Services.AddSignalR();
builder.Services.AddSingleton<IRealtimeNotifier, SignalRNotifier>();

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddBackgroundWorkers(builder.Configuration);
    builder.Services.AddIdempotency(builder.Configuration);
    builder.Services.AddStorage(builder.Configuration);
}

var app = builder.Build();

// P0 Security: Block startup with weak/default secrets in Production
if (app.Environment.IsProduction())
{
    var config = app.Configuration;
    var jwtKey = config["Jwt:SecretKey"] ?? "";
    if (string.IsNullOrEmpty(jwtKey) || jwtKey.Contains("CHANGE_ME") || jwtKey.Length < 32)
        throw new InvalidOperationException("FATAL: Jwt:SecretKey is missing, uses default placeholder, or is too short (min 32 chars). Set a strong secret for Production.");

    var masterKey = config["Security:MasterKey"] ?? "";
    if (string.IsNullOrEmpty(masterKey) || masterKey.Length < 32)
        throw new InvalidOperationException("FATAL: Security:MasterKey is missing or too short (min 32 chars). Set a strong key for Production.");

    var stripeSecret = config["Stripe:SecretKey"] ?? "";
    if (string.IsNullOrEmpty(stripeSecret) || !stripeSecret.StartsWith("sk_"))
        throw new InvalidOperationException("FATAL: Stripe:SecretKey is missing or invalid. Must start with 'sk_'.");

    var backupCodePepper = config["Security:BackupCodePepper"] ?? "";
    if (string.IsNullOrEmpty(backupCodePepper) || backupCodePepper.Contains("DEV_ONLY") || backupCodePepper.Length < 16)
        throw new InvalidOperationException("FATAL: Security:BackupCodePepper is missing, uses dev default, or is too short (min 16 chars).");

    // TD5: Validate default/dev credentials are not present in Production.
    var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
    CredentialValidator.ValidateProductionCredentials(app.Configuration, startupLogger);
}

await app.SeedDatabase();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://js.stripe.com; " +
        "frame-src https://js.stripe.com https://hooks.stripe.com https://pay.google.com https://www.google.com; " +
        "connect-src 'self' https://*.stripe.com; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https:; " +
        "font-src 'self' https://fonts.gstatic.com;";
    if (!app.Environment.IsDevelopment())
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseCors("Default");
app.UseAuthentication();
app.UseMiddleware<FellowCore.Api.Auth.JwtAuthContextMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();
if (!app.Environment.IsEnvironment("Testing"))
    app.UseBackgroundWorkersDashboard();
app.UseIdempotency();
app.MapControllers();
app.MapHub<NotificationHub>("/hubs/notifications");
// M26: Expose only the aggregate status — do not leak per-dependency names, durations,
// or internal topology to unauthenticated callers.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new { status = report.Status.ToString() }));
    }
});
// Prometheus metrics scraping endpoint — restricted to internal/loopback traffic.
// In production, also protect via ingress allowlist or Cloudflare Access.
app.MapPrometheusScrapingEndpoint("/metrics")
    .RequireHost("localhost", "127.0.0.1", "host.docker.internal", "fellowpay_api");

// Apple Pay domain verification — Stripe provides the file content via their API.
// Configure the association file content via Stripe:ApplePayDomainAssociation config key.
app.MapGet("/.well-known/apple-developer-merchantid-domain-association", (IConfiguration config) =>
{
    var content = config["Stripe:ApplePayDomainAssociation"] ?? "";
    return Results.Text(content, "text/plain");
}).AllowAnonymous();

// Public checkout config — returns only non-sensitive data (Stripe publishable key + default seller).
// The merchant API key is NEVER exposed here. Checkout page must receive it via the config panel UI only.
// L4: ?apiKey= URL query param is intentionally NOT supported (would leak key into access logs / browser history).
app.MapGet("/checkout/config", async (IConfiguration config, FellowCore.Infrastructure.Database.AppDbContext db) =>
{
    var stripePk = config["Stripe:PublishableKey"] ?? "";
    var checkoutTenantSlug = config["Checkout:TenantSlug"] ?? "";
    string? sellerId = null;

    if (!string.IsNullOrEmpty(checkoutTenantSlug))
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == checkoutTenantSlug);
        if (tenant != null)
        {
            var seller = await db.Sellers.AsNoTracking().Where(s => s.TenantId == tenant.Id).Select(s => s.Id).FirstOrDefaultAsync();
            if (seller != default) sellerId = seller.ToString();
        }
    }

    return Results.Ok(new { stripePk, sellerId });
}).AllowAnonymous();

app.MapGet("/", () => new {
    Message = "FellowCore API rodando e conectada!",
    Timestamp = DateTime.UtcNow
});

// R5: Graceful shutdown — configurable drain delay for in-flight requests
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var shutdownDelay = app.Configuration.GetValue("GracefulShutdown:DrainDelaySeconds", 10);

lifetime.ApplicationStopping.Register(() =>
{
    Log.Information("[SHUTDOWN] Application stopping — draining in-flight requests for {Delay}s...", shutdownDelay);
    Thread.Sleep(TimeSpan.FromSeconds(shutdownDelay));
    Log.Information("[SHUTDOWN] Drain period complete. Shutting down.");
});

app.Run();

public partial class Program { }