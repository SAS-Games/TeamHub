using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.AIReports.Contracts;
using TeamHub.AIReports.Persistence;

namespace TeamHub.AIReports;

public static class AiReportsServiceCollectionExtensions
{
    public static IServiceCollection AddAiReports(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AiReportsOptions>()
            .Bind(configuration.GetSection(AiReportsOptions.SectionName))
            .Validate(options => options.MaximumConcurrentRequests is >= 1 and <= 16,
                "AIReports:MaximumConcurrentRequests must be between 1 and 16.")
            .ValidateOnStart();

        services.AddDbContext<AiReportDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("AiReportsDb")
                ?? "Data Source=data/ai-reports.db";
            options.UseSqlite(connectionString);
        });
        services.AddScoped<IAiReportDatabaseInitializer, SqliteAiReportDatabaseInitializer>();
        services.AddScoped<IAiReportRunRepository, SqliteAiReportRunRepository>();

        return services;
    }
}