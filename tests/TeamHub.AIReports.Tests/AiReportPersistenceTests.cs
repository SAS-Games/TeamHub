using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.AI.Contracts;
using TeamHub.AIReports.Persistence;

namespace TeamHub.AIReports.Tests;

public sealed class AiReportPersistenceTests
{
    [Fact]
    public async Task Initializer_and_repository_use_the_configured_separate_database()
    {
        var directory = Path.Combine(Path.GetTempPath(), "teamhub-ai-reports-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "ai-reports.db");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AIReports:Enabled"] = "false",
                    ["ConnectionStrings:AiReportsDb"] = $"Data Source={databasePath};Pooling=False"
                })
                .Build();
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddLogging();
            serviceCollection.AddAiReports(configuration);
            await using (var services = serviceCollection.BuildServiceProvider())
            {

            await using (var scope = services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IAiReportDatabaseInitializer>().InitializeAsync();
            }

            var expected = new AiReportRunSnapshot(
                Guid.NewGuid(),
                "Weekly",
                new DateOnly(2026, 9, 7),
                new DateOnly(2026, 9, 13),
                "All active studios",
                AiReportRunStatus.Pending,
                AiModelProviderKeys.Ollama,
                "qwen3:8b",
                "weekly-v1",
                "source-hash",
                "admin",
                DateTimeOffset.UtcNow,
                null,
                null);

            await using (var scope = services.CreateAsyncScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IAiReportRunRepository>();
                await repository.AddAsync(expected);
                var actual = await repository.GetAsync(expected.Id);
                actual.Should().BeEquivalentTo(expected);
            }

            File.Exists(databasePath).Should().BeTrue();
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
