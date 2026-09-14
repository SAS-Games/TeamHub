using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.AIReports.Application;
using TeamHub.AIReports.Contracts;
using TeamHub.AIReports.ModelProviders;
using TeamHub.AIReports.Persistence;

namespace TeamHub.AIReports;

public static class AiReportsServiceCollectionExtensions
{
    internal const string HttpClientName = "TeamHub.AIReports";

    public static IServiceCollection AddAiReports(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AiReportOptions>()
            .Bind(configuration.GetSection(AiReportOptions.SectionName))
            .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.Provider),
                "AIReports:Provider is required when AI Reports is enabled.")
            .Validate(options => !options.Enabled || Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
                    && endpoint.Scheme is "http" or "https",
                "AIReports:Endpoint must be an absolute HTTP or HTTPS URL when AI Reports is enabled.")
            .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.Model),
                "AIReports:Model is required when AI Reports is enabled.")
            .Validate(options => options.Temperature is >= 0 and <= 2,
                "AIReports:Temperature must be between 0 and 2.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 1800,
                "AIReports:TimeoutSeconds must be between 1 and 1800.")
            .Validate(options => options.MaximumConcurrentRequests is >= 1 and <= 16,
                "AIReports:MaximumConcurrentRequests must be between 1 and 16.")
            .ValidateOnStart();

        services.AddHttpClient(HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<IAiProviderCredentialAccessor, EnvironmentAiProviderCredentialAccessor>();
        services.AddSingleton<IAiModelProvider, OllamaModelProvider>();
        services.AddSingleton<IAiModelProvider, OpenAiCompatibleModelProvider>();
        services.AddSingleton<IAiModelProviderRegistry, AiModelProviderRegistry>();
        services.AddSingleton<IAiReportModelService, AiReportModelService>();

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
