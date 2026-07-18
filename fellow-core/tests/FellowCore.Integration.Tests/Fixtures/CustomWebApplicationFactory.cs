using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Modules.Email.Interfaces;
using FellowCore.Application.Modules.Webhooks.DTOs;
using FellowCore.Application.Modules.Webhooks.Interfaces;
using FellowCore.Application.Modules.Payouts.Interfaces;
using FellowCore.Application.Modules.Sellers.DTOs;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Models;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Models;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Infrastructure.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Hosting;

namespace FellowCore.Integration.Tests.Fixtures;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private SqliteConnection? _connection;

    public CustomWebApplicationFactory()
    {
        // Set env vars BEFORE Program.Main runs so builder.Configuration can resolve them.
        // WebApplicationFactory calls Program.Main inside CreateHost, and builder.Configuration
        // reads env vars at that point. ConfigureAppConfiguration runs too late for AddWebApi().
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection",
            "Host=localhost;Database=fellowpay_test;Username=test;Password=test");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Prevent Serilog "logger already frozen" across test classes
        Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().CreateLogger();
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenPix:AppId"] = "fake-app-id-for-tests",
                ["Stripe:SecretKey"] = "sk_test_fake_key_for_integration_tests",
                ["Stripe:WebhookSecret"] = "whsec_test_secret_for_integration_tests",
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=fellowpay_test;Username=test;Password=test",
                ["Security:MasterKey"] = "test-master-key-for-integration-tests-minimum-32-chars",
                ["Security:BackupCodePepper"] = "test-backup-code-pepper-for-integration"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Replace Serilog with basic logging
            services.RemoveAll<ILoggerFactory>();
            services.AddLogging(l => l.ClearProviders());

            // Remove real DbContext registrations
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor != null) services.Remove(descriptor);
            services.RemoveAll<AppDbContext>();

            // SQLite in-memory with shared connection
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            services.AddDbContext<TestAppDbContext>(options =>
                options.UseSqlite(_connection));
            services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<TestAppDbContext>());

            // Ensure database is created
            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TestAppDbContext>();
            db.Database.EnsureCreated();

            // Remove hosted services
            services.RemoveAll<IHostedService>();

            // Fakes
            services.RemoveAll<IIdempotencyService>();
            services.AddScoped<IIdempotencyService, FakeIdempotencyService>();

            services.RemoveAll<ISecurityService>();
            services.AddSingleton<ISecurityService, FakeSecurityService>();

            services.RemoveAll<IStorageService>();
            services.AddSingleton<IStorageService, FakeStorageService>();

            services.RemoveAll<IPayoutProcessor>();
            services.AddSingleton<IPayoutProcessor, FakePayoutProcessor>();

            services.RemoveAll<IPaymentProviderFactory>();
            services.AddSingleton<IPaymentProviderFactory, FakePaymentProviderFactory>();

            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService, FakeEmailService>();

            services.RemoveAll<IRealtimeNotifier>();
            services.AddSingleton<IRealtimeNotifier, FakeRealtimeNotifier>();

            services.RemoveAll<IOpenPixApiClient>();
            services.AddSingleton<IOpenPixApiClient, FakeOpenPixApiClient>();

            services.RemoveAll<IBackgroundJobs>();
            services.AddSingleton<IBackgroundJobs, FakeBackgroundJobs>();

            services.RemoveAll<IStripeApiClient>();
            services.AddSingleton<IStripeApiClient, FakeStripeApiClient>();

            // Probe client real bate em URL externa (example.com) durante o registro.
            // Em testes substituímos pelo fake que sempre dá Success=true.
            services.RemoveAll<IWebhookProbeClient>();
            services.AddSingleton<IWebhookProbeClient, FakeWebhookProbeClient>();

            // In-memory distributed cache instead of Redis
            services.AddDistributedMemoryCache();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection?.Dispose();
    }
}

/// <summary>
/// In-memory idempotency service that exercises the middleware without requiring Redis.
/// Always returns "not processed" so each request proceeds normally.
/// </summary>
public class FakeIdempotencyService : IIdempotencyService
{
    public Task<IdempotencyResult> TryAcquireLockAsync(string idempotencyKey)
        => Task.FromResult(new IdempotencyResult(AlreadyProcessed: false, CachedResponse: null));

