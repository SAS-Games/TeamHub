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
        services.AddScoped<IAiUsageLimitService>(provider => provider.GetRequiredService<AiUsageLedger>());
        services.AddScoped<IAiUsageWriter>(provider => provider.GetRequiredService<AiUsageLedger>());
        services.AddSingleton<AiUsageUserGate>();
        services.AddScoped<IAiUsageTracker, AiUsageTracker>();
        services.AddScoped<IAiUsageDatabaseInitializer, AiUsageDatabaseInitializer>();
        return services;
    }

    public static IServiceCollection AddAiUsageTrackingFallback(this IServiceCollection services)
    {
        services.TryAddScoped<IAiUsageTracker, NullAiUsageTracker>();
        return services;
    }
}
