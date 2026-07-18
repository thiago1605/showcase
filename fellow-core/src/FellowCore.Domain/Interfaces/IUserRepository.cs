using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface IUserRepository
{
    /// <summary>
    /// No TenantId filter — email is unique globally by design. A user can belong to multiple
    /// tenants via role assignments; the login flow resolves the user identity first, then
    /// checks tenant membership separately. ACCEPTED RISK: intentional global uniqueness.
    /// </summary>
    Task<User?> GetByEmailAsync(string email);
    Task<User?> GetByIdAsync(Guid userId);
    Task<User?> GetByRefreshTokenHashAsync(string tokenHash);

    /// <summary>
    /// Busca user pelo "sub" do Google ID Token (vínculo estável OAuth).
    /// Retorna null se nenhum user tem aquele Google subject vinculado.
    /// </summary>
    Task<User?> GetByGoogleSubjectAsync(string googleSubject);

    /// <summary>
    /// Retorna o TenantId default para novos users criados via SSO em modo
    /// single-tenant. Hoje retorna o primeiro tenant ativo. Multi-tenant
    /// precisaria de fluxo distinto (invite link, domínio do email etc).
    /// </summary>
    Task<Guid?> GetDefaultTenantIdAsync();

    Task<List<User>> ListByTenantAsync(Guid tenantId);
    Task AddAsync(User user);

    /// <summary>Add sem await (Entity Framework tracking puro). Para fluxos
    /// onde o caller controla quando o SaveChanges acontece.</summary>
    void Add(User user);

    Task SaveChangesAsync();
}
