using FellowCore.Application.Common.Interfaces;
using FellowCore.Application.Common.Models;
using FellowCore.Application.Common.Utils;
using FellowCore.Application.Exceptions;
using FellowCore.Application.Modules.Sellers.DTOs;
using FellowCore.Application.Modules.Sellers.Interfaces;
using FellowCore.Application.Modules.Ledgers.Interfaces;
using FellowCore.Application.Modules.Transactions.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Interfaces;
using FellowCore.Application.Modules.Transactions.Providers.OpenPix.Models;
using FellowCore.Application.Modules.Transactions.Providers.Stripe.Interfaces;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace FellowCore.Application.Modules.Sellers.Services;

public class SellerService(
    ISellerRepository sellerRepository,
    ITenantRepository tenantRepository,
    IPaymentProviderFactory providerFactory,
    IOpenPixApiClient openPixApi,
    IStripeApiClient stripeApi,
    ISecurityService securityService,
    ILedgerService ledgerService,
    ITransactionInstallmentRepository installmentRepository,
    IConfiguration configuration,
    IUnitOfWork unitOfWork,
    ILogger<SellerService> logger) : ISellerService
{
    public async Task<SellerResponseDto> CreateAsync(Guid tenantId, CreateSellerDto request)
    {
        if (await sellerRepository.ExistsByDocumentAsync(tenantId, request.Document))
            throw new ConflictException("Seller.DuplicateDocument", "Já existe um seller com este documento neste Tenant.");

        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId)
            ?? throw new NotFoundException("Tenant.NotFound", "Tenant não encontrado.");

        if (tenant.Config == null)
            throw new BusinessException("Tenant.NoConfig", "Tenant sem configuração de pagamento ativa.");

        var providerType = tenant.Config.ActivePixProvider;
        var gateway = providerFactory.GetProvider(providerType);

        logger.LogInformation("Criando subconta para o Seller {Doc} no Gateway {Gateway}", request.Document, providerType);
        var subAccount = await gateway.CreateSubAccountAsync(tenant, request);

        string encryptedKey = await securityService.EncryptAsync(subAccount.ApiKey);

        var seller = Seller.Create(
            tenantId: tenantId,
            legalName: request.LegalName,
            document: request.Document,
            email: request.Email,
            webhookSecret: await securityService.EncryptAsync(CryptoUtils.GenerateRandomHex(32)),
            preferredProvider: providerType,
            externalAccountId: subAccount.ExternalAccountId,
            encryptedAccessToken: encryptedKey,
            tradeName: request.TradeName,
            mobilePhone: request.MobilePhone,
            pixKey: subAccount.PixKey,
            address: request.Address != null ? JsonSerializer.SerializeToDocument(request.Address) : null,
            feeDebit: request.FeeDebit ?? 2.0M,
            feePixIn: request.FeePixIn ?? 1.0M,
            feeCreditCash: request.FeeCreditCash ?? 4.50M,
            feeCreditInstallment: request.FeeCreditInstallment ?? 6.50M,
            payoutFixedFee: request.PayoutFixedFee ?? 1M,
            payoutPercentFee: request.PayoutPercentFee ?? 1.50M);

        await unitOfWork.BeginAsync();
        try
        {
            sellerRepository.Add(seller);
            await unitOfWork.CommitAsync();
        }
        catch (Exception ex) when (ex is not AppException)
        {
            await unitOfWork.RollbackAsync();
            logger.LogError(ex, "Falha ao salvar o Seller no banco após criar subconta.");
            throw;
        }

        // Hook subconta nominal Woovi (POST /api/v1/subaccount):
        // No modelo split-de-cobrança (spec 2026-05-15), cada seller precisa ter
        // uma SUBCONTA na conta principal da plataforma, identificada pela chave
        // PIX do beneficiário. Charges com splits[] roteiam valor pra essa
        // subconta automaticamente.
        //
        // Diferente do gateway.CreateSubAccountAsync acima (que abre conta BaaS
        // filha completa via /api/v1/account/register, modelo legacy), este hook
        // chama o endpoint /api/v1/subaccount nominal — apenas nome + chave PIX.
        //
        // Falha aqui NÃO derruba o onboarding — seller fica criado, admin pode
        // forçar a criação manual depois. Cenários de falha: feature Subconta
        // não ativada no painel Woovi, chave PIX inválida no DICT, etc.
        if (providerType == PaymentProvider.OPENPIX && !string.IsNullOrWhiteSpace(seller.PixKey))
        {
            var platformAppId = configuration["OpenPix:AppId"];
            if (!string.IsNullOrWhiteSpace(platformAppId))
            {
                try
                {
                    await openPixApi.CreateSubAccountAsync(
                        platformAppId,
                        new OpenPixSubAccountRequest(PixKey: seller.PixKey, Name: seller.LegalName));
                    logger.LogInformation(
                        "[WOOVI] Subconta nominal criada pro seller {SellerId} (pixKey={PixKey})",
                        seller.Id, seller.PixKey);
                }
                catch (Exception ex)
                {
                    // Não-bloqueante. Admin tem ferramenta pra retentar.
                    logger.LogWarning(ex,
                        "[WOOVI] Falha criando subconta nominal pro seller {SellerId} (pixKey={PixKey}). " +
                        "Onboarding mantido; recriar subconta via endpoint admin.",
                        seller.Id, seller.PixKey);
                }
            }
            else
            {
                logger.LogWarning(
                    "[WOOVI] OpenPix:AppId não configurado — subconta não criada pro seller {SellerId}.",
                    seller.Id);
            }
        }

        return new SellerResponseDto(seller.Id, seller.LegalName, seller.Document, seller.Status, seller.CreatedAt);
    }

    public async Task<SellerResponseDto> ProvisionConnectAccountAsync(Guid tenantId, Guid sellerId, ProvisionConnectAccountDto request)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} não encontrado.");

        if (!string.IsNullOrEmpty(seller.ExternalAccountId))
            throw new ConflictException("Seller.AlreadyProvisioned",
                $"Seller já tem ExternalAccountId={seller.ExternalAccountId}. Não recriamos pra evitar contas órfãs no provider.");

        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId)
            ?? throw new NotFoundException("Tenant.NotFound", "Tenant não encontrado.");

        if (tenant.Config == null)
            throw new BusinessException("Tenant.NoConfig", "Tenant sem configuração de pagamento ativa.");

        // Compõe um CreateSellerDto reaproveitando os campos que o seller já
        // tem no banco (LegalName, Document, Email) + o que veio do request
        // (KYC adicional: BirthDate, MobilePhone, Address, IncomeValue, banco).
        // Fees ficam null → o provider/seed default decide.
        var createDto = new CreateSellerDto(
            LegalName: seller.LegalName,
            TradeName: seller.TradeName,
            Document: seller.Document,
            Email: seller.Email,
            IncomeValue: request.IncomeValue,
            BirthDate: request.BirthDate,
            MobilePhone: request.MobilePhone,
            Address: request.Address,
            FeeDebit: null,
            FeeCreditCash: null,
            FeeCreditInstallment: null,
            FeePixIn: null,
            PayoutFixedFee: null,
            PayoutPercentFee: null,
            BusinessDescription: null,
            BusinessProduct: null,
            BusinessLifetime: null,
            BusinessGoal: null,
            Documents: null,
            BankAccount: request.BankAccount,
            Mcc: request.Mcc,
            ProductDescription: request.ProductDescription,
            PoliticalExposure: request.PoliticalExposure,
            VerificationDocumentToken: request.VerificationDocumentToken
        );

        // Endpoint é /stripe-connect/provision — sempre cria conta Stripe Connect
        // (não OpenPix). O `ActivePixProvider` do tenant é só pra PIX in/out;
        // pra cartão de crédito a plataforma sempre roteia pelo Stripe.
        const PaymentProvider providerType = PaymentProvider.STRIPE;
        var gateway = providerFactory.GetProvider(providerType);

        logger.LogInformation("[CONNECT_PROVISION] Provisionando Stripe Connect retroativamente pro seller {SellerId} ({Doc})",
            seller.Id, seller.Document);

        var subAccount = await gateway.CreateSubAccountAsync(tenant, createDto);

        string encryptedKey = await securityService.EncryptAsync(subAccount.ApiKey);
        var linkResult = seller.LinkProviderAccount(subAccount.ExternalAccountId, encryptedKey, subAccount.PixKey);
        if (linkResult.IsFailure)
            throw new BusinessException(linkResult.Error.Code, linkResult.Error.Description);

        await unitOfWork.BeginAsync();
        try
        {
            sellerRepository.Update(seller);
            await unitOfWork.CommitAsync();
        }
        catch (Exception ex) when (ex is not AppException)
        {
            await unitOfWork.RollbackAsync();
            logger.LogError(ex,
                "[CONNECT_PROVISION] CRITICAL: Conta {ExternalAccountId} criada no provider mas falha ao salvar no banco. Intervenção manual necessária pra desvincular órfão.",
                subAccount.ExternalAccountId);
            throw;
        }

        logger.LogInformation("[CONNECT_PROVISION] Seller {SellerId} provisionado com sucesso. ExternalAccountId={ExternalAccountId}",
            seller.Id, subAccount.ExternalAccountId);

        return new SellerResponseDto(seller.Id, seller.LegalName, seller.Document, seller.Status, seller.CreatedAt);
    }

    public async Task<StripeSyncReportDto> SyncStripeBalanceAsync(Guid tenantId, Guid sellerId)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} não encontrado.");

        if (string.IsNullOrEmpty(seller.ExternalAccountId))
            throw new BusinessException(
                "Seller.NoConnectedAccount",
                $"Seller {sellerId} não tem Stripe Connect provisionado — não há o que sincronizar. " +
                "Provisione antes via POST /api/v1/sellers/{id}/stripe-connect/provision.");

        // 1. Ledger local
        var local = await ledgerService.GetBalanceAsync(tenantId, sellerId);
        decimal localAvailable = local.Available;
        decimal localPending = local.WaitingFunds;
        decimal localTotal = localAvailable + localPending;

        // 2. Stripe real (conta conectada)
        string apiKey = configuration["Stripe:SecretKey"]
            ?? throw new BusinessException("Stripe.KeyMissing", "Stripe:SecretKey não configurado.");
        var stripeBalance = await stripeApi.GetBalanceAsync(apiKey, seller.ExternalAccountId);

        // Stripe pode retornar várias moedas; filtramos BRL e somamos. Se não
        // tiver entrada de BRL, fica 0 (conta nunca foi creditada).
        decimal stripeAvailable = (stripeBalance.Available ?? new())
            .Where(b => string.Equals(b.Currency, "brl", StringComparison.OrdinalIgnoreCase))
            .Sum(b => b.Amount / 100m);
        decimal stripePending = (stripeBalance.Pending ?? new())
            .Where(b => string.Equals(b.Currency, "brl", StringComparison.OrdinalIgnoreCase))
            .Sum(b => b.Amount / 100m);
        decimal stripeTotal = stripeAvailable + stripePending;

        decimal deltaAvailable = localAvailable - stripeAvailable;
        decimal deltaPending = localPending - stripePending;
        decimal deltaTotal = localTotal - stripeTotal;

        bool hasDiscrepancy = Math.Abs(deltaTotal) > 0.01m;

        // Recomendação textual — orienta o operador sem decidir por ele.
        string recommendation = !hasDiscrepancy
            ? "Saldos batem — nada a fazer."
            : deltaTotal > 0
                ? $"Ledger local tem R$ {deltaTotal:F2} a mais que a Stripe. Provavelmente " +
                  "dinheiro fantasma de TXs pré-Connect (capturadas antes do seller ter " +
                  "conta conectada, ficaram no caixa da plataforma). Considere write-off " +
                  "via POST /api/v1/sellers/{id}/stripe-reconcile pra alinhar o ledger com a realidade."
                : $"Stripe tem R$ {Math.Abs(deltaTotal):F2} a mais que o ledger. " +
                  "Dinheiro chegou no Stripe Connect do seller mas o ledger não capturou (webhook perdido, " +
                  "TX manual, etc). Investigue o histórico antes de qualquer ajuste.";

        logger.LogInformation(
            "[STRIPE_SYNC] Seller {SellerId} ({Account}) — local: R${LocalTotal} | stripe: R${StripeTotal} | delta: R${Delta}",
            sellerId, seller.ExternalAccountId, localTotal, stripeTotal, deltaTotal);

        return new StripeSyncReportDto(
            SellerId: sellerId,
            ExternalAccountId: seller.ExternalAccountId,
            LocalAvailable: localAvailable,
            LocalPending: localPending,
            LocalTotal: localTotal,
            StripeAvailable: stripeAvailable,
            StripePending: stripePending,
            StripeTotal: stripeTotal,
            DeltaAvailable: deltaAvailable,
            DeltaPending: deltaPending,
            DeltaTotal: deltaTotal,
            HasDiscrepancy: hasDiscrepancy,
            Recommendation: recommendation
        );
    }

    public async Task<StripeReconcileResultDto> ReconcileWithStripeAsync(Guid tenantId, Guid sellerId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new BusinessException("Reconcile.MissingReason",
                "Campo `reason` é obrigatório — operação destrutiva exige audit trail.");

        // Reusa o sync pra ter os valores target. Se não tem ExternalAccountId,
        // sync joga BusinessException — propagamos.
        var sync = await SyncStripeBalanceAsync(tenantId, sellerId);

        if (!sync.HasDiscrepancy)
        {
            logger.LogInformation("[STRIPE_RECONCILE] Seller {SellerId} já em sync, no-op.", sellerId);
            return new StripeReconcileResultDto(
                SellerId: sellerId,
                WalletAdjustment: 0,
                FutureReceivablesAdjustment: 0,
                TotalWriteOff: 0,
                NewLocalAvailable: sync.LocalAvailable,
                NewLocalPending: sync.LocalPending,
                Reason: reason
            );
        }

        var result = await ledgerService.ReconcileSellerBalanceAsync(
            tenantId, sellerId,
            targetAvailable: sync.StripeAvailable,
            targetPending: sync.StripePending,
            reason: reason);

        logger.LogWarning(
            "[STRIPE_RECONCILE] Seller {SellerId} reconciliado com Stripe {Account}. Write-off total: R${WriteOff}. Razão: {Reason}",
            sellerId, sync.ExternalAccountId, result.TotalWriteOff, reason);

        return new StripeReconcileResultDto(
            SellerId: sellerId,
            WalletAdjustment: result.WalletAdjustment,
            FutureReceivablesAdjustment: result.FutureReceivablesAdjustment,
            TotalWriteOff: result.TotalWriteOff,
            NewLocalAvailable: result.NewWalletBalance,
            NewLocalPending: result.NewFutureReceivablesBalance,
            Reason: reason
        );
    }

    public async Task<IEnumerable<SellerResponseDto>> GetAllAsync(Guid tenantId)
    {
        var sellers = await sellerRepository.GetAllAsync(tenantId);
        return sellers.Select(seller => new SellerResponseDto(seller.Id, seller.LegalName, seller.Document, seller.Status, seller.CreatedAt));
    }

    public async Task<PagedResult<SellerResponseDto>> ListAsync(Guid tenantId, int page, int pageSize)
    {
        var (skip, take, normalizedPage) = PagedResult<SellerResponseDto>.Normalize(page, pageSize);
        var (items, totalCount) = await sellerRepository.GetPagedAsync(tenantId, skip, take);

        return new PagedResult<SellerResponseDto>(
            Items: items.Select(s => new SellerResponseDto(s.Id, s.LegalName, s.Document, s.Status, s.CreatedAt)).ToList(),
            TotalCount: totalCount,
            Page: normalizedPage,
            PageSize: take);
    }

    public async Task<SellerDetailDto> GetByIdAsync(Guid tenantId, Guid sellerId)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} nao encontrado.");

        return MapToDetail(seller);
    }

    public async Task<SellerDetailDto> SetFoundingAsync(Guid tenantId, Guid sellerId, SetFoundingSellerDto request)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} nao encontrado.");

        if (!request.IsFoundingSeller)
        {
            seller.ClearFounding();
            logger.LogInformation("Seller {SellerId} desmarcado como Founding por admin (tenant {TenantId})", sellerId, tenantId);
        }
        else
        {
            if (!request.FoundingNumber.HasValue)
                throw new BusinessException("Seller.MissingFoundingNumber",
                    "FoundingNumber é obrigatório quando IsFoundingSeller=true.");

            // Pre-check pra dar mensagem amigável antes do unique index parcial do banco
            // disparar (DbUpdateException virtualmente impossível de tratar no Application
            // sem referenciar EF). Race condition ainda possível em concorrência alta —
            // o index permanece como defesa final, mas com mensagem cru pro caller.
            var taken = await sellerRepository.IsFoundingNumberTakenAsync(
                tenantId, request.FoundingNumber.Value, excludingSellerId: sellerId);
            if (taken)
                throw new BusinessException("Seller.FoundingNumberTaken",
                    $"O Founding Number #{request.FoundingNumber} já está em uso por outro seller deste tenant.");

            var setResult = seller.SetFounding(request.FoundingNumber.Value);
            if (setResult.IsFailure)
                throw new BusinessException(setResult.Error.Code, setResult.Error.Description);

            logger.LogInformation("Seller {SellerId} marcado como Founding #{Number} por admin (tenant {TenantId})",
                sellerId, request.FoundingNumber.Value, tenantId);
        }

        sellerRepository.Update(seller);
        await sellerRepository.SaveChangesAsync();

        return MapToDetail(seller);
    }

    public async Task<SellerDetailDto> UpdateAsync(Guid tenantId, Guid sellerId, UpdateSellerDto request)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} nao encontrado.");

        seller.Update(request.TradeName, request.Email, request.MobilePhone, request.PixKey, request.WebhookUrl);

        // Toggle de antecipação automática — null mantém o estado atual,
        // true/false aplica. Só afeta TXs futuras (TXs já capturadas mantêm
        // seu SettlementMode registrado).
        if (request.AutoAdvanceSettlement.HasValue)
        {
            seller.SetAutoAdvanceSettlement(request.AutoAdvanceSettlement.Value);
            logger.LogInformation("Seller {SellerId} AutoAdvanceSettlement={State}",
                sellerId, request.AutoAdvanceSettlement.Value);
        }

        sellerRepository.Update(seller);
        await sellerRepository.SaveChangesAsync();

        return MapToDetail(seller);
    }

    public async Task<SellerBalanceDto> GetBalanceAsync(Guid tenantId, Guid sellerId)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} nao encontrado.");

        // Try external provider balance first; fall back to internal ledger.
        // External path tem total/blocked/available consolidado pelo Stripe, mas o
        // schedule por dia vem SEMPRE do nosso ledger porque é a única fonte que
        // sabe quando cada TX libera (Stripe não expõe expected payout date por tx).
        decimal available, blocked, total;
        bool isReady;

        if (seller.PreferredProvider != null)
        {
            try
            {
                var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId)
                    ?? throw new NotFoundException("Tenant.NotFound", "Tenant nao encontrado.");

                var gateway = providerFactory.GetProvider(seller.PreferredProvider.Value);
                var balance = await gateway.GetAccountBalanceAsync(tenant, seller);

                available = balance.AvailableInReais;
                blocked = balance.BlockedInReais;
                total = balance.TotalInReais;
                isReady = balance.IsReady;
            }
            catch (NotSupportedException)
            {
                var ledgerBalance = await ledgerService.GetBalanceAsync(tenantId, sellerId);
                available = ledgerBalance.Available;
                blocked = ledgerBalance.WaitingFunds;
                total = ledgerBalance.Total;
                isReady = true;
            }
        }
        else
        {
            var ledgerBalance = await ledgerService.GetBalanceAsync(tenantId, sellerId);
            available = ledgerBalance.Available;
            blocked = ledgerBalance.WaitingFunds;
            total = ledgerBalance.Total;
            isReady = true;
        }

        // Schedule + buckets — só vale a pena se há dinheiro bloqueado.
        // Evita query desnecessária pra sellers com saldo 100% disponível.
        List<SellerReleaseSlotDto>? byDate = null;
        SellerReleaseBucketsDto? buckets = null;
        if (blocked > 0)
        {
            var now = DateTime.UtcNow;
            // Fonte agora é TransactionInstallments (parcela-a-parcela) em vez de
            // Transactions.ExpectedSettlementDate (data única). Crédito 6x mostra 6 linhas
            // de R$ X cada, não 1 linha gigante em D+180.
            var schedule = await installmentRepository.GetReleaseScheduleAsync(tenantId, sellerId, now);
            byDate = schedule.Select(s => new SellerReleaseSlotDto(s.ReleaseDate, s.Amount)).ToList();
            buckets = new SellerReleaseBucketsDto(
                Next2Days:   schedule.Where(s => s.ReleaseDate <= now.AddDays(2)).Sum(s => s.Amount),
                Next7Days:   schedule.Where(s => s.ReleaseDate <= now.AddDays(7)).Sum(s => s.Amount),
                Next30Days:  schedule.Where(s => s.ReleaseDate <= now.AddDays(30)).Sum(s => s.Amount),
                Next90Days:  schedule.Where(s => s.ReleaseDate <= now.AddDays(90)).Sum(s => s.Amount),
                Next180Days: schedule.Where(s => s.ReleaseDate <= now.AddDays(180)).Sum(s => s.Amount),
                Next365Days: schedule.Where(s => s.ReleaseDate <= now.AddDays(365)).Sum(s => s.Amount));
        }

        return new SellerBalanceDto(
            SellerId: sellerId,
            Total: total,
            Blocked: blocked,
            Available: available,
            IsAccountReady: isReady,
            BlockedByDate: byDate,
            BlockedBuckets: buckets);
    }

    public async Task<List<SellerStatementEntryDto>> GetStatementAsync(Guid tenantId, Guid sellerId, DateTime? start = null, DateTime? end = null)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} nao encontrado.");

        if (seller.PreferredProvider == null)
            throw new BusinessException("Seller.NoProvider", "Seller nao possui provider configurado.");

        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId)
            ?? throw new NotFoundException("Tenant.NotFound", "Tenant nao encontrado.");

        var gateway = providerFactory.GetProvider(seller.PreferredProvider.Value);
        var entries = await gateway.GetStatementAsync(tenant, seller, start, end);

        return entries.Select(e => new SellerStatementEntryDto(
            EndToEndId: e.EndToEndId,
            Amount: e.AmountInReais,
            Time: e.Time,
            Type: e.Type,
            Description: e.Description
        )).ToList();
    }

    public async Task<SellerWithdrawResponseDto> WithdrawAsync(Guid tenantId, Guid sellerId, SellerWithdrawRequestDto request)
    {
        var seller = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller.NotFound", $"Seller {sellerId} nao encontrado.");

        if (seller.PreferredProvider != PaymentProvider.OPENPIX)
            throw new BusinessException("Seller.WithdrawNotSupported", "Saque so esta disponivel para sellers com provider OpenPix.");

        if (string.IsNullOrEmpty(seller.ExternalAccountId))
            throw new BusinessException("Seller.NoExternalAccount", "Seller nao possui conta BaaS configurada.");

        if (request.Amount <= 0)
            throw new BusinessException("Withdraw.InvalidAmount", "Valor do saque deve ser maior que zero.");

        string appId = await securityService.DecryptAsync(seller.EncryptedAccessToken!);

        var withdrawRequest = new OpenPixWithdrawRequest(Value: (int)(request.Amount * 100));
        var result = await openPixApi.WithdrawFromAccountAsync(appId, seller.ExternalAccountId, withdrawRequest);

        var remainingBalance = result.Withdraw?.Account?.Balance ?? 0;

        logger.LogInformation("Saque de {Amount} para seller {SellerId}. EndToEndId: {E2E}",
            request.Amount, sellerId, result.Withdraw?.Transaction?.EndToEndId);

        return new SellerWithdrawResponseDto(
            SellerId: sellerId,
            Amount: request.Amount,
            EndToEndId: result.Withdraw?.Transaction?.EndToEndId,
            RemainingBalance: remainingBalance / 100m
        );
    }

    // --- Subaccount management ---

    private async Task<string> GetTenantAppIdAsync(Guid tenantId)
    {
        var tenant = await tenantRepository.GetByIdWithConfigAsync(tenantId)
            ?? throw new NotFoundException("Tenant.NotFound", "Tenant nao encontrado.");

        if (tenant.Config?.ActivePixProvider != PaymentProvider.OPENPIX)
            throw new BusinessException("Tenant.ProviderNotOpenPix", "Subcontas so estao disponiveis para tenants com provider OpenPix.");

        return configuration["OpenPix:AppId"]
            ?? throw new BusinessException("Tenant.NoOpenPixAppId", "OpenPix:AppId nao configurado.");
    }

    public async Task<SubAccountDto> CreateSubAccountAsync(Guid tenantId, CreateSubAccountDto request)
    {
        var appId = await GetTenantAppIdAsync(tenantId);
        var result = await openPixApi.CreateSubAccountAsync(appId, new OpenPixSubAccountRequest(request.PixKey, request.Name));
        return MapSubAccount(result.SubAccount);
    }

    public async Task<SubAccountDto> GetSubAccountAsync(Guid tenantId, string pixKeyOrId)
    {
        var appId = await GetTenantAppIdAsync(tenantId);
        var result = await openPixApi.GetSubAccountAsync(appId, pixKeyOrId);
        return MapSubAccount(result.SubAccount);
    }

    public async Task<List<SubAccountDto>> ListSubAccountsAsync(Guid tenantId)
    {
        var appId = await GetTenantAppIdAsync(tenantId);
        var result = await openPixApi.ListSubAccountsAsync(appId);
        return result.Subaccounts.Select(MapSubAccount).ToList();
    }

    public async Task DeleteSubAccountAsync(Guid tenantId, string pixKeyOrId)
    {
        var appId = await GetTenantAppIdAsync(tenantId);
        await openPixApi.DeleteSubAccountAsync(appId, pixKeyOrId);
    }

    public async Task<SubAccountCreditDebitResponseDto> CreditSubAccountAsync(Guid tenantId, string pixKeyOrId, SubAccountCreditDebitDto request)
    {
        if (request.Amount <= 0)
            throw new BusinessException("SubAccount.InvalidAmount", "Valor deve ser maior que zero.");

        var appId = await GetTenantAppIdAsync(tenantId);
        var result = await openPixApi.CreditSubAccountAsync(appId, pixKeyOrId,
            new OpenPixSubAccountCreditDebitRequest((int)(request.Amount * 100), request.Description));

        return new SubAccountCreditDebitResponseDto(result.PixKey, result.Value / 100m, result.Description, result.Success);
    }

    public async Task<SubAccountCreditDebitResponseDto> DebitSubAccountAsync(Guid tenantId, string pixKeyOrId, SubAccountCreditDebitDto request)
    {
        if (request.Amount <= 0)
            throw new BusinessException("SubAccount.InvalidAmount", "Valor deve ser maior que zero.");

        var appId = await GetTenantAppIdAsync(tenantId);
        var result = await openPixApi.DebitSubAccountAsync(appId, pixKeyOrId,
            new OpenPixSubAccountCreditDebitRequest((int)(request.Amount * 100), request.Description));

        return new SubAccountCreditDebitResponseDto(result.PixKey, result.Value / 100m, result.Description, result.Success);
    }

    public async Task<SubAccountTransferResponseDto> TransferBetweenSubAccountsAsync(Guid tenantId, SubAccountTransferDto request)
    {
        if (request.Amount <= 0)
            throw new BusinessException("SubAccount.InvalidAmount", "Valor deve ser maior que zero.");

        var appId = await GetTenantAppIdAsync(tenantId);
        var result = await openPixApi.TransferBetweenSubAccountsAsync(appId,
            new OpenPixSubAccountTransferRequest(
                Value: (int)(request.Amount * 100),
                FromPixKey: request.FromPixKey,
                ToPixKey: request.ToPixKey,
                FromPixKeyType: request.FromPixKeyType,
                ToPixKeyType: request.ToPixKeyType));

        return new SubAccountTransferResponseDto(
            Amount: result.Value / 100m,
            Origin: result.OriginSubaccount != null
                ? new SubAccountSummaryDto(result.OriginSubaccount.Name, result.OriginSubaccount.PixKey, result.OriginSubaccount.Balance / 100m)
                : null,
            Destination: result.DestinationSubaccount != null
                ? new SubAccountSummaryDto(result.DestinationSubaccount.Name, result.DestinationSubaccount.PixKey, result.DestinationSubaccount.Balance / 100m)
                : null
        );
    }

    public async Task<SubAccountWithdrawResponseDto> WithdrawFromSubAccountAsync(Guid tenantId, string pixKeyOrId, SubAccountWithdrawDto request)
    {
        if (request.Amount <= 0)
            throw new BusinessException("SubAccount.InvalidAmount", "Valor deve ser maior que zero.");

        var appId = await GetTenantAppIdAsync(tenantId);
        var result = await openPixApi.WithdrawFromSubAccountAsync(appId, pixKeyOrId,
            new OpenPixSubAccountWithdrawRequest((int)(request.Amount * 100)));

        return new SubAccountWithdrawResponseDto(
            Status: result.Withdraw?.Account?.Status,
            Amount: request.Amount
        );
    }

    public async Task<List<SubAccountStatementEntryDto>> GetSubAccountStatementAsync(Guid tenantId, string pixKeyOrId, DateTime? start = null, DateTime? end = null)
    {
        var appId = await GetTenantAppIdAsync(tenantId);
        var entries = await openPixApi.GetSubAccountStatementAsync(appId, pixKeyOrId, start, end);

        return entries.Select(e => new SubAccountStatementEntryDto(
            Id: e.Id,
            Time: e.Time,
            Description: e.Description,
            Balance: e.Balance / 100m,
            Amount: e.Value / 100m,
            Type: e.Type,
            OperationType: e.OperationType
        )).ToList();
    }

    private static SubAccountDto MapSubAccount(OpenPixSubAccountData data) => new(
        Name: data.Name,
        PixKey: data.PixKey,
        Balance: data.Balance / 100m,
        WithdrawBlocked: data.WithdrawBlocked
    );

    private static SellerDetailDto MapToDetail(Seller seller) => new(
        Id: seller.Id,
        LegalName: seller.LegalName,
        TradeName: seller.TradeName,
        Document: seller.Document,
        Email: seller.Email,
        MobilePhone: seller.MobilePhone,
        PixKey: seller.PixKey,
        Status: seller.Status,
        PreferredProvider: seller.PreferredProvider,
        ExternalAccountId: seller.ExternalAccountId,
        CreatedAt: seller.CreatedAt,
        UpdatedAt: seller.UpdatedAt,
        AutoAdvanceSettlement: seller.AutoAdvanceSettlement,
        AdvanceCreditLimit: seller.AdvanceCreditLimit,
        AdvanceExposureCurrent: seller.AdvanceExposureCurrent,
        IsFoundingSeller: seller.IsFoundingSeller,
        FoundingNumber: seller.FoundingNumber
    );
}
