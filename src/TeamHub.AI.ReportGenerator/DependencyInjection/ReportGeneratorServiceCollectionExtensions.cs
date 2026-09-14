using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.AI.ReportGenerator.Contracts;
using TeamHub.AI.ReportGenerator.Persistence;

namespace TeamHub.AI.ReportGenerator;

public static class ReportGeneratorServiceCollectionExtensions
{
    public static IServiceCollection AddReportGenerator(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ReportGeneratorOptions>()
            .Bind(configuration.GetSection(ReportGeneratorOptions.SectionName))
            .Validate(options => options.MaximumConcurrentRequests is >= 1 and <= 16,
                "AI:ReportGenerator:MaximumConcurrentRequests must be between 1 and 16.")
            .ValidateOnStart();

        services.AddDbContext<AiReportDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("AiReportsDb")
                ?? "Data Source=data/ai-reports.db";
            options.UseSqlite(connectionString);
        });
        services.AddScoped<IReportGeneratorDatabaseInitializer, SqliteReportGeneratorDatabaseInitializer>();
        services.AddScoped<IAiReportRunRepository, SqliteAiReportRunRepository>();

        return services;
    }
}