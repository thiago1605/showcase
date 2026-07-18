using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class UserRepository(AppDbContext db) : IUserRepository
{
    /// <summary>
    /// Looks up a user by email address without a TenantId filter.
    /// ACCEPTED RISK: Email addresses are unique globally by design — a user authenticates with
    /// a single identity and can belong to multiple tenants via role assignments. Filtering by
    /// TenantId at this layer would prevent cross-tenant user lookup which is intentionally
    /// supported (e.g. login flow resolves user first, then checks tenant membership).
    /// </summary>
    public async Task<User?> GetByEmailAsync(string email)
        => await db.Users.FirstOrDefaultAsync(u => u.Email == email);

    public async Task<User?> GetByIdAsync(Guid userId)
        => await db.Users.FindAsync(userId);

    public async Task<User?> GetByRefreshTokenHashAsync(string tokenHash)
        => await db.Users.FirstOrDefaultAsync(u =>
            u.RefreshTokenHash == tokenHash &&
            u.RefreshTokenExpiry != null &&
            u.RefreshTokenExpiry > DateTime.UtcNow);

    public async Task<User?> GetByGoogleSubjectAsync(string googleSubject)
        => await db.Users.FirstOrDefaultAsync(u => u.GoogleSubject == googleSubject);

    public async Task<Guid?> GetDefaultTenantIdAsync()
    {
        // Single-tenant default: primeiro tenant ordenado por criação. Em
        // produção multi-tenant, esse fluxo deveria vir de um invite explícito
        // (link/email) ou domain match. TODO quando aplicável.
        var tenant = await db.Tenants
            .OrderBy(t => t.CreatedAt)
            .Select(t => new { t.Id })
            .FirstOrDefaultAsync();
        return tenant?.Id;
    }

    public async Task<List<User>> ListByTenantAsync(Guid tenantId)
        => await db.Users.Where(u => u.TenantId == tenantId).ToListAsync();

    public async Task AddAsync(User user)
    {
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    public void Add(User user) => db.Users.Add(user);

    public async Task SaveChangesAsync() => await db.SaveChangesAsync();
}
