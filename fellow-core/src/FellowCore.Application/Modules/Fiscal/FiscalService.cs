using FellowCore.Application.Exceptions;
using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;

namespace FellowCore.Application.Modules.Fiscal;

public class FiscalService(
    ISellerFiscalSettingsRepository fiscalSettingsRepository,
    IFiscalInvoiceRepository fiscalInvoiceRepository,
    ITransactionRepository transactionRepository,
    ISellerRepository sellerRepository,
    IUnitOfWork unitOfWork) : IFiscalService
{
    public async Task<SellerFiscalSettings> GetOrCreateSettingsAsync(Guid tenantId, Guid sellerId)
    {
        var existing = await fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId);
        if (existing != null) return existing;

        _ = await sellerRepository.GetByIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("Seller", sellerId.ToString());

        var settings = SellerFiscalSettings.Create(tenantId, sellerId);
        await fiscalSettingsRepository.AddAsync(settings);
        await unitOfWork.CommitAsync();
        return settings;
    }

    public async Task<SellerFiscalSettings> UpdateSettingsAsync(Guid tenantId, Guid sellerId, UpdateFiscalSettingsDto dto)
    {
        var settings = await GetOrCreateSettingsAsync(tenantId, sellerId);
        settings.Update(dto.MunicipalRegistration, dto.ServiceCode, dto.IssRate, dto.CityCode);
        await unitOfWork.CommitAsync();
        return settings;
    }

    public async Task EnableAsync(Guid tenantId, Guid sellerId)
    {
        var settings = await fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("SellerFiscalSettings", sellerId.ToString());

        if (string.IsNullOrWhiteSpace(settings.MunicipalRegistration))
            throw new BusinessException("Fiscal.MissingRegistration", "Inscricao municipal obrigatoria para emitir NFS-e.");

        if (string.IsNullOrWhiteSpace(settings.ServiceCode))
            throw new BusinessException("Fiscal.MissingServiceCode", "Codigo de servico obrigatorio para emitir NFS-e.");

        settings.Enable();
        await unitOfWork.CommitAsync();
    }

    public async Task DisableAsync(Guid tenantId, Guid sellerId)
    {
        var settings = await fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId)
            ?? throw new NotFoundException("SellerFiscalSettings", sellerId.ToString());

        settings.Disable();
        await unitOfWork.CommitAsync();
    }

    public async Task<FiscalInvoice> RequestInvoiceAsync(Guid tenantId, Guid transactionId)
    {
        // Check idempotency - if invoice already exists for this transaction
        var existing = await fiscalInvoiceRepository.GetByTransactionIdAsync(tenantId, transactionId);
        if (existing != null) return existing;

        var transaction = await transactionRepository.GetByIdAsync(tenantId, transactionId)
            ?? throw new NotFoundException("Transaction", transactionId.ToString());

        if (transaction.Status != TransactionStatus.CAPTURED)
            throw new BusinessException("Fiscal.InvalidStatus", "NFS-e so pode ser emitida para transacoes capturadas.");

        if (!transaction.SellerId.HasValue)
            throw new BusinessException("Fiscal.NoSeller", "Transacao sem seller associado.");

        var sellerId = transaction.SellerId.Value;

        var settings = await fiscalSettingsRepository.GetBySellerIdAsync(tenantId, sellerId)
            ?? throw new BusinessException("Fiscal.NotConfigured", "Configuracoes fiscais nao encontradas para o seller.");

        if (!settings.Enabled)
            throw new BusinessException("Fiscal.Disabled", "Emissao de NFS-e esta desabilitada para este seller.");

        decimal issAmount = transaction.Amount * (settings.IssRate / 100m);

        var invoice = FiscalInvoice.Create(
            tenantId,
            sellerId,
            transactionId,
            transaction.Amount,
            issAmount,
            $"Servico ref. transacao {transaction.Id}");

        await fiscalInvoiceRepository.AddAsync(invoice);
        await unitOfWork.CommitAsync();

        return invoice;
    }

    public async Task<FiscalInvoice?> GetInvoiceByTransactionAsync(Guid tenantId, Guid transactionId)
    {
        return await fiscalInvoiceRepository.GetByTransactionIdAsync(tenantId, transactionId);
    }

    public async Task<List<FiscalInvoice>> GetInvoicesBySellerAsync(Guid tenantId, Guid sellerId, int limit = 50, int offset = 0)
    {
        return await fiscalInvoiceRepository.GetBySellerAsync(tenantId, sellerId, limit, offset);
    }
}
