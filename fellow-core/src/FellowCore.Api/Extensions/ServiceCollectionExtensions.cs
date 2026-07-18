using Microsoft.Extensions.Http.Resilience;
using Polly;
using Microsoft.Extensions.Logging;
using FellowCore.Api.ExceptionHandlers;
using FellowCore.Infrastructure.Events;
using FellowCore.Api.Filters;
using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Dashboard.Services;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Ledgers.Services;
using FellowCore.Application.Modules.Notifications.Interfaces;
using FellowCore.Application.Modules.Sellers.Interfaces;
using FellowCore.Application.Modules.Sellers.Services;
using FellowCore.Application.Modules.Settlements.Interfaces;
using FellowCore.Application.Modules.Settlements.Services;
using FellowCore.Application.Modules.Tenants.Interfaces;
using FellowCore.Application.Modules.Tenants.Services;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers;
using FellowCore.Application.Modules.Transactions.Providers.Stripe;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using FellowCore.Application.Modules.Transactions.Services;
using FellowCore.Application.Modules.Transactions.Rails;
using FellowCore.Application.Modules.Notifications.Handlers;
using FellowCore.Application.Modules.Customers.Interfaces;
using FellowCore.Application.Modules.Customers.Services;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Application.Modules.Payouts.Services;
using FellowCore.Application.Modules.Subscriptions.Interfaces;
using FellowCore.Application.Modules.Subscriptions.Services;
using FellowCore.Application.Modules.Webhooks.Interfaces;
using FellowCore.Application.Modules.Webhooks.Services;
using FellowCore.Application.Modules.AuditLogs.Interfaces;
using FellowCore.Application.Modules.AuditLogs.Services;
using FellowCore.Application.Modules.Auth.Interfaces;
using FellowCore.Application.Modules.Auth.Services;
using FellowCore.Application.Modules.PixPayments.Interfaces;
using FellowCore.Application.Modules.PixPayments.Services;
using FellowCore.Application.Modules.Pricing.Interfaces;
using FellowCore.Application.Modules.Pricing.Services;
using FellowCore.Application.Modules.Splits.Interfaces;
using FellowCore.Application.Modules.Splits.Services;
using FellowCore.Application.Modules.Fiscal;
using FellowCore.Application.Modules.Receipts;
using FellowCore.Application.Modules.Reconciliation.Interfaces;
using FellowCore.Application.Modules.Reconciliation.Services;
using FellowCore.Application.Modules.Reports.Interfaces;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Infrastructure.Auth;
using FellowCore.Infrastructure.Email;
using FellowCore.Infrastructure.Webhooks;
using FellowCore.Infrastructure.Export;
using FellowCore.Domain.Events;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using FellowCore.Infrastructure.Notifications;
using FellowCore.Infrastructure.Workers.Processors;
using FellowCore.Infrastructure.Repositories;
using FellowCore.Infrastructure.Security;
using FellowCore.Infrastructure.Workers.BackgroundServices;
using FellowCore.Infrastructure.Workers.Options;
using Microsoft.EntityFrameworkCore;
using Hangfire;
using Hangfire.PostgreSql;
using Hangfire.Dashboard;
using FellowCore.Infrastructure.Database.Seeding;
using StackExchange.Redis;
using FellowCore.Infrastructure.Idempotency;
using FellowCore.Infrastructure.Setup;
using FellowCore.Infrastructure.Storage;
using FellowCore.Api.Middlewares.Idempotency;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Hosting;
using System.Text;
using System.Threading.RateLimiting;
using Amazon.S3;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using FluentValidation;
using FluentValidation.AspNetCore;
using FellowCore.Api.HealthChecks;
using FellowCore.Api.Metrics;
using OpenTelemetry.Metrics;

