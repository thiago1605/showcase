using FellowCore.Domain.Entities;

namespace FellowCore.Domain.Interfaces;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid tenantId, Guid id);
    Task<Customer?> GetByEmailAsync(Guid tenantId, string email);
    Task<(IReadOnlyList<Customer> Items, int TotalCount)> GetPagedAsync(Guid tenantId, int skip, int take);
    void Add(Customer customer);
    void Update(Customer customer);
    Task SaveChangesAsync();
}