    public Task CompleteAsync(string idempotencyKey, string responseBody, int statusCode)
        => Task.CompletedTask;

    public Task ReleaseLockAsync(string idempotencyKey)
        => Task.CompletedTask;
}

public class FakeSecurityService : ISecurityService
{
    public Task<string> EncryptAsync(string plainText) => Task.FromResult($"enc:{plainText}");
    public Task<string> DecryptAsync(string encryptedText) =>
        Task.FromResult(encryptedText.StartsWith("enc:") ? encryptedText[4..] : encryptedText);
}

public class FakeStorageService : IStorageService
{
    public Task<string> UploadAsync(Stream fileStream, string fileName, string contentType) =>
        Task.FromResult($"https://fake-storage.test/{fileName}");
}

public class FakePayoutProcessor : IPayoutProcessor
{
    public Task<PayoutResult> ProcessAsync(Payout payout, Seller seller) =>
        Task.FromResult(new PayoutResult(true, TransactionId: $"fake-txn-{payout.Id:N}"));
}

public class FakePaymentProvider : IPaymentProvider
{
    public Task<GatewayPaymentDetails> ProcessPaymentAsync(Tenant tenant, Seller? seller, CreateTransactionDto request, decimal feeAmount, string? idempotencyKey = null, Guid? transactionId = null) =>
        Task.FromResult(new GatewayPaymentDetails(
            TransactionId: $"fake-provider-tx-{Guid.NewGuid():N}",
            PixQrCode: "fake-brcode",
            PixImageUrl: "https://fake.test/qr.png"));

    public Task<GatewaySubAccountDetails> CreateSubAccountAsync(Tenant tenant, CreateSellerDto request) =>
        Task.FromResult(new GatewaySubAccountDetails("fake-account-id", "fake-api-key"));

    public Task<string?> RefundAsync(Tenant tenant, Seller? seller, string providerTxId, decimal amountInReais, string? reason = null, string? idempotencyKey = null) =>
        Task.FromResult<string?>($"fake-refund-{Guid.NewGuid():N}");

    public Task CancelChargeAsync(Tenant tenant, Seller? seller, string providerTxId) =>
        Task.CompletedTask;

    public Task<AccountBalanceDetails> GetAccountBalanceAsync(Tenant tenant, Seller seller) =>
        Task.FromResult(new AccountBalanceDetails(1000m, 0m, 1000m, true));

    public Task<PixKeyDetails> ValidatePixKeyAsync(Tenant tenant, string pixKey) =>
        Task.FromResult(new PixKeyDetails(pixKey, "CPF", "Fake Owner", "12345678901"));

    public Task<List<StatementEntry>> GetStatementAsync(Tenant tenant, Seller seller, DateTime? start = null, DateTime? end = null) =>
        Task.FromResult(new List<StatementEntry>());
}

public class FakePaymentProviderFactory : IPaymentProviderFactory
{
    private readonly FakePaymentProvider _provider = new();
    public IPaymentProvider GetProvider(PaymentProvider providerType) => _provider;
}

public class FakeEmailService : IEmailService
{
    public List<EmailMessage> SentMessages { get; } = [];
    public Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        SentMessages.Add(message);
        return Task.FromResult(true);
    }
}

public class FakeRealtimeNotifier : IRealtimeNotifier
{
    public List<(Guid TenantId, string EventType, object Payload)> SentNotifications { get; } = [];
    public Task SendToTenantAsync(Guid tenantId, string eventType, object payload)
    {
        SentNotifications.Add((tenantId, eventType, payload));
        return Task.CompletedTask;
    }
}

public class FakeOpenPixApiClient : IOpenPixApiClient
{
    public Task<OpenPixChargeResponse> CreateChargeAsync(string appId, OpenPixChargeRequest request) =>
        Task.FromResult(new OpenPixChargeResponse(
            Charge: new OpenPixCharge(request.CorrelationId, "fake-tx-id", "fake-identifier", "ACTIVE",
                request.Value, "fake-brcode", "https://fake.test/qr.png", null, null,
                DateTime.UtcNow.ToString("o"), DateTime.UtcNow.ToString("o")),
            CorrelationId: request.CorrelationId,
            BrCode: "fake-brcode"));

