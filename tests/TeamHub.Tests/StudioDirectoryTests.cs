using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using System.Text.Json;
using TeamHub.Studio;

namespace TeamHub.Tests;

public sealed class StudioDirectoryTests
{
    [Fact]
    public async Task AtlassianTokens_AreEncryptedInDatabase_AndConnectionStatusDoesNotExposeThem()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"teamhub-studio-{Guid.NewGuid():N}.db");
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();
            var service = scope.ServiceProvider.GetRequiredService<IAtlassianConfigurationService>();

            await service.SaveUserTokensAsync("person@example.com", "jira-secret-token", "confluence-secret-token");
            var status = await service.GetConnectionStatusAsync("PERSON@example.com");

            status.HasJiraToken.Should().BeTrue();
            status.HasConfluenceToken.Should().BeTrue();

            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT JiraTokenProtected, ConfluenceTokenProtected FROM AtlassianUserCredentials LIMIT 1;";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().NotBe("jira-secret-token").And.NotContain("jira-secret-token");
            reader.GetString(1).Should().NotBe("confluence-secret-token").And.NotContain("confluence-secret-token");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task DefaultAtlassianTokens_AreEncrypted_AndAssignedOnlyThroughPrivilegedAccessList()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"teamhub-studio-{Guid.NewGuid():N}.db");
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();
            var service = scope.ServiceProvider.GetRequiredService<IAtlassianConfigurationService>();

            await service.SaveDefaultTokensAsync("shared-jira-token", "shared-confluence-token");
            await service.SavePrivilegedAccessAsync([
                new AtlassianPrivilegedAccess("privileged@example.com", true, true)
            ]);

            var status = await service.GetDefaultCredentialStatusAsync();
            status.HasJiraToken.Should().BeTrue();
            status.HasConfluenceToken.Should().BeTrue();
            (await service.ListPrivilegedAccessAsync()).Should().ContainSingle()
                .Which.Should().Be(new AtlassianPrivilegedAccess("privileged@example.com", true, true));

            var credentialAccessor = scope.ServiceProvider.GetRequiredService<IAtlassianCredentialAccessor>();
            var jiraCredential = await credentialAccessor.ResolveJiraCredentialAsync("privileged@example.com", true);
            var confluenceCredential = await credentialAccessor.ResolveConfluenceCredentialAsync("privileged@example.com", true);
            jiraCredential.Should().NotBeNull();
            jiraCredential!.IsShared.Should().BeTrue();
            jiraCredential.IsReadOnly.Should().BeTrue();
            confluenceCredential.Should().NotBeNull();
            confluenceCredential!.IsShared.Should().BeTrue();
            confluenceCredential.IsReadOnly.Should().BeTrue();

            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DefaultJiraTokenProtected, DefaultConfluenceTokenProtected FROM AtlassianIntegrationSettings WHERE Id = 1;";
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().NotContain("shared-jira-token");
            reader.GetString(1).Should().NotContain("shared-confluence-token");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task SaveSettings_AllowsJiraOnlyConfiguration_WhenConfluenceIsDisabled()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"teamhub-studio-{Guid.NewGuid():N}.db");
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();
            var service = scope.ServiceProvider.GetRequiredService<IAtlassianConfigurationService>();

            await service.SaveSettingsAsync(new AtlassianIntegrationSettings
            {
                JiraEnabled = true,
                JiraBaseUrl = "https://jira.example.test",
                ConfluenceEnabled = false,
                ConfluenceBaseUrl = string.Empty
            });

            var saved = await service.GetSettingsAsync();
            saved.JiraEnabled.Should().BeTrue();
            saved.JiraBaseUrl.Should().Be("https://jira.example.test");
            saved.ConfluenceEnabled.Should().BeFalse();
            saved.ConfluenceBaseUrl.Should().BeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task SaveSettings_PersistsGlobalConfluenceWeeklyPageConfiguration()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"teamhub-studio-{Guid.NewGuid():N}.db");
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();
            var service = scope.ServiceProvider.GetRequiredService<IAtlassianConfigurationService>();

            await service.SaveSettingsAsync(new AtlassianIntegrationSettings
            {
                ConfluenceEnabled = true,
                ConfluenceBaseUrl = "https://confluence.example.test",
                ConfluenceSpaceKey = "WR",
                ConfluenceParentPageId = "12345",
                ConfluenceYearTitlePattern = "Year {Year}",
                ConfluenceMonthTitlePattern = "{Month:00}-{Year}",
                ConfluenceWeeklyTitlePattern = "{WeekStart:dd/MM}-{WeekEnd:dd/MM}"
            });

            var saved = await service.GetSettingsAsync();
            saved.ConfluenceEnabled.Should().BeTrue();
            saved.ConfluenceBaseUrl.Should().Be("https://confluence.example.test");
            saved.ConfluenceSpaceKey.Should().Be("WR");
            saved.ConfluenceParentPageId.Should().Be("12345");
            saved.ConfluenceYearTitlePattern.Should().Be("Year {Year}");
            saved.ConfluenceMonthTitlePattern.Should().Be("{Month:00}-{Year}");
            saved.ConfluenceWeeklyTitlePattern.Should().Be("{WeekStart:dd/MM}-{WeekEnd:dd/MM}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task GetUpdatesAsync_ReadsConfiguredStudioRowFromMatchingWeeklyConfluencePage()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"teamhub-studio-{Guid.NewGuid():N}.db");
        var storageBody = """
            <table><tbody>
              <tr><th>Studio</th><th>Overview (for WR)</th><th>Notes</th></tr>
              <tr><td>HDC</td><td><strong>Studio Work:</strong><br/>Completed milestone<br/><strong>Action Item:</strong><br/>Review build</td><td>On track</td></tr>
            </tbody></table>
            """;
        var responseBody = JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new
                {
                    id = "weekly-1",
                    title = "14/09-18/09",
                    ancestors = new[]
                    {
                        new { id = "100", title = "Activities" },
                        new { id = "101", title = "2026" },
                        new { id = "102", title = "9/2026" }
                    },
                    body = new { storage = new { value = storageBody } },
                    version = new { number = 7 },
                    _links = new { webui = "/pages/viewpage.action?pageId=weekly-1" }
                }
            }
        });
        var handler = new StubHttpMessageHandler(request =>
        {
            request.Headers.Authorization?.Scheme.Should().Be("Bearer");
            request.Headers.Authorization?.Parameter.Should().Be("personal-confluence-token");
            request.RequestUri!.AbsoluteUri.Should().Contain("title=14%2F09-18%2F09");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        });

        try
        {
            await using var provider = CreateServices(dbPath, confluenceHandler: handler);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();
            var directory = scope.ServiceProvider.GetRequiredService<IStudioDirectoryService>();
            var configuration = scope.ServiceProvider.GetRequiredService<IAtlassianConfigurationService>();
            var studio = await directory.SaveStudioAsync(new StudioDetails { StudioName = "HDC Studio", ProjectName = "Game" });

            await configuration.SaveSettingsAsync(new AtlassianIntegrationSettings
            {
                ConfluenceEnabled = true,
                ConfluenceBaseUrl = "https://confluence.example.test",
                ConfluenceSpaceKey = "WR",
                ConfluenceParentPageId = "100"
            });
            await configuration.SaveStudioMappingsAsync([
                new StudioAtlassianMapping { StudioId = studio.Id, ConfluenceStudioIdentifier = "HDC" }
            ]);
            await configuration.SaveUserTokensAsync("person@example.com", null, "personal-confluence-token");

            var result = await scope.ServiceProvider.GetRequiredService<IStudioConfluenceUpdateService>().GetUpdatesAsync(
                new StudioConfluenceUpdateQuery
                {
                    StudioId = studio.Id,
                    RequestingUserId = "person@example.com",
                    StartDate = new DateOnly(2026, 9, 16),
                    EndDate = new DateOnly(2026, 9, 18)
                });

            result.Message.Should().BeNull();
            result.Updates.Should().ContainSingle();
            result.Updates[0].StudioFound.Should().BeTrue();
            result.Updates[0].PageVersion.Should().Be(7);
            result.Updates[0].StudioWork.Should().Be("Completed milestone");
            result.Updates[0].ActionItems.Should().Be("Review build");
            result.Updates[0].Notes.Should().Be("On track");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public async Task GetTicketsAsync_ReturnsConfigurationMessage_WhenJiraIntegrationIsDisabled()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"teamhub-studio-{Guid.NewGuid():N}.db");
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();
            var service = scope.ServiceProvider.GetRequiredService<IStudioJiraTicketService>();

            var result = await service.GetTicketsAsync(new StudioJiraTicketQuery { StudioId = Guid.NewGuid().ToString(), RequestingUserId = "person@example.com" });

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
            saved.TimeZoneId.Should().Be(TimeZoneInfo.Utc.Id);
            saved.OurContacts.Should().BeEmpty();
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
            var configuredTimeZone = TimeZoneInfo.GetSystemTimeZones()
                .FirstOrDefault(zone => zone.Id != TimeZoneInfo.Utc.Id)?.Id ?? TimeZoneInfo.Utc.Id;

            var saved = await service.SaveStudioAsync(new StudioDetails
            {
                StudioName = "North Studio",
                ProjectName = "Project Atlas",
                Location = "Pune",
                TimeZoneId = configuredTimeZone,
                OurContacts =
                [
                    new StudioContact { Name = "Priya", Role = "PIC" },
                    new StudioContact { Name = "Noah", Role = "Producer" }
                ],
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
                TimeZoneId = configuredTimeZone,
                OurContacts =
                [
                    new StudioContact { Name = "Priya", Role = "PIC" }
                ],
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
            updated.TimeZoneId.Should().Be(configuredTimeZone);
            updated.OurContacts.Should().ContainSingle()
                .Which.Role.Should().Be("PIC");
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
            studioTables.Should().Contain(["Studios", "StudioContacts", "StudioTeamMembers", "StudioDevelopmentTools", "StudioImportantLinks"]);

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
    public async Task InitializeAsync_AddsTimeZoneToExistingStudiosTable()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"teamhub-studio-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Studios (
                        Id TEXT NOT NULL CONSTRAINT PK_Studios PRIMARY KEY,
                        StudioName TEXT NOT NULL,
                        ProjectName TEXT NOT NULL,
                        Location TEXT NOT NULL,
                        CreatedAtUtc TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var provider = CreateServices(dbPath))
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>().InitializeAsync();
            }

            var columns = await GetColumnNamesAsync(dbPath, "Studios");
            columns.Should().Contain("TimeZoneId");
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
                    OurContacts =
                    [
                        new StudioContact { Name = "Priya", Role = "PIC" }
                    ],
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
                studios[0].OurContacts.Should().ContainSingle();
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

    private static async Task<IReadOnlyList<string>> GetColumnNamesAsync(string databasePath, string tableName)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static ServiceProvider CreateServices(
        string dbPath,
        string? workflowDbPath = null,
        HttpMessageHandler? confluenceHandler = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:StudioDb"] = $"Data Source={dbPath}",
                ["ConnectionStrings:WorkflowDb"] = workflowDbPath is null ? null : $"Data Source={workflowDbPath}"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddDataProtection();
        services.AddStudioDirectory(configuration);
        if (confluenceHandler is not null)
        {
            services.AddHttpClient<IStudioConfluenceUpdateService, ConfluenceStudioUpdateService>()
                .ConfigurePrimaryHttpMessageHandler(() => confluenceHandler);
        }
        return services.BuildServiceProvider();
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
