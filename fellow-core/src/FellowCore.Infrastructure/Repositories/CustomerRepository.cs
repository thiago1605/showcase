using FellowCore.Domain.Entities;
using FellowCore.Domain.Interfaces;
using FellowCore.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FellowCore.Infrastructure.Repositories;

public class CustomerRepository(AppDbContext context) : ICustomerRepository
{
    public async Task<Customer?> GetByIdAsync(Guid tenantId, Guid id)
    {
        return await context.Customers
            .Include(c => c.PaymentMethods)
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == id);
    }

    public async Task<Customer?> GetByEmailAsync(Guid tenantId, string email)
    {
        return await context.Customers
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Email == email);
    }

    public async Task<(IReadOnlyList<Customer> Items, int TotalCount)> GetPagedAsync(Guid tenantId, int skip, int take)
    {
        var query = context.Customers.Where(c => c.TenantId == tenantId);
        var totalCount = await query.CountAsync();
        var items = await query.OrderByDescending(c => c.CreatedAt).Skip(skip).Take(take).ToListAsync();
        return (items, totalCount);
    }

    public void Add(Customer customer) => context.Customers.Add(customer);
    public void Update(Customer customer) => context.Customers.Update(customer);

    public async Task SaveChangesAsync() => await context.SaveChangesAsync();
}
