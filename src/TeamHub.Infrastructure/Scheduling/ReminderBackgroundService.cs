using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamHub.Application.Interfaces;
using TeamHub.Infrastructure.Options;

namespace TeamHub.Infrastructure.Scheduling;

public sealed class ReminderBackgroundService(
    IServiceProvider serviceProvider,
    IOptions<WorkflowConfigurationOptions> options,
    ILogger<ReminderBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = Math.Max(1, options.Value.ReminderCheckIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
                var sent = await engine.RunReminderCycleAsync(stoppingToken);
                logger.LogInformation("Reminder cycle finished. Notifications sent: {Count}", sent);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reminder cycle failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }
}
