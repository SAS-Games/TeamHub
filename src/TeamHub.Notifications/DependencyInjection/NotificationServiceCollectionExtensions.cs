using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.Notifications.Persistence;
using TeamHub.Notifications.Services;

namespace TeamHub.Notifications.DependencyInjection;

public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddTeamHubNotifications(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<NotificationsDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IWorkflowNotificationInbox, WorkflowNotificationInboxService>();
        services.AddScoped<INotificationDatabaseInitializer, NotificationDatabaseInitializer>();
        return services;
    }
}
