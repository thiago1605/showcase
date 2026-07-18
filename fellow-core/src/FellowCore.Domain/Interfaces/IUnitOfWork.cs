namespace FellowCore.Domain.Interfaces;

public interface IUnitOfWork : IDisposable
{
    Task BeginAsync();
    Task CommitAsync();
    Task RollbackAsync();
}