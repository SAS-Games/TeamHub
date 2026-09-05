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
        services.Configure<EmailNotificationOptions>(configuration.GetSection(EmailNotificationOptions.SectionName));

        services.AddDbContext<WorkflowDbContext>(opts =>
        {
            var conn = configuration.GetConnectionString("WorkflowDb") ?? "Data Source=data/workflow.db";
            opts.UseSqlite(conn);
        });

        services.AddScoped<IWorkflowDefinitionProvider, ExcelWorkflowDefinitionProvider>();
        services.AddScoped<IWorkflowConfigurationService, WorkflowConfigurationService>();
        services.AddScoped<IWorkflowEngine, WorkflowEngineService>();
        services.AddScoped<IWorkflowReadService, WorkflowReadService>();
        services.AddScoped<INotificationService, SmtpNotificationService>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddHostedService<ReminderBackgroundService>();

        return services;
    }
}
