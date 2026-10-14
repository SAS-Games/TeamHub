using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.AI.ReportGenerator.Contracts;
using TeamHub.AI.ReportGenerator.Application;
using TeamHub.AI.Contracts;
using TeamHub.AI.ReportGenerator.Persistence;

namespace TeamHub.AI.ReportGenerator;

public static class ReportGeneratorServiceCollectionExtensions
{
    public static IServiceCollection AddReportGenerator(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddTeamHubAiConfiguration(configuration);
        services.AddOptions<AiReportGeneratorOptions>()
            .Bind(configuration.GetSection(AiReportGeneratorOptions.SectionName))
            .Validate(options => options.MaximumConcurrentRequests is >= 1 and <= 16,
                "AI:ReportGenerator:MaximumConcurrentRequests must be between 1 and 16.")
            .Validate(options => options.MaximumSourceCharactersPerStudio is >= 1 and <= 120000,
                "AI:ReportGenerator:MaximumSourceCharactersPerStudio must be between 1 and 120000.")
            .Validate(options => options.MaximumOutputTokens is >= 1 and <= 16000,
                "AI:ReportGenerator:MaximumOutputTokens must be between 1 and 16000.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.WeeklyWorkSummaryPromptPath),
                "AI:ReportGenerator:WeeklyWorkSummaryPromptPath is required.")
            .ValidateOnStart();

        services.AddDbContext<AiReportDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("AiReportsDb")
                ?? "Data Source=data/ai-reports.db";
            options.UseSqlite(connectionString);
        });
        services.AddScoped<IReportGeneratorDatabaseInitializer, SqliteReportGeneratorDatabaseInitializer>();
        services.AddScoped<IAiReportRunRepository, SqliteAiReportRunRepository>();
        services.AddSingleton<ReportGenerationGate>();
        services.AddScoped<IWeeklyWorkSummaryWorkflow, WeeklyWorkSummaryWorkflow>();

        return services;
    }
}
