using FellowCore.Application.Common.Models;
using FellowCore.Application.Modules.Sellers.DTOs;

namespace FellowCore.Application.Modules.Sellers.Interfaces;

public interface ISellerService
{
    Task<SellerResponseDto> CreateAsync(Guid tenantId, CreateSellerDto request);
    Task<IEnumerable<SellerResponseDto>> GetAllAsync(Guid tenantId);
    Task<PagedResult<SellerResponseDto>> ListAsync(Guid tenantId, int page, int pageSize);
    Task<SellerDetailDto> GetByIdAsync(Guid tenantId, Guid sellerId);
    Task<SellerDetailDto> UpdateAsync(Guid tenantId, Guid sellerId, UpdateSellerDto request);

    /// <summary>
    /// Marca/desmarca o seller como Founding. Apenas admin. Quando <paramref name="request"/>.IsFoundingSeller=true,
    /// <paramref name="request"/>.FoundingNumber é obrigatório e único por tenant (DB enforce via unique index parcial).
    /// Quando IsFoundingSeller=false, FoundingNumber é zerado.
    /// </summary>
    Task<SellerDetailDto> SetFoundingAsync(Guid tenantId, Guid sellerId, SetFoundingSellerDto request);
    Task<SellerBalanceDto> GetBalanceAsync(Guid tenantId, Guid sellerId);
    Task<List<SellerStatementEntryDto>> GetStatementAsync(Guid tenantId, Guid sellerId, DateTime? start = null, DateTime? end = null);
    Task<SellerWithdrawResponseDto> WithdrawAsync(Guid tenantId, Guid sellerId, SellerWithdrawRequestDto request);

    /// <summary>
    /// Provisiona retroativamente a Connected Account do provider pra um seller
    /// que foi criado sem ela (ex: seedado direto no banco). Cria conta no
    /// gateway com os dados KYC fornecidos + dados do próprio seller, depois
    /// salva o `acct_xxx` em <c>Sellers.ExternalAccountId</c>. Sem isso, TXs
    /// com esse seller deixariam o dinheiro na plataforma em vez de transferir
    /// pra ele (fail-fast no <c>StripePaymentProvider</c> agora bloqueia).
    /// </summary>
    Task<SellerResponseDto> ProvisionConnectAccountAsync(Guid tenantId, Guid sellerId, ProvisionConnectAccountDto request);

    /// <summary>
    /// Compara o saldo do seller no ledger interno com o saldo real na conta
    /// Stripe Connect dele. READ-ONLY — não modifica nada. Detecta:
    ///   - Dinheiro fantasma local: TXs antigas (pré-Connect) que creditaram
    ///     o seller no ledger mas o caixa ficou na plataforma.
    ///   - Saldo Stripe não contabilizado: dinheiro chegou na Stripe do seller
    ///     mas o ledger não viu (TX manual, webhook perdido, etc).
    /// Retorna o delta + recomendação textual de ação.
    /// </summary>
    Task<StripeSyncReportDto> SyncStripeBalanceAsync(Guid tenantId, Guid sellerId);

    /// <summary>
    /// Write-off do ledger pra alinhar com o caixa real Stripe. Roda
    /// <c>SyncStripeBalanceAsync</c> internamente pra obter target, depois
    /// chama <c>LedgerService.ReconcileSellerBalanceAsync</c>. Operação
    /// destrutiva — exige confirmação explícita do operador (auditoria via
    /// AuditAction). Retorna o report pós-ajuste pra confirmar que zerou.
    /// </summary>
    Task<StripeReconcileResultDto> ReconcileWithStripeAsync(Guid tenantId, Guid sellerId, string reason);

    // Subaccount management
    Task<SubAccountDto> CreateSubAccountAsync(Guid tenantId, CreateSubAccountDto request);
    Task<SubAccountDto> GetSubAccountAsync(Guid tenantId, string pixKeyOrId);
    Task<List<SubAccountDto>> ListSubAccountsAsync(Guid tenantId);
    Task DeleteSubAccountAsync(Guid tenantId, string pixKeyOrId);
    Task<SubAccountCreditDebitResponseDto> CreditSubAccountAsync(Guid tenantId, string pixKeyOrId, SubAccountCreditDebitDto request);
    Task<SubAccountCreditDebitResponseDto> DebitSubAccountAsync(Guid tenantId, string pixKeyOrId, SubAccountCreditDebitDto request);
    Task<SubAccountTransferResponseDto> TransferBetweenSubAccountsAsync(Guid tenantId, SubAccountTransferDto request);
    Task<SubAccountWithdrawResponseDto> WithdrawFromSubAccountAsync(Guid tenantId, string pixKeyOrId, SubAccountWithdrawDto request);
    Task<List<SubAccountStatementEntryDto>> GetSubAccountStatementAsync(Guid tenantId, string pixKeyOrId, DateTime? start = null, DateTime? end = null);
}