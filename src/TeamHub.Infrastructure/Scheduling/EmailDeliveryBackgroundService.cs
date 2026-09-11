using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamHub.Infrastructure.Services;

namespace TeamHub.Infrastructure.Scheduling;

public sealed class EmailDeliveryBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<EmailDeliveryBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IEmailOutboxProcessor>();
                var sent = await processor.ProcessPendingAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("Email delivery cycle sent {Count} notification(s)", sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Email delivery cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
