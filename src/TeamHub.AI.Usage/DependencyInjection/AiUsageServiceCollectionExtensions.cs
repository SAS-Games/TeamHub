using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeamHub.AI.Usage.Application;
using TeamHub.AI.Usage.Contracts;
using TeamHub.AI.Usage.Persistence;

namespace TeamHub.AI.Usage;

public static class AiUsageServiceCollectionExtensions
{
    public static IServiceCollection AddAiUsageLedger(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AiUsageOptions>()
            .Bind(configuration.GetSection(AiUsageOptions.SectionName))
            .Validate(options => options.RetentionMonths is >= 6 and <= 120,
                "AI usage retention must be between 6 and 120 months.")
            .Validate(options => options.MaintenanceIntervalHours is >= 1 and <= 168,
                "AI usage maintenance interval must be between 1 and 168 hours.")
            .ValidateOnStart();
        services.AddDbContext<AiUsageDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("AiUsageDb")
                ?? "Data Source=data/ai-usage.db";
            options.UseSqlite(connectionString);
        });
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AiUsageLedger>();
        services.AddScoped<IAiUsageLedger>(provider => provider.GetRequiredService<AiUsageLedger>());
        services.AddScoped<IAiToolActivityLedger>(provider => provider.GetRequiredService<AiUsageLedger>());
        services.AddScoped<IAiUsageLimitService>(provider => provider.GetRequiredService<AiUsageLedger>());
        services.AddScoped<IAiUsageWriter>(provider => provider.GetRequiredService<AiUsageLedger>());
        services.AddScoped<IAiToolActivityWriter>(provider => provider.GetRequiredService<AiUsageLedger>());
        services.AddSingleton<AiUsageUserGate>();
        services.TryAddScoped<IAiUsageThresholdNotifier, NullAiUsageThresholdNotifier>();
        services.AddScoped<IAiUsageThresholdEvaluator, AiUsageThresholdEvaluator>();
        services.AddScoped<IAiUsageTracker, AiUsageTracker>();
        services.AddScoped<IAiToolActivityTracker, AiToolActivityTracker>();
        services.AddScoped<IAiUsageDatabaseInitializer, AiUsageDatabaseInitializer>();
        services.AddHostedService<AiUsageMaintenanceBackgroundService>();
        return services;
    }

    public static IServiceCollection AddAiUsageTrackingFallback(this IServiceCollection services)
    {
        services.TryAddScoped<IAiUsageTracker, NullAiUsageTracker>();
        services.TryAddScoped<IAiToolActivityTracker, NullAiToolActivityTracker>();
        return services;
    }
}
