using FellowCore.Application.Modules.Settlements.Interfaces;
using FellowCore.Infrastructure.Workers.Options; 
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options; 

namespace FellowCore.Infrastructure.Workers.BackgroundServices;

public class SettlementWorker(
    IServiceScopeFactory scopeFactory, 
    TimeProvider timeProvider, 
    IOptions<SettlementWorkerOptions> options, 
    ILogger<SettlementWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int targetHour = options.Value.TargetHourUtc;

        logger.LogInformation(message:"🤖 Settlement Worker iniciado. Configurado para rodar às {Hour}h UTC...", args: targetHour);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset now = timeProvider.GetUtcNow(); 
                
                DateTimeOffset nextRun = new(year: now.Year, month: now.Month, day: now.Day, hour: targetHour, minute: 0, second: 0, offset: TimeSpan.Zero);

                // Se a hora alvo de hoje já passou, agenda para amanhã
                if (now >= nextRun) nextRun = nextRun.AddDays(1);

                TimeSpan delay = nextRun - now;

                logger.LogInformation(message: "⏳ Próxima execução programada para: {NextRun}", args: nextRun);

                await Task.Delay(delay, timeProvider, stoppingToken); 

                logger.LogInformation(message: "🕛 Disparando o motor de liquidação...");

                using IServiceScope scope = scopeFactory.CreateScope();
                var settlementService = scope.ServiceProvider.GetRequiredService<ISettlementService>();

                await settlementService.ProcessDailySettlementsAsync();
            }
            catch (TaskCanceledException)
            {
                break; 
            }
            catch (Exception ex)
            {
                logger.LogCritical(exception: ex, message: "💥 Falha crítica no Settlement Worker!");
                await Task.Delay(delay: TimeSpan.FromMinutes(5), timeProvider: timeProvider, cancellationToken: stoppingToken); 
            }
        }
    }
}