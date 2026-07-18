namespace FellowCore.Application.Modules.Transactions.Interfaces;

public interface IDunningProcessor
{
    Task ProcessDunningAsync(CancellationToken ct = default);
}