    public Task<OpenPixChargeResponse> GetChargeAsync(string appId, string correlationId) =>
        Task.FromResult(new OpenPixChargeResponse(
            Charge: new OpenPixCharge(correlationId, "fake-tx-id", "fake-identifier", "ACTIVE",
                1000, "fake-brcode", null, null, null,
                DateTime.UtcNow.ToString("o"), DateTime.UtcNow.ToString("o")),
            CorrelationId: correlationId,
            BrCode: "fake-brcode"));

    public Task<OpenPixAccountRegisterResponse> CreateAccountRegisterAsync(string appId, OpenPixAccountRegisterRequest request) =>
        Task.FromResult(new OpenPixAccountRegisterResponse(
            request.OfficialName, request.TradeName, null, "PENDING", Guid.NewGuid().ToString()));

    public Task RegisterWebhookAsync(string appId, string webhookUrl, string name, string @event) =>
        Task.CompletedTask;

    public Task<OpenPixRefundResponse> CreateRefundAsync(string appId, OpenPixRefundRequest request) =>
        Task.FromResult(new OpenPixRefundResponse(new OpenPixRefund("IN_PROCESSING", request.CorrelationId, request.Value)));

    public Task<OpenPixWithdrawResponse> WithdrawFromAccountAsync(string appId, string accountId, OpenPixWithdrawRequest request) =>
        Task.FromResult(new OpenPixWithdrawResponse(new OpenPixWithdrawData(
            new OpenPixWithdrawAccount(accountId, 100000),
            new OpenPixWithdrawTransaction("fake-e2e", request.Value))));

    public Task DeleteChargeAsync(string appId, string correlationId) => Task.CompletedTask;

    public Task<OpenPixAccountResponse> GetAccountAsync(string appId, string accountId) =>
        Task.FromResult(new OpenPixAccountResponse(new OpenPixAccountData(accountId, true,
            new OpenPixAccountBalance(100000, 0, 100000))));

    public Task<OpenPixPixKeyCheckResponse> CheckPixKeyAsync(string appId, string pixKey) =>
        Task.FromResult(new OpenPixPixKeyCheckResponse(new OpenPixPixKeyData(pixKey, "CPF", "Fake Owner")));

    public Task<OpenPixPaymentResponse> CreatePaymentAsync(string appId, OpenPixPaymentRequest request) =>
        Task.FromResult(new OpenPixPaymentResponse(new OpenPixPaymentData(
            request.Value, "CONFIRMED", request.DestinationAlias, request.Comment, request.CorrelationId, "fake-tx-id")));

    public Task<OpenPixPaymentResponse> GetPaymentAsync(string appId, string correlationId) =>
        Task.FromResult(new OpenPixPaymentResponse(new OpenPixPaymentData(
            1000, "CONFIRMED", "fake-key", null, correlationId, "fake-tx-id")));

    public Task<OpenPixStatementResponse> GetStatementAsync(string appId, DateTime? start = null, DateTime? end = null) =>
        Task.FromResult(new OpenPixStatementResponse([]));

    public Task<OpenPixRefundDetailResponse> GetRefundAsync(string appId, string correlationId) =>
        Task.FromResult(new OpenPixRefundDetailResponse(new OpenPixRefundDetail("CONFIRMED", 1000, correlationId)));

    public Task<OpenPixRefundListResponse> ListRefundsAsync(string appId) =>
        Task.FromResult(new OpenPixRefundListResponse([]));

    public Task<OpenPixStaticQrResponse> CreateStaticQrAsync(string appId, OpenPixStaticQrRequest request) =>
        Task.FromResult(new OpenPixStaticQrResponse(
            new OpenPixStaticQrData(request.Name, request.CorrelationId, "fake-id", "fake-brcode", "https://fake.test/qr.png"),
            "fake-brcode"));

    public Task<OpenPixStaticQrListResponse> ListStaticQrAsync(string appId) =>
        Task.FromResult(new OpenPixStaticQrListResponse([]));

    public Task<OpenPixStaticQrResponse> GetStaticQrAsync(string appId, string id) =>
        Task.FromResult(new OpenPixStaticQrResponse(
            new OpenPixStaticQrData("Fake QR", "fake-corr", id, "fake-brcode", "https://fake.test/qr.png")));

    public Task DeleteStaticQrAsync(string appId, string id) => Task.CompletedTask;

