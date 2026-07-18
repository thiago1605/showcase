using FellowCore.Application.Common.Utils;
using FellowCore.Application.Modules.Auth.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Infrastructure.Database;
using Microsoft.Extensions.DependencyInjection;

namespace FellowCore.Integration.Tests.Fixtures;

public static class TestDataHelper
{
    public const string TestApiKey = "pk_test_integration_key_123456";
    public const string TestUserEmail = "admin@test.com";
    public const string TestUserPassword = "StrongP@ss123!";

    public static async Task<(Tenant Tenant, Seller Seller)> SeedTenantAndSellerAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = Tenant.Create(
            name: "Test Tenant",
            slug: "test-tenant",
            apiKeyHash: CryptoUtils.GenerateSha256Hash(TestApiKey),
            apiKeyPrefix: TestApiKey[..12],
            apiSecretHash: "test-secret-hash");

        var seller = Seller.Create(
            tenantId: tenant.Id,
            legalName: "Test Seller LTDA",
            document: "12345678000199",
            email: "seller@test.com",
            webhookSecret: "test-webhook-secret-32chars-ok!!",
            preferredProvider: PaymentProvider.STRIPE,
            externalAccountId: "acc-test-123456",
            tradeName: "Test Seller",
            pixKey: "12345678000199");

        var config = tenant.CreateDefaultConfig();

        var wallet = LedgerAccount.Create(tenant.Id, seller.Id, LedgerAccountType.WALLET);
        _ = wallet.Credit(50000m, "Saldo inicial para testes", "SEED", "seed-001");

        db.Tenants.Add(tenant);
        db.TenantConfigs.Add(config);
        db.Sellers.Add(seller);
        db.LedgerAccounts.Add(wallet);
        await db.SaveChangesAsync();

        return (tenant, seller);
    }

    public static async Task<Customer> SeedCustomerAsync(IServiceProvider services, Guid tenantId)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = Customer.Create(tenantId, "John Doe", "john@test.com", "12345678901");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        return customer;
    }

    public static async Task<User> SeedUserAsync(IServiceProvider services, Guid tenantId, UserRole role = UserRole.OWNER)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = User.Create(
            name: "Test Admin",
            email: TestUserEmail,
            passwordHash: hasher.Hash(TestUserPassword),
            role: role,
            tenantId: tenantId);

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<Payout> SeedPayoutAsync(IServiceProvider services, Guid tenantId, Guid sellerId, decimal amount = 500m)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = Payout.Create(tenantId, sellerId, amount, fee: 5m);
        var payout = result.Value;
        db.Payouts.Add(payout);
        await db.SaveChangesAsync();

        return payout;
    }
}
