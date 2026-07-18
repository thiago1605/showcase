using FellowCore.Domain.Entities;
using FellowCore.Domain.Enums;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class SellerRepository(AppDbContext _context) : ISellerRepository
{
    public void Add(Seller seller) => _context.Sellers.Add(seller);
    public void Update(Seller seller) => _context.Sellers.Update(seller);

    public async Task<bool> ExistsByDocumentAsync(Guid tenantId, string document)
    {
        return await _context.Sellers.AnyAsync(seller => seller.TenantId == tenantId && seller.Document == document);
    }

    public async Task<Seller?> GetByIdAsync(Guid tenantId, Guid sellerId)
    {
        return await _context.Sellers.FirstOrDefaultAsync(seller => seller.TenantId == tenantId && seller.Id == sellerId);
    }

    public async Task<IReadOnlyList<Seller>> GetByIdsAsync(Guid tenantId, IReadOnlyCollection<Guid> sellerIds)
    {
        if (sellerIds.Count == 0) return [];
        return await _context.Sellers
            .Where(s => s.TenantId == tenantId && sellerIds.Contains(s.Id))
            .ToListAsync();
    }

    public async Task<IEnumerable<Seller>> GetAllAsync(Guid tenantId)
    {
        return await _context.Sellers.Where(seller => seller.TenantId == tenantId).ToListAsync();
    }

    public async Task<(IReadOnlyList<Seller> Items, int TotalCount)> GetPagedAsync(Guid tenantId, int skip, int take)
    {
        var query = _context.Sellers.Where(s => s.TenantId == tenantId);
        var totalCount = await query.CountAsync();
        var items = await query.OrderByDescending(s => s.CreatedAt).Skip(skip).Take(take).ToListAsync();
        return (items, totalCount);
    }

    /// <summary>
    /// Looks up a seller by its external payment-provider account ID (e.g. Stripe acct_*, OpenPix correlationId).
    /// No TenantId filter is applied here because this method is called from webhook handlers where the
    /// TenantId is not known upfront (e.g. Stripe account.updated, OpenPix ACCOUNT_REGISTER_*).
    /// ACCEPTED RISK: Stripe Connect account IDs (acct_*) and OpenPix correlationIds are globally unique
    /// by design — a valid external account ID cannot belong to more than one seller across all tenants,
    /// so the absence of a TenantId filter does not create a cross-tenant data-leak vector here.
    /// </summary>
    public async Task<Seller?> GetByExternalAccountIdAsync(string externalAccountId)
    {
        return await _context.Sellers.FirstOrDefaultAsync(s => s.ExternalAccountId == externalAccountId);
    }

    public async Task SaveChangesAsync()
    {
        await _context.SaveChangesAsync();
    }

    public async Task<bool> IsFoundingNumberTakenAsync(Guid tenantId, int foundingNumber, Guid? excludingSellerId = null)
    {
        var q = _context.Sellers
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId
                && s.IsFoundingSeller
                && s.FoundingNumber == foundingNumber);

        if (excludingSellerId.HasValue)
            q = q.Where(s => s.Id != excludingSellerId.Value);

        return await q.AnyAsync();
    }

    public async Task<decimal> GetCapturedNetSumAsync(Guid tenantId, Guid sellerId, DateTime since)
    {
        // NetAmount é null pra TXs antigas (pré-pricing) — fallback pra Amount preserva
        // comportamento sensato em datasets legados.
        return await _context.Transactions
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId
                && t.SellerId == sellerId
                && t.Status == TransactionStatus.CAPTURED
                && t.CreatedAt >= since)
            .SumAsync(t => (decimal?)(t.NetAmount ?? t.Amount)) ?? 0m;
    }

    public async Task<List<(Guid TenantId, Guid SellerId)>> GetActiveTenantSellerPairsAsync(int batchSize = 5000)
    {
        // OrderBy(CreatedAt) garante ordem determinística entre rodadas (mesmo job, mesmo set).
        var rows = await _context.Sellers
            .AsNoTracking()
            .Where(s => s.Status == SellerStatus.ACTIVE)
            .OrderBy(s => s.CreatedAt)
            .Take(batchSize)
            .Select(s => new { s.TenantId, s.Id })
            .ToListAsync();
        return rows.Select(r => (r.TenantId, r.Id)).ToList();
    }
}