    public Task<byte[]> GetReceiptAsync(string appId, string receiptType, string endToEndId) =>
        Task.FromResult(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF header

    public Task<OpenPixWebhookListResponse> ListWebhooksAsync(string appId) =>
        Task.FromResult(new OpenPixWebhookListResponse([]));

    public Task DeleteWebhookAsync(string appId, string webhookId) => Task.CompletedTask;

    // Charge update
    public Task<OpenPixChargePatchResponse> UpdateChargeExpirationAsync(string appId, string correlationId, OpenPixChargePatchRequest request) =>
        Task.FromResult(new OpenPixChargePatchResponse(new OpenPixCharge(
            correlationId, "fake-tx-id", "fake-identifier", "ACTIVE", 1000, "fake-brcode", null, null,
            request.ExpiresDate, DateTime.UtcNow.ToString("o"), DateTime.UtcNow.ToString("o"))));

    // Payment approve
    public Task<OpenPixPaymentApproveResponse> ApprovePaymentAsync(string appId, OpenPixPaymentApproveRequest request) =>
        Task.FromResult(new OpenPixPaymentApproveResponse(new OpenPixPaymentData(
            1000, "CONFIRMED", "fake-key", null, request.CorrelationId, "fake-tx-id")));

    // Transfer
    public Task<OpenPixTransferResponse> CreateTransferAsync(string appId, OpenPixTransferRequest request) =>
        Task.FromResult(new OpenPixTransferResponse(new OpenPixTransferTransaction(
            request.Value, DateTime.UtcNow.ToString("o"), request.CorrelationId ?? Guid.NewGuid().ToString())));

    // Pix Key management
    public Task<OpenPixPixKeyListResponse> ListPixKeysAsync(string appId) =>
        Task.FromResult(new OpenPixPixKeyListResponse([
            new OpenPixPixKeyResponse("12345678901", "CPF", true),
            new OpenPixPixKeyResponse("fake@test.com", "EMAIL")
        ]));

    public Task<OpenPixPixKeyResponse> CreatePixKeyAsync(string appId, OpenPixPixKeyCreateRequest request) =>
        Task.FromResult(new OpenPixPixKeyResponse(request.Key, request.Type));

    public Task DeletePixKeyAsync(string appId, string pixKey) => Task.CompletedTask;

    public Task<OpenPixPixKeyResponse> SetDefaultPixKeyAsync(string appId, string pixKey) =>
        Task.FromResult(new OpenPixPixKeyResponse(pixKey, "CPF", true));

    // Subaccount management
    public Task<OpenPixSubAccountResponse> CreateSubAccountAsync(string appId, OpenPixSubAccountRequest request) =>
        Task.FromResult(new OpenPixSubAccountResponse(new OpenPixSubAccountData(request.Name, request.PixKey)));

    public Task<OpenPixSubAccountResponse> GetSubAccountAsync(string appId, string pixKeyOrId) =>
        Task.FromResult(new OpenPixSubAccountResponse(new OpenPixSubAccountData("Fake SubAccount", pixKeyOrId, 100000)));

    public Task<OpenPixSubAccountListResponse> ListSubAccountsAsync(string appId) =>
        Task.FromResult(new OpenPixSubAccountListResponse([
            new OpenPixSubAccountData("Sub 1", "sub1@test.com", 50000),
            new OpenPixSubAccountData("Sub 2", "sub2@test.com", 75000)
        ]));

    public Task DeleteSubAccountAsync(string appId, string pixKeyOrId) => Task.CompletedTask;

    public Task<OpenPixSubAccountCreditDebitResponse> CreditSubAccountAsync(string appId, string pixKeyOrId, OpenPixSubAccountCreditDebitRequest request) =>
        Task.FromResult(new OpenPixSubAccountCreditDebitResponse(pixKeyOrId, request.Value, request.Description, $"Sub-account successfully credited, {request.Value}"));

    public Task<OpenPixSubAccountCreditDebitResponse> DebitSubAccountAsync(string appId, string pixKeyOrId, OpenPixSubAccountCreditDebitRequest request) =>
        Task.FromResult(new OpenPixSubAccountCreditDebitResponse(pixKeyOrId, request.Value, request.Description, $"Sub-account successfully debited, {request.Value}"));

    public Task<OpenPixSubAccountTransferResponse> TransferBetweenSubAccountsAsync(string appId, OpenPixSubAccountTransferRequest request) =>
        Task.FromResult(new OpenPixSubAccountTransferResponse(
            request.Value,
            new OpenPixSubAccountSummary("Dest", request.ToPixKey, 100000),
            new OpenPixSubAccountSummary("Origin", request.FromPixKey, 50000)));

    public Task<OpenPixSubAccountWithdrawResponse> WithdrawFromSubAccountAsync(string appId, string pixKeyOrId, OpenPixSubAccountWithdrawRequest request) =>
        Task.FromResult(new OpenPixSubAccountWithdrawResponse(
            new OpenPixSubAccountWithdrawData(new OpenPixSubAccountWithdrawTransaction("CREATED", request.Value))));

    public Task<List<OpenPixSubAccountStatementEntry>> GetSubAccountStatementAsync(string appId, string pixKeyOrId, DateTime? start = null, DateTime? end = null, int skip = 0, int limit = 20) =>
        Task.FromResult(new List<OpenPixSubAccountStatementEntry>
        {
            new(Id: "stmt-1", Time: DateTime.UtcNow.ToString("o"), Description: "Credit", Balance: 100000, Value: 5000, Type: "CREDIT", OperationType: "TRANSFER")
        });
}

public class FakeWebhookProbeClient : IWebhookProbeClient
{
    public Task<WebhookTestResultDto> ProbeAsync(string url, string secret, string eventType, CancellationToken ct = default) =>
        Task.FromResult(new WebhookTestResultDto(
            Success: true,
            StatusCode: 200,
            LatencyMs: 12,
            ResponseBody: "{\"ok\":true}",
            Error: null));
}

public class FakeBackgroundJobs : IBackgroundJobs
{
    public List<string> EnqueuedTypes { get; } = [];

