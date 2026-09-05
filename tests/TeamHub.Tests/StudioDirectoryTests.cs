using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.Studio;

namespace TeamHub.Tests;

public sealed class StudioDirectoryTests
{
    [Fact]
    public async Task GetTicketsAsync_ReturnsConfigurationMessage_WhenJiraIntegrationIsDisabled()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"teamhub-studio-{Guid.NewGuid():N}.db");
        var configPath = Path.Combine(Path.GetTempPath(), $"teamhub-jira-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(configPath, """
                {
                  "enabled": false,
                  "baseUrl": "https://example.atlassian.net",
                  "studioMappings": []
                }
                """);

            await using var provider = CreateServices(dbPath, configPath);
            var service = provider.GetRequiredService<IStudioJiraTicketService>();

            var result = await service.GetTicketsAsync(new StudioJiraTicketQuery { StudioProjectName = "Project Zero" });

            result.IsConfigured.Should().BeFalse();
            result.Message.Should().Contain("disabled");
            result.Groups.Should().BeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }
        }
    }

    [Fact]
    public async Task SaveStudioAsync_CreatesStudio_WhenTeamMembersAndLinksAreEmpty()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"teamhub-studio-{Guid.NewGuid():N}.db");
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();
            var service = scope.ServiceProvider.GetRequiredService<IStudioDirectoryService>();

            var saved = await service.SaveStudioAsync(new StudioDetails
            {
                StudioName = "Solo Studio",
                ProjectName = "Project Zero",
                Location = "Remote"
            });

            saved.StudioName.Should().Be("Solo Studio");
            saved.TeamMembers.Should().BeEmpty();
            saved.ImportantLinks.Should().BeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task SaveStudioAsync_ReplacesTeamMembersAndLinks_WhenStudioIsUpdated()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"teamhub-studio-{Guid.NewGuid():N}.db");
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();
            var service = scope.ServiceProvider.GetRequiredService<IStudioDirectoryService>();

            var saved = await service.SaveStudioAsync(new StudioDetails
            {
                StudioName = "North Studio",
                ProjectName = "Project Atlas",
                Location = "Pune",
                TeamMembers =
                [
                    new StudioTeamMember { Name = "Asha", RolesAndResponsibilities = "Producer", EmailId = "asha@example.com" },
                    new StudioTeamMember { Name = "Dev", RolesAndResponsibilities = "QA Lead" }
                ],
                ImportantLinks =
                [
                    new StudioImportantLink { Label = "Plan", Url = "https://example.com/plan", Description = "Delivery plan" },
                    new StudioImportantLink { Label = "Builds", Url = "https://example.com/builds", Description = "Build drops" }
                ]
            });

            var updated = await service.SaveStudioAsync(new StudioDetails
            {
                Id = saved.Id,
                StudioName = "North Studio",
                ProjectName = "Project Atlas",
                Location = "Mumbai",
                TeamMembers =
                [
                    new StudioTeamMember { Name = "Asha", RolesAndResponsibilities = "Production owner", EmailId = "asha@example.com" }
                ],
                ImportantLinks =
                [
                    new StudioImportantLink { Label = "Plan", Url = "https://example.com/plan-v2", Description = "Updated delivery plan" }
                ]
            });

            updated.Location.Should().Be("Mumbai");
            updated.TeamMembers.Should().ContainSingle()
                .Which.RolesAndResponsibilities.Should().Be("Production owner");
            updated.ImportantLinks.Should().ContainSingle()
                .Which.Url.Should().Be("https://example.com/plan-v2");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    private static ServiceProvider CreateServices(string dbPath, string? jiraConfigPath = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:WorkflowDb"] = $"Data Source={dbPath}",
                ["StudioJiraConfiguration:ConfigPath"] = jiraConfigPath
            })
            .Build();

        var services = new ServiceCollection();
        services.AddStudioDirectory(configuration);
        return services.BuildServiceProvider();
    }
}