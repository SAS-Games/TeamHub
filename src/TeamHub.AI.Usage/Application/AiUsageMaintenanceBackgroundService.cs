using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamHub.AI.Usage.Contracts;

namespace TeamHub.AI.Usage.Application;

internal sealed class AiUsageMaintenanceBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<AiUsageOptions> options,
    TimeProvider timeProvider,
    ILogger<AiUsageMaintenanceBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromHours(options.Value.MaintenanceIntervalHours),
            timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var ledger = scope.ServiceProvider.GetRequiredService<IAiUsageLedger>();
                var cutoff = timeProvider.GetUtcNow().AddMonths(-options.Value.RetentionMonths);
                var deleted = await ledger.DeleteBeforeAsync(cutoff, stoppingToken);
                logger.LogInformation(
                    "AI usage retention maintenance completed. Deleted {OperationCount} operation(s) before {CutoffUtc}.",
                    deleted,
                    cutoff);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AI usage retention maintenance failed.");
            }
        }
    }
}