    public void Enqueue<T>(System.Linq.Expressions.Expression<Func<T, Task>> methodCall)
    {
        EnqueuedTypes.Add(typeof(T).Name);
    }
}

public class FakeStripeApiClient : IStripeApiClient
{
    private readonly List<StripeApplePayDomainResponse> _domains = [];

    public Task<StripePaymentIntentResponse> CreatePaymentIntentAsync(string apiKey, StripePaymentIntentRequest request, string? idempotencyKey = null, string? connectedAccountId = null) =>
        Task.FromResult(new StripePaymentIntentResponse($"pi_{Guid.NewGuid():N}", "requires_payment_method", $"secret_{Guid.NewGuid():N}", request.Amount));

    public Task<StripeCustomerResponse> CreateCustomerAsync(string apiKey, StripeCustomerRequest request, string? connectedAccountId = null) =>
        Task.FromResult(new StripeCustomerResponse($"cus_{Guid.NewGuid():N}", request.Email));

    public Task<StripeCustomerResponse?> FindCustomerByEmailAsync(string apiKey, string email, string? connectedAccountId = null) =>
        Task.FromResult<StripeCustomerResponse?>(null);

    public Task<string> EnsureCustomerAsync(string apiKey, string name, string email, string? connectedAccountId = null) =>
        Task.FromResult($"cus_{Guid.NewGuid():N}");

    public Task<StripeRefundResponse> CreateRefundAsync(string apiKey, StripeRefundRequest request, string? idempotencyKey = null, string? connectedAccountId = null) =>
        Task.FromResult(new StripeRefundResponse($"re_{Guid.NewGuid():N}", "succeeded"));

    public Task<StripeConnectedAccountResponse> CreateConnectedAccountAsync(string apiKey, StripeConnectedAccountRequest request, string? idempotencyKey = null) =>
        Task.FromResult(new StripeConnectedAccountResponse($"acct_{Guid.NewGuid():N}", "custom"));

    public Task<StripeApplePayDomainResponse> RegisterApplePayDomainAsync(string apiKey, string domainName)
    {
        var response = new StripeApplePayDomainResponse($"apwc_{Guid.NewGuid():N}", domainName);
        _domains.Add(response);
        return Task.FromResult(response);
    }

    public Task<List<StripeApplePayDomainResponse>> ListApplePayDomainsAsync(string apiKey) =>
        Task.FromResult(_domains.ToList());

