using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.Team;

namespace TeamHub.Tests;

public sealed class TeamDirectoryTests
{
    [Fact]
    public async Task Directory_IsEmpty_WhenNothingHasBeenConfigured()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();

            var directory = await scope.ServiceProvider.GetRequiredService<ITeamDirectoryService>()
                .GetTeamDirectoryAsync();

            directory.Members.Should().BeEmpty();
            directory.Specializations.Should().BeEmpty();
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Configuration_CreatesAndUpdatesTeamMember()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var configuration = scope.ServiceProvider.GetRequiredService<ITeamConfigurationService>();

            var saved = await configuration.SaveTeamMemberAsync(new TeamMemberDto
            {
                EmployeeName = "Asha Rao",
                Role = "Support Engineer",
                Gid = "G123",
                Email = "asha@example.com",
                ContactNumber = "+91 99999 00000",
                Section = TeamMemberSections.Management
            });
            saved.Role = "Senior Support Engineer";
            var updated = await configuration.SaveTeamMemberAsync(saved);

            updated.Id.Should().Be(saved.Id);
            updated.Role.Should().Be("Senior Support Engineer");
            var directory = await scope.ServiceProvider.GetRequiredService<ITeamDirectoryService>()
                .GetTeamDirectoryAsync();
            var member = directory.Members.Should().ContainSingle().Subject;
            member.Role.Should().Be("Senior Support Engineer");
            member.Section.Should().Be(TeamMemberSections.Management);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Configuration_ManagesSupportSpecializationsIndependently()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var configuration = scope.ServiceProvider.GetRequiredService<ITeamConfigurationService>();

            var saved = await configuration.SaveSpecializationAsync(new SpecializationDto
            {
                Pod = "Rendering",
                FocusAreas = "Shaders, performance",
                Members = "Asha, Dev"
            });

            var directory = await scope.ServiceProvider.GetRequiredService<ITeamDirectoryService>()
                .GetTeamDirectoryAsync();
            directory.Specializations.Should().ContainSingle().Which.Pod.Should().Be("Rendering");

            await configuration.DeleteSpecializationAsync(saved.Id);
            (await scope.ServiceProvider.GetRequiredService<ITeamDirectoryService>()
                .GetTeamDirectoryAsync()).Specializations.Should().BeEmpty();
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Achievements_AreStoredAndListedNewestFirst()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var achievements = scope.ServiceProvider.GetRequiredService<ITeamAchievementService>();

            await achievements.AddAchievementAsync(new TeamAchievementDto
            {
                Title = "First release",
                Description = "Shipped the first release.",
                AchievedBy = "Platform pod",
                AchievedOn = new DateTime(2026, 1, 10)
            });
            await achievements.AddAchievementAsync(new TeamAchievementDto
            {
                Title = "Performance target",
                Description = "Reached the frame-time target.",
                AchievedBy = "Rendering pod",
                AchievedOn = new DateTime(2026, 2, 12)
            });

            var saved = await achievements.GetAchievementsAsync();

            saved.Select(item => item.Title).Should().Equal("Performance target", "First release");
            saved[0].AchievedBy.Should().Be("Rendering pod");
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task TextAppearance_IsStoredPerPageAndColumn()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var appearanceService = scope.ServiceProvider.GetRequiredService<IPageTextAppearanceService>();

            await appearanceService.SavePageAppearanceAsync(new PageTextAppearanceDto
            {
                PageKey = TeamPageAppearanceCatalog.Directory,
                Columns =
                [
                    new() { ColumnKey = "employee-name", IsBold = true },
                    new() { ColumnKey = "role", IsItalic = true }
                ]
            });

            var directoryAppearance = await appearanceService.GetPageAppearanceAsync(
                TeamPageAppearanceCatalog.Directory);
            var achievementsAppearance = await appearanceService.GetPageAppearanceAsync(
                TeamPageAppearanceCatalog.Achievements);

            directoryAppearance.Columns.Should().HaveCount(2);
            directoryAppearance.CssClass("employee-name").Should().Contain("configured-text-bold");
            directoryAppearance.CssClass("role").Should().Contain("configured-text-italic");
            directoryAppearance.CssClass("email").Should().BeEmpty();
            achievementsAppearance.Columns.Should().BeEmpty();
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task Initializer_AddsSectionToAnExistingTeamMembersTable()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE TeamMembers (
                        Id TEXT NOT NULL CONSTRAINT PK_TeamMembers PRIMARY KEY,
                        EmployeeName TEXT NOT NULL,
                        Role TEXT NOT NULL,
                        Gid TEXT NOT NULL,
                        Email TEXT NOT NULL,
                        ContactNumber TEXT NOT NULL,
                        CreatedAtUtc TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var provider = CreateServices(dbPath))
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            }

            await using var verificationConnection = new SqliteConnection($"Data Source={dbPath}");
            await verificationConnection.OpenAsync();
            await using var verificationCommand = verificationConnection.CreateCommand();
            verificationCommand.CommandText = "PRAGMA table_info(TeamMembers);";
            await using var reader = await verificationCommand.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            columns.Should().Contain("Section");
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"teamhub-team-{Guid.NewGuid():N}.db");

    private static ServiceProvider CreateServices(string dbPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TeamDb"] = $"Data Source={dbPath}"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddTeamDirectory(configuration);
        return services.BuildServiceProvider();
    }

    private static void DeleteDatabase(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
    }
}