namespace FellowCore.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        services.AddScoped<IDomainEventHandler<TransactionStatusChangedEvent>, TransactionStatusChangedHandler>();
        services.AddScoped<IDomainEventHandler<TransactionCreatedEvent>, TransactionCreatedHandler>();
        services.AddScoped<IDomainEventHandler<SellerCreatedEvent>, SellerCreatedHandler>();
        services.AddScoped<IDomainEventHandler<TenantCreatedEvent>, TenantCreatedHandler>();
        services.AddScoped<IDomainEventHandler<SubscriptionCreatedEvent>, SubscriptionCreatedHandler>();
        // Tier change → notificação in-app. Disparado pelo SellerTierRecomputeProcessor
        // só pra transições reais (upgrade/downgrade). Producer roda fire-and-forget
        // dentro do NotificationService — falha aqui não bloqueia o job de recálculo.
        services.AddScoped<
            IDomainEventHandler<SellerTierChangedEvent>,
            FellowCore.Application.Modules.Notifications.Handlers.SellerTierChangedHandler>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ILedgerRepository, LedgerRepository>();
        services.AddScoped<ITransactionRepository, TransactionRepository>();
        services.AddScoped<ITransactionInstallmentRepository, TransactionInstallmentRepository>();
        services.AddScoped<FellowCore.Application.Modules.Settlements.AdvanceRisk.IAdvanceRiskEvaluator,
            FellowCore.Application.Modules.Settlements.AdvanceRisk.AdvanceRiskEvaluator>();
        services.Configure<FellowCore.Application.Modules.Settlements.AdvanceRisk.AdvanceRiskOptions>(
            configuration.GetSection("AdvanceRisk"));
        services.Configure<FellowCore.Application.Modules.Pricing.Options.TierPricingOptions>(
            configuration.GetSection(FellowCore.Application.Modules.Pricing.Options.TierPricingOptions.SectionName));
        services.AddHostedService<FellowCore.Infrastructure.Pricing.TierPricingFloorValidator>();
        services.AddScoped<ISellerRepository, SellerRepository>();
        services.AddScoped<IWebhookEndpointRepository, WebhookEndpointRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IPayoutRepository, PayoutRepository>();
        services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
        services.AddScoped<IWebhookDeliveryRepository, WebhookDeliveryRepository>();
        services.AddScoped<IInboundWebhookEventRepository, InboundWebhookEventRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IScheduledReportRepository, ScheduledReportRepository>();
        services.AddScoped<ILoginLogRepository, LoginLogRepository>();
        services.AddScoped<IPaymentLinkRepository, PaymentLinkRepository>();
        services.AddScoped<IPixPaymentRepository, PixPaymentRepository>();
        services.AddScoped<IReconciliationRepository, ReconciliationRepository>();
        services.AddScoped<ISettlementReportRepository, SettlementReportRepository>();
        services.AddScoped<IPaymentIntentRepository, PaymentIntentRepository>();
        services.AddScoped<IDisputeRepository, DisputeRepository>();
        services.AddScoped<IRefundIntentRepository, RefundIntentRepository>();
        services.AddScoped<IProviderCostScheduleRepository, ProviderCostScheduleRepository>();
        services.AddScoped<ISplitRuleRepository, SplitRuleRepository>();
        services.AddScoped<ISplitTransferRepository, SplitTransferRepository>();
        services.AddScoped<IReceiptRepository, ReceiptRepository>();
        services.AddScoped<ISellerFiscalSettingsRepository, SellerFiscalSettingsRepository>();
        services.AddScoped<IFiscalInvoiceRepository, FiscalInvoiceRepository>();
        services.AddScoped<ITransactionItemRepository, TransactionItemRepository>();
        services.AddScoped<ISplitAllocationRepository, SplitAllocationRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<INotificationOutboxRepository, NotificationOutboxRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IAffiliationRepository, AffiliationRepository>();
        services.AddScoped<ICouponRepository, CouponRepository>();
        services.AddScoped<IProductOrderBumpRepository, ProductOrderBumpRepository>();

        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<ITenantService>(sp =>
        {
            var repo = sp.GetRequiredService<ITenantRepository>();
            var cache = sp.GetService<IDistributedCache>();
            var env = sp.GetRequiredService<IHostEnvironment>();
            return new TenantService(repo, cache, env.IsProduction());
        });
        services.AddScoped<ILedgerService, LedgerService>();
        services.AddScoped<ISettlementService, SettlementService>();
        services.AddScoped<ITransactionService, TransactionService>();
        services.AddScoped<ISellerService, SellerService>();
        services.AddScoped<ISellerTierService, SellerTierService>();
        services.AddScoped<IWebhooksService, WebhooksService>();
        services.AddScoped<IWebhookProbeClient, WebhookProbeClient>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IPayoutService, PayoutService>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        services.AddScoped<ISubscriptionBillingProcessor, SubscriptionBillingProcessor>();
        services.AddScoped<IPayoutProcessor, OpenPixPayoutProcessor>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IPixPaymentService, PixPaymentService>();
        services.AddScoped<INotificationsService, NotificationsService>();
        services.AddScoped<INotificationsProcessor, NotificationsProcessor>();
        services.AddScoped<ITenantWebhookProcessor, TenantWebhookProcessor>();
        services.AddScoped<IWebhookRetryProcessor, WebhookRetryProcessor>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IExportService, ExportService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IScheduledReportProcessor, ScheduledReportProcessor>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddScoped<ITotpService, TotpService>();
        services.AddScoped<IDunningProcessor, DunningProcessor>();
        services.AddScoped<ISplitProcessor, SplitProcessor>();
        services.AddScoped<IPayoutRetryProcessor, PayoutRetryProcessor>();
        services.AddScoped<IRefundRetryProcessor, RefundRetryProcessor>();
        services.AddScoped<IStaleTransactionCleanupProcessor, StaleTransactionCleanupProcessor>();
        services.AddScoped<IWithdrawalResumeProcessor, WithdrawalResumeProcessor>();
        services.AddScoped<IAdvanceSettlementReconciler, AdvanceSettlementReconciler>();
        services.AddScoped<IStripeAdvanceReconciler, StripeAdvanceReconciler>();
        services.AddScoped<ISellerRiskProfileRefreshProcessor, SellerRiskProfileRefreshProcessor>();
        services.AddScoped<ISellerTierRecomputeProcessor, SellerTierRecomputeProcessor>();
        services.AddScoped<ISellerRiskProfileRepository, SellerRiskProfileRepository>();
        services.AddScoped<ISellerTierProfileRepository, SellerTierProfileRepository>();
        services.AddScoped<FellowCore.Domain.Interfaces.IWithdrawalAttemptRepository, FellowCore.Infrastructure.Repositories.WithdrawalAttemptRepository>();
        services.AddScoped<FellowCore.Application.Modules.Payouts.Interfaces.IPayoutGateway, FellowCore.Application.Modules.Payouts.Gateways.StripePayoutGateway>();
        services.AddScoped<FellowCore.Application.Modules.Payouts.Interfaces.IPayoutGateway, FellowCore.Application.Modules.Payouts.Gateways.OpenPixPayoutGateway>();
        services.AddScoped<FellowCore.Application.Modules.Payouts.Interfaces.IPayoutGatewayFactory, FellowCore.Application.Modules.Payouts.Gateways.PayoutGatewayFactory>();
        services.AddScoped<FellowCore.Application.Modules.Payouts.Services.IWithdrawOrchestrator, FellowCore.Application.Modules.Payouts.Services.WithdrawOrchestrator>();
        // Withdraw flow novo (2026-05-15) — facade comercial sobre PayoutProcessor com regras D0/D1, cap diário e fila FIFO.
        services.AddScoped<IWithdrawService, FellowCore.Application.Modules.Payouts.Services.WithdrawService>();
        services.AddScoped<IWithdrawQueueProcessor, FellowCore.Infrastructure.Workers.Processors.WithdrawQueueProcessor>();
        services.AddScoped<IReceiptService, ReceiptService>();
        services.AddScoped<FellowCore.Application.Modules.PaymentLinks.Interfaces.IPaymentLinkService, FellowCore.Application.Modules.PaymentLinks.Services.PaymentLinkService>();
        services.AddScoped<IProviderCostService, ProviderCostService>();
        services.AddScoped<IPricingService, PricingService>();
        // Sprint 1.5: PlanEligibility / PlanSimulator / PlanMigration / MonthlyPlanBilling
        // foram deletados — sistema agora é 100% tier-based, sem planos a migrar/simular.
        services.AddScoped<ISplitRuleService, SplitRuleService>();
        services.AddSingleton<ISplitCalculationService, SplitCalculationService>();
        services.AddScoped<ISplitSimulatorService, SplitSimulatorService>();
        services.AddScoped<IItemSplitResolver, ItemSplitResolver>();
        services.AddScoped<IFiscalService, FiscalService>();

        // Payment rails + router
        services.AddScoped<IPaymentRail, StripeCardRail>();
        services.AddScoped<IPaymentRail, StripeBoletoRail>();
        services.AddScoped<IPaymentRail, OpenPixRail>();
        services.AddScoped<IRailRouter, RailRouter>();
        services.AddScoped<IReconciliationService, ReconciliationService>();
        services.AddScoped<ISettlementReconciliationService, SettlementReconciliationService>();
        services.AddScoped<
            FellowCore.Application.Modules.Notifications.Interfaces.INotificationService,
            FellowCore.Application.Modules.Notifications.Services.NotificationService>();
        services.AddScoped<
            FellowCore.Application.Modules.Marketplace.Interfaces.IProductService,
            FellowCore.Application.Modules.Marketplace.Services.ProductService>();
        services.AddScoped<
            FellowCore.Application.Modules.Marketplace.Interfaces.IAffiliationService,
            FellowCore.Application.Modules.Marketplace.Services.AffiliationService>();
        services.AddScoped<
            FellowCore.Application.Modules.Marketplace.Interfaces.IMarketplaceCheckoutService,
            FellowCore.Application.Modules.Marketplace.Services.MarketplaceCheckoutService>();
        services.AddScoped<
            FellowCore.Application.Modules.Marketplace.Interfaces.ICouponService,
            FellowCore.Application.Modules.Marketplace.Services.CouponService>();
        services.AddScoped<
            FellowCore.Application.Modules.Marketplace.Interfaces.IProductOrderBumpService,
            FellowCore.Application.Modules.Marketplace.Services.ProductOrderBumpService>();
        services.AddScoped<ISettlementReportProvider, FellowCore.Application.Modules.Reconciliation.Providers.StripeSettlementProvider>();
        services.AddScoped<ISettlementReportProvider, FellowCore.Application.Modules.Reconciliation.Providers.OpenPixSettlementProvider>();
        services.AddScoped<FellowCore.Application.Modules.Reconciliation.Providers.CsvSettlementProvider>();
        services.AddScoped<FellowCore.Application.Modules.Reconciliation.Interfaces.IAlertService, FellowCore.Application.Modules.Reconciliation.Services.AlertService>();
        services.AddScoped<IBackgroundJobs, FellowCore.Infrastructure.Jobs.HangfireBackgroundJobs>();
        services.AddScoped<FellowCore.Infrastructure.Workers.Processors.OutboxProcessor>();
        services.AddScoped<
            FellowCore.Infrastructure.Workers.Processors.INotificationOutboxProcessor,
            FellowCore.Infrastructure.Workers.Processors.NotificationOutboxProcessor>();

        services.AddSingleton(TimeProvider.System);

        return services;
    }

    public static IServiceCollection AddPaymentGateways(this IServiceCollection services, IWebHostEnvironment environment)
    {
        services.AddScoped<IPaymentProviderFactory, PaymentProviderFactory>();

        services.AddHttpClient<IStripeApiClient, StripeApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.stripe.com");
        })
        .AddHttpMessageHandler(sp => new ProviderMetricsDelegatingHandler(
            sp.GetRequiredService<FellowCoreMetrics>(), "stripe"))
        .AddResilienceHandler("stripe", ConfigurePaymentProviderResilience);

        services.AddKeyedScoped<IPaymentProvider, StripePaymentProvider>(PaymentProvider.STRIPE);

        services.AddHttpClient<IOpenPixApiClient, OpenPixApiClient>((sp, client) =>
        {
            // Configurável pra alternar prod ↔ sandbox sem rebuild. Default
            // produção (api.openpix.com.br). Sandbox: api.woovi-sandbox.com via
            // OpenPix:BaseUrl no appsettings.Development.json.
            var cfg = sp.GetRequiredService<IConfiguration>();
            var baseUrl = cfg["OpenPix:BaseUrl"] ?? "https://api.openpix.com.br";
            client.BaseAddress = new Uri(baseUrl);
        })
        .AddHttpMessageHandler(sp => new ProviderMetricsDelegatingHandler(
            sp.GetRequiredService<FellowCoreMetrics>(), "openpix"))
        .AddResilienceHandler("openpix", ConfigurePaymentProviderResilience);

        services.AddKeyedScoped<IPaymentProvider, OpenPixPaymentProvider>(PaymentProvider.OPENPIX);

        services.AddHostedService<OpenPixWebhookSetup>();

        return services;
    }

    /// <summary>
    /// Configures resilience policies for payment provider HttpClients:
    /// R1 — Timeout (30s) + Retry (3x exponential backoff + jitter) + Circuit Breaker (5 failures, 30s break)
    /// R3 — Concurrency Limiter (max 10 concurrent calls per provider)
    /// </summary>
    private static void ConfigurePaymentProviderResilience(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        // R3: Concurrency limiter (bulkhead) — max 10 concurrent calls per provider
        builder.AddConcurrencyLimiter(new System.Threading.RateLimiting.ConcurrencyLimiterOptions
        {
            PermitLimit = 10,
            QueueLimit = 20
        });

        // R1/R2: Retry with exponential backoff + jitter (3 retries)
        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(1),
            ShouldHandle = static args => ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome)),
            OnRetry = static args =>
            {
                var logger = args.Context.Properties.GetValue(
                    new ResiliencePropertyKey<ILogger?>("logger"), null);
                logger?.LogWarning(
                    "[RESILIENCE] Retry #{Attempt} after {Delay}ms for {OperationKey}",
                    args.AttemptNumber + 1,
                    args.RetryDelay.TotalMilliseconds,
                    args.Context.OperationKey);
                return ValueTask.CompletedTask;
            }
        });

        // R1/R2: Circuit breaker — open after 5 consecutive failures, break for 30s
        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 1.0, // 100% = consecutive failures only
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = static args => ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome)),
            OnOpened = static args =>
            {
                var logger = args.Context.Properties.GetValue(
                    new ResiliencePropertyKey<ILogger?>("logger"), null);
                logger?.LogCritical(
                    "[RESILIENCE] Circuit OPENED for {OperationKey} — breaking for {Duration}s after consecutive failures",
                    args.Context.OperationKey,
                    args.BreakDuration.TotalSeconds);
                var metrics = args.Context.Properties.GetValue(
                    new ResiliencePropertyKey<FellowCoreMetrics?>("metrics"), null);
                metrics?.SetCircuitBreakerState(args.Context.OperationKey, 1); // 1 = open
                return ValueTask.CompletedTask;
            },
            OnClosed = static args =>
            {
                var logger = args.Context.Properties.GetValue(
                    new ResiliencePropertyKey<ILogger?>("logger"), null);
                logger?.LogInformation(
                    "[RESILIENCE] Circuit CLOSED for {OperationKey} — traffic resumed",
                    args.Context.OperationKey);
                var metrics = args.Context.Properties.GetValue(
                    new ResiliencePropertyKey<FellowCoreMetrics?>("metrics"), null);
                metrics?.SetCircuitBreakerState(args.Context.OperationKey, 0); // 0 = closed
                return ValueTask.CompletedTask;
            },
            OnHalfOpened = static args =>
            {
                var metrics = args.Context.Properties.GetValue(
                    new ResiliencePropertyKey<FellowCoreMetrics?>("metrics"), null);
                metrics?.SetCircuitBreakerState(args.Context.OperationKey, 2); // 2 = half-open
                return ValueTask.CompletedTask;
            }
        });

        // R1: Total request timeout — 30 seconds
        builder.AddTimeout(TimeSpan.FromSeconds(30));
    }

    public static IServiceCollection AddSecurityConfig(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SecurityOptions>(configuration.GetSection("Security"));
        services.AddScoped<ISecurityService, SecurityService>();

        return services;
    }

    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailOptions>(configuration.GetSection("Email"));

        services.AddHttpClient<IEmailService, ResendEmailProvider>(client =>
        {
            client.BaseAddress = new Uri("https://api.resend.com");
        })
        .AddStandardResilienceHandler();

        return services;
    }

    public static IServiceCollection AddBackgroundWorkers(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SettlementWorkerOptions>(configuration.GetSection("SettlementWorker"));
        services.AddHostedService<SettlementWorker>();

        services.AddHangfire(config => config
                .UsePostgreSqlStorage( options =>
                    options.UseNpgsqlConnection(configuration.GetConnectionString("DefaultConnection"))));

        services.AddHangfireServer();

        // SSRF-safe HttpClient for webhook delivery: resolves DNS and blocks private IPs, disables redirects
        services.AddHttpClient("WebhookClient")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var host = context.DnsEndPoint.Host;
                    var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);

                    foreach (var addr in addresses)
                    {
                        if (IsPrivateAddress(addr))
                            throw new HttpRequestException($"Webhook URL resolved to private/reserved IP ({addr}). Delivery blocked.");
                    }

                    // Connect to the first public address
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            });

        return services;
    }

    private static bool IsPrivateAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal) return true;

        byte[] bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 => true,                                           // 0.0.0.0/8
                10 => true,                                          // 10.0.0.0/8
                100 when bytes[1] >= 64 && bytes[1] <= 127 => true,  // 100.64.0.0/10 (CGNAT)
                127 => true,                                         // 127.0.0.0/8
                169 when bytes[1] == 254 => true,                    // 169.254.0.0/16 (link-local / cloud metadata)
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,   // 172.16.0.0/12
                192 when bytes[1] == 168 => true,                    // 192.168.0.0/16
                _ => false
            };
        }

        return false;
    }

    public static IServiceCollection AddJwtAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));

        var jwtSection = configuration.GetSection("Jwt");
        var secretKey = jwtSection["SecretKey"] ?? throw new InvalidOperationException("Jwt:SecretKey not configured.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
                    ValidateIssuer = true,
                    ValidIssuer = jwtSection["Issuer"] ?? "fellowpay",
                    ValidateAudience = true,
                    ValidAudience = jwtSection["Audience"] ?? "fellowpay-dashboard",
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    RoleClaimType = "role",
                    NameClaimType = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub
                };

                // SignalR token via query string. WebSocket handshake do browser
                // não carrega header Authorization customizado — o cliente
                // SignalR manda o token como ?access_token=xxx. Esse handler
                // intercepta requests pra /hubs/* e move o query param pro
                // Token interno (que o middleware bearer já valida).
                // LongPolling continua funcionando com Authorization header
                // normal — esse handler só toma efeito quando há query param E
                // o path bate com /hubs/*.
                options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) &&
                            path.StartsWithSegments("/hubs"))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();

        return services;
    }

    public static IServiceCollection AddWebApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Fellow Core API";
                document.Info.Version = "v1";
                document.Info.Description = "A API de pagamentos que conecta, autoriza e escala. Documentação completa em https://docs.fellowpay.com.br";
                document.Info.Contact = new()
                {
                    Name = "Grupo Fellow",
                    Email = "suporte@grupofellow.com.br",
                    Url = new Uri("https://grupofellow.com.br")
                };
                return Task.CompletedTask;
            });
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();
        services.AddScoped<AuditActionFilter>();
        services.AddControllers(options =>
        {
            options.Filters.Add<StandardResponseFilter>();
            options.Filters.Add<AuditActionFilter>();
        });
        services.AddFluentValidationAutoValidation();
        services.AddValidatorsFromAssemblyContaining<FellowCore.Application.Modules.Transactions.Validators.CreateTransactionDtoValidator>();
        services.AddValidatorsFromAssemblyContaining<FellowCore.Api.Validators.UpdateTransactionDtoValidator>();
        services.AddHealthChecks()
            .AddNpgSql(configuration.GetConnectionString("DefaultConnection")!, name: "postgresql")
            .AddRedis(
                sp => sp.GetRequiredService<IConnectionMultiplexer>(),
                name: "redis")
            .AddCheck<StripeHealthCheck>("stripe", tags: ["external"])
            .AddCheck<OpenPixHealthCheck>("openpix", tags: ["external"])
            .AddCheck<HangfireHealthCheck>("hangfire", tags: ["worker"]);

        // OpenTelemetry metrics with Prometheus exporter
        services.AddSingleton<FellowCoreMetrics>();
        services.AddSingleton<IAppMetrics, AppMetricsAdapter>();
        services.AddHostedService<MetricsCollectorWorker>();
        services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddHttpClientInstrumentation();
                metrics.AddMeter(FellowCoreMetrics.MeterName);
                metrics.AddPrometheusExporter();
            });

        int fixedLimit = configuration.GetValue("RateLimiting:FixedPermitLimit", 100);
        int fixedWindowSeconds = configuration.GetValue("RateLimiting:FixedWindowSeconds", 60);
        int webhooksLimit = configuration.GetValue("RateLimiting:WebhooksPermitLimit", 500);
        int webhooksWindowSeconds = configuration.GetValue("RateLimiting:WebhooksWindowSeconds", 60);
        int authLimit = configuration.GetValue("RateLimiting:AuthPermitLimit", 5);
        int authWindowSeconds = configuration.GetValue("RateLimiting:AuthWindowSeconds", 300);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy("fixed", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = fixedLimit,
                        Window = TimeSpan.FromSeconds(fixedWindowSeconds),
                        QueueLimit = 0
                    }));

            options.AddPolicy("webhooks", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = webhooksLimit,
                        Window = TimeSpan.FromSeconds(webhooksWindowSeconds),
                        QueueLimit = 0
                    }));

            options.AddPolicy("auth-sensitive", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = authLimit,
                        Window = TimeSpan.FromSeconds(authWindowSeconds),
                        QueueLimit = 0
                    }));
        });

        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();

        services.AddCors(options =>
        {
            options.AddPolicy("Default", policy =>
            {
                if (allowedOrigins is { Length: > 0 })
                {
                    policy.WithOrigins(allowedOrigins)
                          .AllowAnyMethod()
                          .AllowAnyHeader()
                          .AllowCredentials();
                }
                else
                {
                    policy.SetIsOriginAllowed(_ => false);
                }
            });
        });

        return services;
    }

    public static WebApplication UseBackgroundWorkersDashboard(this WebApplication app)
    {
        // I4: Hangfire dashboard is only mounted in Development.
        // In non-Development environments the /hangfire route is not registered at all,
        // so it returns 404 rather than being accessible with any credential.
        // Job scheduling (RecurringJob.AddOrUpdate) still runs in all environments.
        if (app.Environment.IsDevelopment())
        {
            app.UseHangfireDashboard("/hangfire", new DashboardOptions
            {
                Authorization = [new PermissiveDashboardAuthorizationFilter()]
            });
        }

        RecurringJob.AddOrUpdate<ISubscriptionBillingProcessor>(
            "subscription-billing",
            processor => processor.ProcessDueBillingAsync(CancellationToken.None),
            Cron.Hourly);

        RecurringJob.AddOrUpdate<IWebhookRetryProcessor>(
            "webhook-retry",
            processor => processor.ProcessPendingRetriesAsync(CancellationToken.None),
            Cron.Minutely);

        RecurringJob.AddOrUpdate<ISettlementService>(
            "daily-settlement",
            service => service.ProcessDailySettlementsAsync(),
            Cron.Daily(6, 0)); // 06:00 UTC

        RecurringJob.AddOrUpdate<IScheduledReportProcessor>(
            "scheduled-reports",
            processor => processor.ProcessDueReportsAsync(CancellationToken.None),
            Cron.Daily(7, 0)); // 07:00 UTC

        RecurringJob.AddOrUpdate<IDunningProcessor>(
            "dunning",
            processor => processor.ProcessDunningAsync(CancellationToken.None),
            Cron.Hourly);

        RecurringJob.AddOrUpdate<FellowCore.Infrastructure.Workers.Processors.OutboxProcessor>(
            "outbox-processor",
            processor => processor.ProcessAsync(),
            "*/15 * * * * *"); // every 15 seconds

        // Notification outbox — materializa intents → Notifications visíveis no portal.
        // 10s = balanceia "latência aceitável pro seller" vs "frequência de poll do DB".
        // Cada execução faz 1 SELECT (partial index) + N INSERT/UPDATE. Custo baixo.
        RecurringJob.AddOrUpdate<FellowCore.Infrastructure.Workers.Processors.INotificationOutboxProcessor>(
            "notification-outbox",
            processor => processor.ProcessAsync(CancellationToken.None),
            "*/10 * * * * *"); // every 10 seconds

        RecurringJob.AddOrUpdate<ISplitProcessor>(
            "split-processing",
            processor => processor.ProcessAllPendingSplitsAsync(CancellationToken.None),
            Cron.Minutely);

        RecurringJob.AddOrUpdate<IPayoutRetryProcessor>(
            "payout-retry",
            processor => processor.ProcessAsync(CancellationToken.None),
            Cron.Minutely);

        // Esvazia fila FIFO de saques agendados (D+1 ou cap-excedido). Roda a cada 5min.
        RecurringJob.AddOrUpdate<IWithdrawQueueProcessor>(
            "withdraw-queue",
            processor => processor.ProcessAsync(CancellationToken.None),
            "*/5 * * * *"); // every 5 minutes

        RecurringJob.AddOrUpdate<IRefundRetryProcessor>(
            "refund-retry",
            processor => processor.ProcessAsync(CancellationToken.None),
            Cron.Minutely);

        RecurringJob.AddOrUpdate<IReconciliationService>(
            "daily-reconciliation",
            service => service.RunDailyReconciliationAsync(CancellationToken.None),
            Cron.Daily(5, 0)); // 05:00 UTC — antes do settlement das 06:00

        RecurringJob.AddOrUpdate<ISettlementReconciliationService>(
            "daily-settlement-reconciliation",
            service => service.RunDailySettlementReconciliationAsync(CancellationToken.None),
            Cron.Daily(4, 30)); // 04:30 UTC — antes da reconciliacao interna das 05:00

        RecurringJob.AddOrUpdate<IAdvanceSettlementReconciler>(
            "advance-settlement-reconcile",
            reconciler => reconciler.ProcessAsync(CancellationToken.None),
            Cron.Daily(3, 0)); // 03:00 UTC — antes do settlement processor (06:00)

        RecurringJob.AddOrUpdate<ISellerRiskProfileRefreshProcessor>(
            "seller-risk-profile-refresh",
            processor => processor.ProcessAsync(CancellationToken.None),
            Cron.Daily(2, 0)); // 02:00 UTC — antes do advance-reconcile (03:00) e da maioria das capturas do dia

        // Recalculo mensal de tier do seller. Dia 1 às 04:00 UTC — depois do risk-profile
        // refresh (que roda diariamente às 02:00) garantindo chargebackRate atualizado.
        // Cron "0 4 1 * *" = minuto 0, hora 4, dia 1 do mês, qualquer mês, qualquer dia da semana.
        RecurringJob.AddOrUpdate<ISellerTierRecomputeProcessor>(
            "seller-tier-recompute",
            processor => processor.ProcessAsync(CancellationToken.None),
            "0 4 1 * *");

        RecurringJob.AddOrUpdate<IStripeAdvanceReconciler>(
            "stripe-advance-reconcile",
            reconciler => reconciler.ProcessAsync(CancellationToken.None),
            Cron.Hourly()); // hourly pra precisão; no-op interno quando UseStripe=false

        // Sprint 1.5: sistema agora é 100% tier-based sem planos com mensalidade.
        // Removemos o job de cobrança mensal e fazemos cleanup dos antigos no Hangfire
        // (idempotente — remove se existir, sem erro se não).
        Hangfire.RecurringJob.RemoveIfExists("monthly-plan-billing");
        Hangfire.RecurringJob.RemoveIfExists("scala-monthly-billing");

        // DB3: Weekly data retention cleanup (Sundays 03:00 UTC)
        RecurringJob.AddOrUpdate<FellowCore.Infrastructure.Workers.Processors.DataRetentionProcessor>(
            "data-retention",
            processor => processor.ProcessAsync(CancellationToken.None),
            Cron.Weekly(DayOfWeek.Sunday, 3));

        // Cancela TXs zumbis (CREATED/PROCESSING velhas) a cada hora — limpa
        // o "em andamento" inflado por PIs abandonados, smoke tests, PIX/boletos
        // expirados que ninguém pagou, etc. Thresholds: cartão/PIX 24h, boleto 7d.
        RecurringJob.AddOrUpdate<FellowCore.Infrastructure.Workers.Processors.IStaleTransactionCleanupProcessor>(
            "stale-tx-cleanup",
            processor => processor.ProcessAsync(CancellationToken.None),
            Cron.Hourly);

        // Resume de saga de saque (WithdrawalAttempt) — pega attempts em
        // PENDING/IN_PROGRESS após crash do API e continua processing. Cada
        // step tem idempotency-key determinística no provider → retry seguro.
        RecurringJob.AddOrUpdate<FellowCore.Infrastructure.Workers.Processors.IWithdrawalResumeProcessor>(
            "withdrawal-resume",
            processor => processor.ProcessAsync(CancellationToken.None),
            Cron.Minutely);

        return app;
    }

    public static async Task<WebApplication> SeedDatabase(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference(options =>
            {
                options
                    .WithTitle("Fellow Core API")
                    .WithTheme(ScalarTheme.DeepSpace)
                    .WithDefaultHttpClient(ScalarTarget.Shell, ScalarClient.Curl);
            });
            await DatabaseSeeder.SeedAsync(app.Services);
        }

        return app;
    }

    public static IServiceCollection AddIdempotency(this IServiceCollection services, IConfiguration configuration)
    {
        string? redisHost = configuration["REDIS_HOST"] ?? "localhost";
        string? redisPort = configuration["REDIS_PORT"] ?? "6379";
        string? redisPassword = configuration["REDIS_PASSWORD"];

        string redisConnectionString = string.IsNullOrEmpty(redisPassword)
            ? $"{redisHost}:{redisPort}"
            : $"{redisHost}:{redisPort},password={redisPassword}";

        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(redisConnectionString));

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnectionString;
            options.InstanceName = "FellowCore:";
        });

        services.AddScoped<IIdempotencyService, RedisIdempotencyService>();

        return services;
    }

    public static WebApplication UseIdempotency(this WebApplication app)
    {
        app.UseMiddleware<IdempotencyMiddleware>();
        return app;
    }

    public static IServiceCollection AddStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection("Storage"));

        var storageOptions = configuration.GetSection("Storage").Get<StorageOptions>()!;

        services.AddSingleton<IAmazonS3>(_ =>
        {
            var config = new AmazonS3Config
            {
                ServiceURL = storageOptions.Endpoint,
                ForcePathStyle = true
            };
            return new AmazonS3Client(storageOptions.AccessKey, storageOptions.SecretKey, config);
        });

        services.AddScoped<IStorageService, MinioStorageService>();

        return services;
    }
}

public class PermissiveDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => true;
}

public class HangfireApiKeyAuthorizationFilter(IConfiguration configuration) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var expectedKey = configuration["Hangfire:DashboardApiKey"];

        if (string.IsNullOrEmpty(expectedKey))
            return false;

        if (!httpContext.Request.Headers.TryGetValue("X-Hangfire-Key", out var key)
            || string.IsNullOrEmpty(key.ToString()))
            return false;

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(key.ToString()),
            System.Text.Encoding.UTF8.GetBytes(expectedKey));
    }
}