    public Task DeleteApplePayDomainAsync(string apiKey, string domainId)
    {
        _domains.RemoveAll(d => d.Id == domainId);
        return Task.CompletedTask;
    }

    public Task<StripePaymentMethodResponse> CreateBoletoPaymentMethodAsync(
        string apiKey, string taxId, string name, string email,
        string? addressLine1 = null, string? city = null, string? state = null,
        string? postalCode = null, string country = "BR", string? idempotencyKey = null, string? connectedAccountId = null) =>
        Task.FromResult(new StripePaymentMethodResponse($"pm_{Guid.NewGuid():N}", "boleto"));

    public Task<StripeConfirmIntentResponse> ConfirmPaymentIntentAsync(string apiKey, string paymentIntentId, string paymentMethodId, string? idempotencyKey = null, string? connectedAccountId = null) =>
        Task.FromResult(new StripeConfirmIntentResponse(paymentIntentId, "requires_action"));

    public Task<StripePersonResponse> CreatePersonAsync(string apiKey, string accountId, StripePersonRequest request, string? idempotencyKey = null) =>
        Task.FromResult(new StripePersonResponse($"person_{Guid.NewGuid():N}", request.FirstName, request.LastName));

    public Task<StripeBankAccountResponse> CreateBankAccountAsync(string apiKey, string accountId, StripeBankAccountRequest request, string? idempotencyKey = null) =>
        Task.FromResult(new StripeBankAccountResponse($"ba_{Guid.NewGuid():N}", "Fake Bank", "1234"));

    public Task<StripeBalanceResponse> GetBalanceAsync(string apiKey, string? connectedAccountId = null) =>
        Task.FromResult(new StripeBalanceResponse(
            [new StripeBalanceAmount(100000, "brl")],
            [new StripeBalanceAmount(50000, "brl")]));

    public Task<StripeChargeListResponse> ListChargesAsync(string apiKey, long? createdGte = null, long? createdLte = null, int limit = 100, string? connectedAccountId = null) =>
        Task.FromResult(new StripeChargeListResponse([]));

    public Task<StripePaymentIntentDetailResponse> GetPaymentIntentAsync(string apiKey, string paymentIntentId, string? connectedAccountId = null) =>
        Task.FromResult(new StripePaymentIntentDetailResponse(paymentIntentId, "succeeded", 1000, 1000));

    public Task<StripeTransferListResponse> ListTransfersAsync(string apiKey, long? createdGte = null, long? createdLte = null, string? destination = null, int limit = 100) =>
        Task.FromResult(new StripeTransferListResponse([]));

    public Task<StripePayoutListResponse> ListPayoutsAsync(string apiKey, long? createdGte = null, long? createdLte = null, string? status = null, int limit = 100) =>
        Task.FromResult(new StripePayoutListResponse([]));

    public Task CancelPaymentIntentAsync(string apiKey, string paymentIntentId, string? connectedAccountId = null) =>
        Task.CompletedTask;

    public Task<StripeBalanceTransactionListResponse> ListBalanceTransactionsAsync(string apiKey, long? createdGte = null, long? createdLte = null, string? type = null, int limit = 100, string? connectedAccountId = null) =>
        Task.FromResult(new StripeBalanceTransactionListResponse());

    public Task<StripeConnectedAccountResponse?> FindActiveAccountByDocumentAsync(string apiKey, string tenantId, string document, int maxScan = 200) =>
        Task.FromResult<StripeConnectedAccountResponse?>(null);

    public Task<StripePayoutItem> CreatePayoutAsync(string apiKey, string connectedAccountId, long amountInCents, string currency, string? idempotencyKey = null, Dictionary<string, string>? metadata = null) =>
        Task.FromResult(new StripePayoutItem($"po_{Guid.NewGuid():N}", amountInCents, currency, "pending"));

    public Task<StripePayoutItem> GetPayoutAsync(string apiKey, string connectedAccountId, string payoutId) =>
        Task.FromResult(new StripePayoutItem(payoutId, 0, "brl", "pending"));

    public Task<StripePayoutItem> CancelPayoutAsync(string apiKey, string connectedAccountId, string payoutId) =>
        Task.FromResult(new StripePayoutItem(payoutId, 0, "brl", "canceled"));
}
