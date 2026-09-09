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

    [Fact]
    public async Task InitializeAsync_CreatesStudioTablesOnlyInStudioDatabase()
    {
        var studioDbPath = Path.Combine(Path.GetTempPath(), $"teamhub-studio-{Guid.NewGuid():N}.db");
        var workflowDbPath = Path.Combine(Path.GetTempPath(), $"teamhub-workflow-{Guid.NewGuid():N}.db");

        try
        {
            await using (var workflowConnection = new SqliteConnection($"Data Source={workflowDbPath}"))
            {
                await workflowConnection.OpenAsync();
                await using var command = workflowConnection.CreateCommand();
                command.CommandText = "CREATE TABLE WorkflowOnly (Id TEXT PRIMARY KEY);";
                await command.ExecuteNonQueryAsync();
            }

            await using var provider = CreateServices(studioDbPath, workflowDbPath: workflowDbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();

            var studioTables = await GetTableNamesAsync(studioDbPath);
            studioTables.Should().Contain(["Studios", "StudioTeamMembers", "StudioDevelopmentTools", "StudioImportantLinks"]);

            var workflowTables = await GetTableNamesAsync(workflowDbPath);
            workflowTables.Should().ContainSingle().Which.Should().Be("WorkflowOnly");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(studioDbPath))
            {
                File.Delete(studioDbPath);
            }
            if (File.Exists(workflowDbPath))
            {
                File.Delete(workflowDbPath);
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_MovesLegacyStudioDataOutOfWorkflowDatabase()
    {
        var studioDbPath = Path.Combine(Path.GetTempPath(), $"teamhub-studio-{Guid.NewGuid():N}.db");
        var workflowDbPath = Path.Combine(Path.GetTempPath(), $"teamhub-workflow-{Guid.NewGuid():N}.db");

        try
        {
            await using (var legacyProvider = CreateServices(workflowDbPath))
            await using (var legacyScope = legacyProvider.CreateAsyncScope())
            {
                await legacyScope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();
                await legacyScope.ServiceProvider.GetRequiredService<IStudioDirectoryService>().SaveStudioAsync(new StudioDetails
                {
                    StudioName = "Legacy Studio",
                    ProjectName = "Legacy Project",
                    Location = "Pune",
                    TeamMembers =
                    [
                        new StudioTeamMember
                        {
                            Name = "Asha",
                            RolesAndResponsibilities = "Producer",
                            EmailId = "asha@example.com"
                        }
                    ],
                    DevelopmentTools =
                    [
                        new StudioDevelopmentTool { Name = "Build Monitor", Description = "Tracks builds" }
                    ],
                    ImportantLinks =
                    [
                        new StudioImportantLink
                        {
                            Label = "Plan",
                            Url = "https://example.com/plan",
                            Description = "Delivery plan"
                        }
                    ]
                });
            }

            await using (var provider = CreateServices(studioDbPath, workflowDbPath: workflowDbPath))
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();
                var studios = await scope.ServiceProvider.GetRequiredService<IStudioDirectoryService>().GetStudiosAsync();

                studios.Should().ContainSingle();
                studios[0].StudioName.Should().Be("Legacy Studio");
                studios[0].TeamMembers.Should().ContainSingle();
                studios[0].DevelopmentTools.Should().ContainSingle();
                studios[0].ImportantLinks.Should().ContainSingle();
            }

            var workflowTables = await GetTableNamesAsync(workflowDbPath);
            workflowTables.Should().NotContain(table => table.StartsWith("Studio", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(studioDbPath))
            {
                File.Delete(studioDbPath);
            }
            if (File.Exists(workflowDbPath))
            {
                File.Delete(workflowDbPath);
            }
        }
    }

    private static async Task<IReadOnlyList<string>> GetTableNamesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static ServiceProvider CreateServices(string dbPath, string? jiraConfigPath = null, string? workflowDbPath = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:StudioDb"] = $"Data Source={dbPath}",
                ["ConnectionStrings:WorkflowDb"] = workflowDbPath is null ? null : $"Data Source={workflowDbPath}",
                ["StudioJiraConfiguration:ConfigPath"] = jiraConfigPath
            })
            .Build();

        var services = new ServiceCollection();
        services.AddStudioDirectory(configuration);
        return services.BuildServiceProvider();
    }
}
