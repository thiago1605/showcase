using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FellowCore.Application.Common.Utils;
using FellowCore.Application.Modules.Payouts.DTOs;
using FellowCore.Application.Modules.Transactions.DTOs;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Infrastructure.Database;
using FellowCore.Integration.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FellowCore.Integration.Tests;

/// <summary>
/// Audit gap integration tests T1-T6.
/// Each test class gets its own <see cref="CustomWebApplicationFactory"/> and SQLite database
/// via <see cref="IntegrationTestBase"/>.
/// </summary>
public class AuditGapTests : IntegrationTestBase
{
    private const string StripeWebhookSecret = "whsec_test_secret_for_integration_tests";

    // -----------------------------------------------------------------------
    // T1: Webhook-to-ledger refund test
    // -----------------------------------------------------------------------

    [Fact]
    public async Task T1_StripeChargeRefunded_DebitsSellerLedger()
    {
        // Arrange: seed a CAPTURED transaction with seller and WALLET balance
        var providerTxId = $"pi_refund_test_{Guid.NewGuid():N}";
        decimal txAmount = 200m;
        decimal feeAmount = 4m;
        decimal netAmount = 196m;

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var txResult = Transaction.Create(
                tenantId: TenantId,
                amount: txAmount,
                paymentType: PaymentType.CREDIT_CARD,
                provider: PaymentProvider.STRIPE,
                installments: 1,
                feeAmount: feeAmount,
                netAmount: netAmount,
                expectedSettlementDate: null,
                providerTxId: providerTxId,
                sellerId: SellerId);

            db.Transactions.Add(txResult.Value);
            await db.SaveChangesAsync();

            // Set transaction to CAPTURED via ExecuteUpdate (same pattern as webhook handler)
            await db.Transactions
                .Where(t => t.ProviderTxId == providerTxId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TransactionStatus.CAPTURED));
        }

        // Get wallet balance before refund
        decimal walletBefore;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = await db.LedgerAccounts.AsNoTracking()
                .FirstAsync(a => a.TenantId == TenantId && a.SellerId == SellerId && a.Type == LedgerAccountType.WALLET);
            walletBefore = wallet.Balance;
        }

        // Act: send charge.refunded webhook (full refund)
        long amountCents = (long)(txAmount * 100);
        var webhookPayload = new
        {
            id = "evt_refund_test_001",
            type = "charge.refunded",
            data = new
            {
                @object = new
                {
                    id = $"ch_refund_{Guid.NewGuid():N}",
                    payment_intent = providerTxId,
                    amount = amountCents,
                    amount_refunded = amountCents,
                    currency = "brl"
                }
            }
        };

        var response = await SendStripeWebhookAsync(webhookPayload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Transaction should be REFUNDED (full refund)
            var tx = await db.Transactions.AsNoTracking().FirstAsync(t => t.ProviderTxId == providerTxId);
            tx.Status.Should().Be(TransactionStatus.REFUNDED);
            tx.RefundedAmount.Should().Be(txAmount);

            // Wallet balance should be debited by the GROSS refund amount.
            // Política unificada (vide RefundCalculator.Calculate): seller paga o valor
            // TOTAL que o cliente recebe de volta, não proporcional ao net. Margin/fee
            // ficam com a plataforma pra cobrir custo Stripe que não devolve a taxa.
            var wallet = await db.LedgerAccounts.AsNoTracking()
                .FirstAsync(a => a.TenantId == TenantId && a.SellerId == SellerId && a.Type == LedgerAccountType.WALLET);
            decimal expectedDebit = txAmount; // gross integral
            wallet.Balance.Should().BeLessThan(walletBefore);
            wallet.Balance.Should().Be(walletBefore - expectedDebit,
                "política unificada: refund debita o gross do seller (RefundCalculator.SellerTotalDebit = refundAmount)");
        }
    }

    // -----------------------------------------------------------------------
    // T2: Dispute flow test (open + close/won + close/lost)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task T2_DisputeCreated_SetsChargebackError()
    {
        // Arrange: seed CAPTURED transaction
        var providerTxId = $"pi_dispute_won_{Guid.NewGuid():N}";
        await SeedCapturedStripeTransactionAsync(providerTxId, 300m, 6m, 294m);

        // Act: send dispute.created webhook
        var disputePayload = BuildDisputePayload("charge.dispute.created", providerTxId, 30000, "needs_response", "dp_test_won_001");
        var response = await SendStripeWebhookAsync(disputePayload);

        // Assert: transaction becomes CHARGEBACKERROR
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tx = await db.Transactions.AsNoTracking().FirstAsync(t => t.ProviderTxId == providerTxId);
        tx.Status.Should().Be(TransactionStatus.CHARGEBACKERROR);

        // Dispute entity should be created
        var dispute = await db.Disputes.AsNoTracking().FirstOrDefaultAsync(d => d.ExternalDisputeId == "dp_test_won_001");
        dispute.Should().NotBeNull();
        dispute!.Status.Should().Be(DisputeStatus.OPEN);
    }

    [Fact]
    public async Task T2_DisputeClosedWon_RevertsToCapture()
    {
        // Arrange: seed CAPTURED transaction, then open a dispute
        var providerTxId = $"pi_dispute_won2_{Guid.NewGuid():N}";
        await SeedCapturedStripeTransactionAsync(providerTxId, 300m, 6m, 294m);

        // Open dispute
        var createPayload = BuildDisputePayload("charge.dispute.created", providerTxId, 30000, "needs_response", "dp_test_won_002");
        await SendStripeWebhookAsync(createPayload);

        // Act: close dispute as WON
        var closePayload = BuildDisputePayload("charge.dispute.closed", providerTxId, 30000, "won", "dp_test_won_002");
        var response = await SendStripeWebhookAsync(closePayload);

        // Assert: transaction reverts to CAPTURED
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tx = await db.Transactions.AsNoTracking().FirstAsync(t => t.ProviderTxId == providerTxId);
        tx.Status.Should().Be(TransactionStatus.CAPTURED);

        // Dispute entity status should be WON
        var dispute = await db.Disputes.AsNoTracking().FirstOrDefaultAsync(d => d.ExternalDisputeId == "dp_test_won_002");
        dispute.Should().NotBeNull();
        dispute!.Status.Should().Be(DisputeStatus.WON);
    }

    [Fact]
    public async Task T2_DisputeClosedLost_StaysChargebackError()
    {
        // Arrange: seed CAPTURED transaction, then open a dispute
        var providerTxId = $"pi_dispute_lost_{Guid.NewGuid():N}";
        await SeedCapturedStripeTransactionAsync(providerTxId, 500m, 10m, 490m);

        // Open dispute
        var createPayload = BuildDisputePayload("charge.dispute.created", providerTxId, 50000, "needs_response", "dp_test_lost_001");
        await SendStripeWebhookAsync(createPayload);

        // Act: close dispute as LOST
        var closePayload = BuildDisputePayload("charge.dispute.closed", providerTxId, 50000, "lost", "dp_test_lost_001");
        var response = await SendStripeWebhookAsync(closePayload);

        // Assert: transaction remains CHARGEBACKERROR
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tx = await db.Transactions.AsNoTracking().FirstAsync(t => t.ProviderTxId == providerTxId);
        tx.Status.Should().Be(TransactionStatus.CHARGEBACKERROR);

        // Dispute entity status should be LOST
        var dispute = await db.Disputes.AsNoTracking().FirstOrDefaultAsync(d => d.ExternalDisputeId == "dp_test_lost_001");
        dispute.Should().NotBeNull();
        dispute!.Status.Should().Be(DisputeStatus.LOST);
    }

    // -----------------------------------------------------------------------
    // T3: Multi-tenant data isolation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task T3_TenantA_CannotAccessTenantB_Transactions()
    {
        // Arrange: Create a second tenant with its own API key
        const string secondApiKey = "pk_test_second_tenant_key_12345";
        Guid tenantBId;
        Guid sellerBId;

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var tenantB = Tenant.Create(
                name: "Tenant B",
                slug: "tenant-b",
                apiKeyHash: CryptoUtils.GenerateSha256Hash(secondApiKey),
                apiKeyPrefix: secondApiKey[..12],
                apiSecretHash: "test-secret-hash-b");

            var configB = tenantB.CreateDefaultConfig();

            var sellerB = Seller.Create(
                tenantId: tenantB.Id,
                legalName: "Seller B LTDA",
                document: "98765432000100",
                email: "seller-b@test.com",
                webhookSecret: "test-webhook-secret-32chars-ok!!",
                preferredProvider: PaymentProvider.STRIPE,
                externalAccountId: "acc-test-b-123456",
                tradeName: "Seller B",
                pixKey: "98765432000100");

            var walletB = LedgerAccount.Create(tenantB.Id, sellerB.Id, LedgerAccountType.WALLET);
            _ = walletB.Credit(30000m, "Saldo inicial Tenant B", "SEED", "seed-b-001");

            db.Tenants.Add(tenantB);
            db.TenantConfigs.Add(configB);
            db.Sellers.Add(sellerB);
            db.LedgerAccounts.Add(walletB);
            await db.SaveChangesAsync();

            tenantBId = tenantB.Id;
            sellerBId = sellerB.Id;
        }

        // Create a transaction for Tenant A via API
        var payerA = new PayerDto("Alice", "11111111111", "alice@test.com");
        var dtoA = new CreateTransactionDto(SellerId, 100m, PaymentType.PIX, 1, "TX Tenant A", payerA);
        var reqA = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        reqA.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        reqA.Content = JsonContent.Create(dtoA);
        var resA = await Client.SendAsync(reqA);
        resA.StatusCode.Should().Be(HttpStatusCode.Created);

        var jsonA = await resA.Content.ReadFromJsonAsync<JsonElement>();
        var txAId = jsonA.GetProperty("data").GetProperty("internalId").GetString();

        // Create a transaction for Tenant B directly in DB
        Guid txBId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var txResult = Transaction.Create(
                tenantId: tenantBId,
                amount: 200m,
                paymentType: PaymentType.PIX,
                provider: PaymentProvider.STRIPE,
                installments: 1,
                feeAmount: 3m,
                netAmount: 197m,
                expectedSettlementDate: null,
                providerTxId: $"pi_tenant_b_{Guid.NewGuid():N}",
                sellerId: sellerBId,
                description: "TX Tenant B");

            db.Transactions.Add(txResult.Value);
            await db.SaveChangesAsync();
            txBId = txResult.Value.Id;
        }

        // Act: Tenant A tries to list transactions — should NOT see Tenant B's transaction
        var listResponse = await Client.GetAsync("/api/v1/transactions?page=1&pageSize=100");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var items = listJson.GetProperty("data").GetProperty("items");
        var txAIds = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            txAIds.Add(item.GetProperty("id").GetString()!);
        }

        txAIds.Should().Contain(txAId);
        txAIds.Should().NotContain(txBId.ToString());

        // Act: Tenant A tries to GET Tenant B's transaction directly
        var getResponse = await Client.GetAsync($"/api/v1/transactions/{txBId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Act: Tenant B authenticates and lists — should see only its own transactions
        var clientB = CreateUnauthenticatedClient();
        clientB.DefaultRequestHeaders.Add("X-Api-Key", secondApiKey);

        var listBResponse = await clientB.GetAsync("/api/v1/transactions?page=1&pageSize=100");
        listBResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listBJson = await listBResponse.Content.ReadFromJsonAsync<JsonElement>();
        var itemsB = listBJson.GetProperty("data").GetProperty("items");
        var txBIds = new List<string>();
        foreach (var item in itemsB.EnumerateArray())
        {
            txBIds.Add(item.GetProperty("id").GetString()!);
        }

        txBIds.Should().Contain(txBId.ToString());
        txBIds.Should().NotContain(txAId);
    }

    // -----------------------------------------------------------------------
    // T4: Redis failure graceful degradation (idempotency with in-memory cache)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task T4_IdempotencyKey_GracefulDegradation_WhenCacheUnavailable()
    {
        // In the test environment, Redis is unavailable. The IdempotencyMiddleware catches
        // the exception and degrades gracefully — allowing the request to proceed without dedup.
        // This test verifies that the endpoint works correctly with the Idempotency-Key header
        // even when the cache backend is down.
        var payer = new PayerDto("Maria", "12345678901", "maria@test.com");
        var dto = new CreateTransactionDto(SellerId, 50m, PaymentType.PIX, 1, "Idempotency test", payer);

        // Request with Idempotency-Key — should succeed (graceful degradation)
        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        request1.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request1.Content = JsonContent.Create(dto);
        var response1 = await Client.SendAsync(request1);

        response1.StatusCode.Should().Be(HttpStatusCode.Created);

        var json1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        json1.GetProperty("success").GetBoolean().Should().BeTrue();
        json1.GetProperty("data").GetProperty("internalId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task T4_PostEndpoint_Rejects400_WhenIdempotencyKeyMissing()
    {
        // The IdempotencyMiddleware is now enabled in Testing environment.
        // POST requests to /api/v1/* without the Idempotency-Key header must be rejected with 400.
        // Note: We bypass the AutoIdempotencyKeyHandler by creating a raw client.
        using var rawClient = Factory.CreateClient();
        rawClient.DefaultRequestHeaders.Add("X-Api-Key", Fixtures.TestDataHelper.TestApiKey);

        var payer = new PayerDto("Maria", "12345678901", "maria@test.com");
        var dto = new CreateTransactionDto(SellerId, 50m, PaymentType.PIX, 1, "No idempotency key", payer);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        // Intentionally NOT adding Idempotency-Key header
        request.Content = JsonContent.Create(dto);
        var response = await rawClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task T4_DifferentIdempotencyKeys_CreateSeparateTransactions()
    {
        var payer = new PayerDto("Carlos", "98765432100", "carlos@test.com");
        var dto = new CreateTransactionDto(SellerId, 75m, PaymentType.PIX, 1, "Different idempotency", payer);

        // First request
        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        request1.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request1.Content = JsonContent.Create(dto);
        var response1 = await Client.SendAsync(request1);
        response1.StatusCode.Should().Be(HttpStatusCode.Created);
        var json1 = await response1.Content.ReadFromJsonAsync<JsonElement>();
        var txId1 = json1.GetProperty("data").GetProperty("internalId").GetString();

        // Second request with different key
        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transactions");
        request2.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request2.Content = JsonContent.Create(dto);
        var response2 = await Client.SendAsync(request2);
        response2.StatusCode.Should().Be(HttpStatusCode.Created);
        var json2 = await response2.Content.ReadFromJsonAsync<JsonElement>();
        var txId2 = json2.GetProperty("data").GetProperty("internalId").GetString();

        txId1.Should().NotBe(txId2);
    }

    // -----------------------------------------------------------------------
    // T5: Concurrent PaymentIntent capture (collision guard)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task T5_SecondCapture_OnSamePaymentIntent_DoesNotCreditLedger()
    {
        // Arrange: create a PaymentIntent and two PROCESSING transactions pointing to it.
        // Both transactions use CREDIT_CARD + STRIPE so the StripeCardRail is resolved.
        // StripeCardRail.CaptureAccountType is FUTURE_RECEIVABLES, so we check that account.
        var providerTxId1 = $"pi_collision_tx1_{Guid.NewGuid():N}";
        var providerTxId2 = $"pi_collision_tx2_{Guid.NewGuid():N}";
        Guid intentId;
        Guid tx1Id;
        Guid tx2Id;

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Create PaymentIntent
            var intent = PaymentIntent.Create(
                tenantId: TenantId,
                externalReferenceId: $"order-collision-{Guid.NewGuid():N}",
                amount: 100m,
                sellerId: SellerId);
            db.PaymentIntents.Add(intent);
            intentId = intent.Id;

            // TX1 — PROCESSING, linked to PaymentIntent (CREDIT_CARD + STRIPE)
            var tx1Result = Transaction.Create(
                tenantId: TenantId,
                amount: 100m,
                paymentType: PaymentType.CREDIT_CARD,
                provider: PaymentProvider.STRIPE,
                installments: 1,
                feeAmount: 2m,
                netAmount: 98m,
                expectedSettlementDate: null,
                providerTxId: providerTxId1,
                sellerId: SellerId,
                externalReferenceId: intent.ExternalReferenceId);

            // TX2 — PROCESSING, linked to same PaymentIntent (simulates multi-method: also CREDIT_CARD + STRIPE)
            var tx2Result = Transaction.Create(
                tenantId: TenantId,
                amount: 100m,
                paymentType: PaymentType.CREDIT_CARD,
                provider: PaymentProvider.STRIPE,
                installments: 1,
                feeAmount: 2m,
                netAmount: 98m,
                expectedSettlementDate: null,
                providerTxId: providerTxId2,
                sellerId: SellerId,
                externalReferenceId: intent.ExternalReferenceId);

            var tx1 = tx1Result.Value;
            var tx2 = tx2Result.Value;

            tx1.SetPaymentIntentId(intentId);
            tx2.SetPaymentIntentId(intentId);

            db.Transactions.Add(tx1);
            db.Transactions.Add(tx2);
            await db.SaveChangesAsync();

            tx1Id = tx1.Id;
            tx2Id = tx2.Id;
        }

        // Snapshot: count total ledger entries before any capture (we'll compare counts after)
        int totalEntriesBefore;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            totalEntriesBefore = await db.LedgerEntries.CountAsync();
        }

        // Act 1: Send CAPTURED webhook for TX1 — should succeed and credit FUTURE_RECEIVABLES
        var payload1 = new
        {
            id = "evt_collision_1",
            type = "payment_intent.succeeded",
            data = new
            {
                @object = new
                {
                    id = providerTxId1,
                    status = "succeeded",
                    amount = 10000L
                }
            }
        };
        var response1 = await SendStripeWebhookAsync(payload1);
        response1.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify TX1 is CAPTURED and FUTURE_RECEIVABLES was credited
        decimal frBalanceAfterFirst;
        int frEntriesAfterFirst;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var tx1 = await db.Transactions.AsNoTracking().FirstAsync(t => t.Id == tx1Id);
            tx1.Status.Should().Be(TransactionStatus.CAPTURED);

            // FUTURE_RECEIVABLES should have been created and credited
            var frAccount = await db.LedgerAccounts.AsNoTracking()
                .FirstOrDefaultAsync(a => a.TenantId == TenantId && a.SellerId == SellerId
                    && a.Type == LedgerAccountType.FUTURE_RECEIVABLES);
            frAccount.Should().NotBeNull("RecordIncomingFundsAsync should have created the FUTURE_RECEIVABLES account");
            frBalanceAfterFirst = frAccount!.Balance;
            frBalanceAfterFirst.Should().BeGreaterThan(0, "first CAPTURED should credit FUTURE_RECEIVABLES");

            frEntriesAfterFirst = await db.LedgerEntries.CountAsync();
            frEntriesAfterFirst.Should().BeGreaterThan(totalEntriesBefore, "ledger entries should have been created");

            // PaymentIntent should be CAPTURED with TX1
            var intent = await db.PaymentIntents.AsNoTracking().FirstAsync(pi => pi.Id == intentId);
            intent.CapturedTransactionId.Should().Be(tx1Id);
        }

        // Act 2: Send CAPTURED webhook for TX2 — collision guard should prevent ledger credit
        var payload2 = new
        {
            id = "evt_collision_2",
            type = "payment_intent.succeeded",
            data = new
            {
                @object = new
                {
                    id = providerTxId2,
                    status = "succeeded",
                    amount = 10000L
                }
            }
        };
        var response2 = await SendStripeWebhookAsync(payload2);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert: TX2 is CAPTURED (status changed) but FUTURE_RECEIVABLES was NOT credited again
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var tx2 = await db.Transactions.AsNoTracking().FirstAsync(t => t.Id == tx2Id);
            tx2.Status.Should().Be(TransactionStatus.CAPTURED);

            // FUTURE_RECEIVABLES balance and entry count should NOT have changed
            var frAccount = await db.LedgerAccounts.AsNoTracking()
                .FirstAsync(a => a.TenantId == TenantId && a.SellerId == SellerId
                    && a.Type == LedgerAccountType.FUTURE_RECEIVABLES);
            frAccount.Balance.Should().Be(frBalanceAfterFirst,
                "the collision guard should prevent double-crediting the ledger");

            int frEntriesAfterSecond = await db.LedgerEntries.CountAsync();
            frEntriesAfterSecond.Should().Be(frEntriesAfterFirst,
                "no new ledger entries should be created for the collision loser");

            // PaymentIntent should still point to TX1
            var intent = await db.PaymentIntents.AsNoTracking().FirstAsync(pi => pi.Id == intentId);
            intent.CapturedTransactionId.Should().Be(tx1Id,
                "the PaymentIntent should remain captured by the first transaction");
        }
    }

    // -----------------------------------------------------------------------
    // T6: Payout ledger debit
    // -----------------------------------------------------------------------

    [Fact]
    public async Task T6_CreatePayout_DebitsSellerWallet()
    {
        // Arrange: get wallet balance before payout
        decimal walletBefore;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = await db.LedgerAccounts.AsNoTracking()
                .FirstAsync(a => a.TenantId == TenantId && a.SellerId == SellerId && a.Type == LedgerAccountType.WALLET);
            walletBefore = wallet.Balance;
        }

        // Act: create a payout via API
        decimal payoutAmount = 100m;
        var payoutDto = new CreatePayoutDto(SellerId, payoutAmount);
        var response = await Client.PostAsJsonAsync("/api/v1/payouts", payoutDto);

        // Assert: payout created successfully
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = json.GetProperty("data");
        data.GetProperty("sellerId").GetString().Should().Be(SellerId.ToString());
        data.GetProperty("amount").GetDecimal().Should().Be(payoutAmount);

        // Verify wallet was debited
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wallet = await db.LedgerAccounts.AsNoTracking()
                .FirstAsync(a => a.TenantId == TenantId && a.SellerId == SellerId && a.Type == LedgerAccountType.WALLET);

            wallet.Balance.Should().BeLessThan(walletBefore);
            wallet.Balance.Should().Be(walletBefore - payoutAmount);
        }
    }

    [Fact]
    public async Task T6_Payout_WithCompletedStatus_HasBankTransactionId()
    {
        // Act: create a payout via API
        var payoutDto = new CreatePayoutDto(SellerId, 200m);
        var response = await Client.PostAsJsonAsync("/api/v1/payouts", payoutDto);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var payoutId = json.GetProperty("data").GetProperty("id").GetString();

        // Verify payout is PAID (FakePayoutProcessor always succeeds)
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payout = await db.Payouts.AsNoTracking().FirstAsync(p => p.Id == Guid.Parse(payoutId!));

        payout.Status.Should().Be(PayoutStatus.PAID);
        payout.BankTransactionId.Should().NotBeNullOrEmpty();
    }

    // -----------------------------------------------------------------------
    // Helper methods
    // -----------------------------------------------------------------------

    /// <summary>
    /// Seeds a CAPTURED Stripe transaction for the default tenant/seller.
    /// Also credits the WALLET with the net amount to support dispute holds.
    /// </summary>
    private async Task SeedCapturedStripeTransactionAsync(string providerTxId, decimal amount, decimal feeAmount, decimal netAmount)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var txResult = Transaction.Create(
            tenantId: TenantId,
            amount: amount,
            paymentType: PaymentType.CREDIT_CARD,
            provider: PaymentProvider.STRIPE,
            installments: 1,
            feeAmount: feeAmount,
            netAmount: netAmount,
            expectedSettlementDate: null,
            providerTxId: providerTxId,
            sellerId: SellerId);

        db.Transactions.Add(txResult.Value);
        await db.SaveChangesAsync();

        // Set to CAPTURED
        await db.Transactions
            .Where(t => t.ProviderTxId == providerTxId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TransactionStatus.CAPTURED));
    }

    /// <summary>
    /// Builds a Stripe dispute webhook payload.
    /// </summary>
    private static object BuildDisputePayload(string eventType, string providerTxId, long amountCents, string status, string disputeId)
    {
        return new
        {
            id = $"evt_{Guid.NewGuid():N}",
            type = eventType,
            data = new
            {
                @object = new
                {
                    id = disputeId,
                    charge = $"ch_{Guid.NewGuid():N}",
                    payment_intent = providerTxId,
                    amount = amountCents,
                    currency = "brl",
                    status = status
                }
            }
        };
    }

    /// <summary>
    /// Sends a Stripe webhook with valid HMAC-SHA256 signature.
    /// Uses an unauthenticated client (webhooks don't require API key).
    /// </summary>
    private async Task<HttpResponseMessage> SendStripeWebhookAsync(object payload)
    {
        var jsonString = JsonSerializer.Serialize(payload);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeStripeSignature(jsonString, timestamp, StripeWebhookSecret);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe")
        {
            Content = new StringContent(jsonString, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Stripe-Signature", $"t={timestamp},v1={signature}");

        var client = CreateUnauthenticatedClient();
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Computes HMAC-SHA256 for Stripe webhook signature validation.
    /// </summary>
    private static string ComputeStripeSignature(string body, string timestamp, string secret)
    {
        var signedPayload = $"{timestamp}.{body}";
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(signedPayload);

        using var hmac = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexString(hash).ToLower();
    }
}
