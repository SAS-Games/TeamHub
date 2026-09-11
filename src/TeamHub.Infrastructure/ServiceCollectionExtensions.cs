using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.Application.Interfaces;
using TeamHub.Infrastructure.Excel;
using TeamHub.Infrastructure.Options;
using TeamHub.Infrastructure.Persistence;
using TeamHub.Infrastructure.Scheduling;
using TeamHub.Infrastructure.Services;

namespace TeamHub.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkflowConfigurationOptions>(configuration.GetSection(WorkflowConfigurationOptions.SectionName));

        services.AddDbContext<WorkflowDbContext>(opts =>
        {
            var conn = configuration.GetConnectionString("WorkflowDb") ?? "Data Source=data/workflow.db";
            opts.UseSqlite(conn);
        });

        services.AddScoped<IWorkflowDefinitionProvider, ExcelWorkflowDefinitionProvider>();
        services.AddScoped<IWorkflowConfigurationService, WorkflowConfigurationService>();
        services.AddScoped<IWorkflowEngine, WorkflowEngineService>();
        services.AddScoped<IWorkflowReadService, WorkflowReadService>();
        services.AddScoped<EmailNotificationConfigurationService>();
        services.AddScoped<IEmailNotificationConfigurationService>(provider =>
            provider.GetRequiredService<EmailNotificationConfigurationService>());
        services.AddScoped<IEmailCredentialAccessor>(provider =>
            provider.GetRequiredService<EmailNotificationConfigurationService>());
        services.AddScoped<INotificationRecipientResolver, PassthroughNotificationRecipientResolver>();
        services.AddScoped<INotificationService, SmtpNotificationService>();
        services.AddScoped<IEmailOutboxProcessor, SmtpEmailOutboxProcessor>();
        services.AddScoped<IWorkflowDatabaseInitializer, WorkflowDatabaseInitializer>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddHostedService<ReminderBackgroundService>();
        services.AddHostedService<EmailDeliveryBackgroundService>();

        return services;
    }
}
