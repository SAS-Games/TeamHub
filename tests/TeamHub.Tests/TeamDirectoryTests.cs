using System.IO.Compression;
using System.Text;
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

    [Fact]
    public async Task Initializer_UpgradesExistingCustomTablesForExcelSynchronization()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE CustomTeamTabs (
                        Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Slug TEXT NOT NULL,
                        DisplayOrder INTEGER NOT NULL, IsArchived INTEGER NOT NULL,
                        CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
                    CREATE TABLE CustomTeamTables (
                        Id TEXT NOT NULL PRIMARY KEY, TabId TEXT NOT NULL, Name TEXT NOT NULL,
                        DisplayOrder INTEGER NOT NULL, IsArchived INTEGER NOT NULL,
                        CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
                    CREATE TABLE CustomTeamColumns (
                        Id TEXT NOT NULL PRIMARY KEY, TableId TEXT NOT NULL, Key TEXT NOT NULL,
                        Label TEXT NOT NULL, FieldType TEXT NOT NULL, IsRequired INTEGER NOT NULL,
                        OptionsJson TEXT NOT NULL, DisplayOrder INTEGER NOT NULL, IsArchived INTEGER NOT NULL,
                        CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);
                    CREATE TABLE CustomTeamRows (
                        Id TEXT NOT NULL PRIMARY KEY, TableId TEXT NOT NULL, ValuesJson TEXT NOT NULL,
                        Version INTEGER NOT NULL, IsDeleted INTEGER NOT NULL, CreatedBy TEXT NOT NULL,
                        UpdatedBy TEXT NOT NULL, DeletedBy TEXT NULL, CreatedAtUtc TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL, DeletedAtUtc TEXT NULL);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var provider = CreateServices(dbPath))
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            }

            await using var verification = new SqliteConnection($"Data Source={dbPath}");
            await verification.OpenAsync();
            (await ColumnNamesAsync(verification, "CustomTeamTables")).Should().Contain([
                "SourceType", "SourceUrl", "SourceDriveId", "SourceItemId", "SourceDisplayName",
                "SourceWorksheet", "SourceHeaderRow",
                "PrimaryKeySourceHeader", "LastSyncedAtUtc", "LastSyncStatus", "LastSyncMessage"]);
            (await ColumnNamesAsync(verification, "CustomTeamColumns")).Should().Contain(["IsSourceColumn", "SourceHeader"]);
            (await ColumnNamesAsync(verification, "CustomTeamRows")).Should().Contain(["SourceKey", "SourceStatus", "LastSeenAtUtc"]);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task CustomTabs_StoreSchemasRowsAndAuditHistoryInTeamDatabase()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var tabs = scope.ServiceProvider.GetRequiredService<ICustomTeamTabService>();

            var tab = await tabs.SaveTabAsync(new(null, "Support Metrics", 2));
            var table = await tabs.SaveTableAsync(new(tab.Id, null, "Weekly Metrics"));
            var status = await tabs.SaveColumnAsync(new(
                table.Id, null, "Status", CustomTeamFieldTypes.Choice, true, ["Green", "Amber", "Red"]));
            var hours = await tabs.SaveColumnAsync(new(
                table.Id, null, "Hours", CustomTeamFieldTypes.Number, false, []));

            var created = await tabs.SaveRowAsync(new(
                table.Id,
                null,
                0,
                new Dictionary<string, string?> { [status.Key] = "Green", [hours.Key] = "12.5" },
                "asha@example.com"));
            var loaded = await tabs.GetTabAsync("support-metrics");

            loaded.Should().NotBeNull();
            loaded!.Tables.Should().ContainSingle();
            loaded.Tables[0].Columns.Should().HaveCount(2);
            loaded.Tables[0].Rows.Should().ContainSingle().Which.Values[status.Key].Should().Be("Green");

            var updated = await tabs.SaveRowAsync(new(
                table.Id,
                created.Id,
                created.Version,
                new Dictionary<string, string?> { [status.Key] = "Amber", [hours.Key] = "14" },
                "dev@example.com"));
            var staleUpdate = () => tabs.SaveRowAsync(new(
                table.Id,
                created.Id,
                created.Version,
                new Dictionary<string, string?> { [status.Key] = "Red" },
                "stale@example.com"));
            await staleUpdate.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*changed by another user*");

            await tabs.RemoveRowAsync(table.Id, updated.Id, updated.Version, "admin@example.com");
            (await tabs.GetTabAsync(tab.Slug))!.Tables[0].Rows.Should().BeEmpty();

            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM CustomTeamRowAudits;";
            Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(3);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public void ExcelReader_ReadsConfiguredWorksheetHeaderAndTextIdentifiers()
    {
        using var workbook = CreateXlsxWorkbook();

        var result = DirectDownloadExcelTableSourceReader.ReadWorkbook(workbook, "People", 2);

        result.Worksheet.Should().Be("People");
        result.Headers.Should().Equal("Employee ID", "Name");
        result.Rows.Should().ContainSingle();
        result.Rows[0]["Employee ID"].Should().Be("00125");
        result.Rows[0]["Name"].Should().Be("Asha");
    }

    [Fact]
    public async Task UploadedExcelSource_IsStoredPrivatelyAndReadByManagedReference()
    {
        var dbPath = CreateDatabasePath();
        var uploadDirectory = Path.Combine(Path.GetTempPath(), $"teamhub-excel-{Guid.NewGuid():N}");
        try
        {
            await using var provider = CreateServices(dbPath, uploadDirectory: uploadDirectory);
            var store = provider.GetRequiredService<IExcelSourceFileStore>();
            var reader = provider.GetRequiredService<IExcelTableSourceReader>();
            using var workbook = CreateXlsxWorkbook();

            var stored = await store.SaveAsync(workbook, "People.xlsx", workbook.Length);
            var result = await reader.ReadAsync(new ExcelTableSourceRequest(
                CustomTeamTableSourceTypes.UploadedExcel,
                stored.Reference,
                null,
                null,
                "People",
                2));

            stored.DisplayName.Should().Be("People.xlsx");
            stored.Reference.Should().EndWith(".xlsx");
            File.Exists(Path.Combine(uploadDirectory, stored.Reference)).Should().BeTrue();
            result.Rows.Should().ContainSingle();
            result.Rows[0]["Employee ID"].Should().Be("00125");

            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var tabs = scope.ServiceProvider.GetRequiredService<ICustomTeamTabService>();
            var tab = await tabs.SaveTabAsync(new(null, "Uploaded workbook"));
            var table = await tabs.SaveTableAsync(new(
                tab.Id,
                null,
                "People",
                SourceType: CustomTeamTableSourceTypes.UploadedExcel,
                SourceUrl: stored.Reference,
                SourceWorksheet: "People",
                SourceHeaderRow: 2,
                PrimaryKeySourceHeader: "Employee ID",
                SourceDisplayName: stored.DisplayName));
            var sync = await tabs.SyncExcelTableAsync(table.Id, "admin@example.com");
            sync.Added.Should().Be(1);
        }
        finally
        {
            DeleteDatabase(dbPath);
            if (Directory.Exists(uploadDirectory)) Directory.Delete(uploadDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MicrosoftGraphExcelTable_UsesStableDriveAndItemIdentifiers()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            var reader = new FakeExcelTableSourceReader { Data = ExcelData(("E-001", "Asha")) };
            await using var provider = CreateServices(dbPath, reader);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var tabs = scope.ServiceProvider.GetRequiredService<ICustomTeamTabService>();
            var tab = await tabs.SaveTabAsync(new(null, "Restricted workbook"));
            var table = await tabs.SaveTableAsync(new(
                tab.Id,
                null,
                "People",
                SourceType: CustomTeamTableSourceTypes.MicrosoftGraphExcel,
                SourceHeaderRow: 1,
                PrimaryKeySourceHeader: "Employee ID",
                SourceDriveId: "drive-123",
                SourceItemId: "item-456",
                SourceDisplayName: "People.xlsx"));

            await tabs.SyncExcelTableAsync(table.Id, "admin@example.com");

            reader.LastRequest.Should().NotBeNull();
            reader.LastRequest!.SourceType.Should().Be(CustomTeamTableSourceTypes.MicrosoftGraphExcel);
            reader.LastRequest.SourceDriveId.Should().Be("drive-123");
            reader.LastRequest.SourceItemId.Should().Be("item-456");
            var loaded = (await tabs.GetTabAsync(tab.Slug))!.Tables.Single();
            loaded.SourceDisplayName.Should().Be("People.xlsx");
            loaded.Rows.Should().ContainSingle();
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task MicrosoftGraphExcelTable_UsesSharingLinkWithoutWorkbookIds()
    {
        var dbPath = CreateDatabasePath();
        const string sharingUrl = "https://company-my.sharepoint.com/:x:/r/personal/user/Documents/People.xlsx?web=1";
        try
        {
            var reader = new FakeExcelTableSourceReader { Data = ExcelData(("E-001", "Asha")) };
            await using var provider = CreateServices(dbPath, reader);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var tabs = scope.ServiceProvider.GetRequiredService<ICustomTeamTabService>();
            var tab = await tabs.SaveTabAsync(new(null, "Shared workbook"));
            var table = await tabs.SaveTableAsync(new(
                tab.Id,
                null,
                "People",
                SourceType: CustomTeamTableSourceTypes.MicrosoftGraphExcel,
                SourceUrl: sharingUrl,
                PrimaryKeySourceHeader: "Employee ID"));

            await tabs.SyncExcelTableAsync(table.Id, "admin@example.com");

            reader.LastRequest.Should().NotBeNull();
            reader.LastRequest!.SourceUrl.Should().Be(sharingUrl);
            reader.LastRequest.SourceDriveId.Should().BeNull();
            reader.LastRequest.SourceItemId.Should().BeNull();
            var loaded = (await tabs.GetTabAsync(tab.Slug))!.Tables.Single();
            loaded.SourceUrl.Should().Be(sharingUrl);
            loaded.Rows.Should().ContainSingle();
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task ExcelSchemaImport_CreatesEditableColumnsBeforeRowsAreSynchronized()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            var reader = new FakeExcelTableSourceReader { Data = ExcelData(("E-001", "Asha")) };
            await using var provider = CreateServices(dbPath, reader);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var tabs = scope.ServiceProvider.GetRequiredService<ICustomTeamTabService>();
            var tab = await tabs.SaveTabAsync(new(null, "Schema import"));
            var table = await tabs.SaveTableAsync(new(
                tab.Id,
                null,
                "People",
                SourceType: CustomTeamTableSourceTypes.ExcelUrl,
                SourceUrl: "https://example.test/people.xlsx",
                SourceWorksheet: "People"));

            var imported = await tabs.ImportExcelSchemaAsync(table.Id);
            var afterImport = (await tabs.GetTabAsync(tab.Slug))!.Tables.Single();

            imported.Added.Should().Be(2);
            imported.HeaderCount.Should().Be(2);
            afterImport.Rows.Should().BeEmpty();
            afterImport.Columns.Should().OnlyContain(column => column.IsSourceColumn);
            afterImport.Columns.Select(column => column.SourceHeader).Should().BeEquivalentTo("Employee ID", "Name");

            var nameColumn = afterImport.Columns.Single(column => column.SourceHeader == "Name");
            await tabs.SaveColumnAsync(new SaveCustomTeamColumnRequest(
                table.Id,
                nameColumn.Id,
                "Employee name",
                CustomTeamFieldTypes.Text,
                true,
                [],
                7));
            await tabs.SaveTableAsync(new SaveCustomTeamTableRequest(
                tab.Id,
                table.Id,
                "People",
                SourceType: CustomTeamTableSourceTypes.ExcelUrl,
                SourceUrl: "https://example.test/people.xlsx",
                SourceWorksheet: "People",
                PrimaryKeySourceHeader: "Employee ID"));

            await tabs.SyncExcelTableAsync(table.Id, "admin@example.com");
            var synchronized = (await tabs.GetTabAsync(tab.Slug))!.Tables.Single();
            var editedColumn = synchronized.Columns.Single(column => column.SourceHeader == "Name");
            editedColumn.Label.Should().Be("Employee name");
            editedColumn.IsRequired.Should().BeTrue();
            editedColumn.DisplayOrder.Should().Be(7);
            synchronized.Rows.Should().ContainSingle();
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task ExcelBackedTable_UsesPrimaryKeyAndPreservesLocalValuesAcrossReorderingAndRemoval()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            var reader = new FakeExcelTableSourceReader
            {
                Data = ExcelData(
                    ("E-001", "Asha"),
                    ("E-002", "Dev"))
            };
            await using var provider = CreateServices(dbPath, reader);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var tabs = scope.ServiceProvider.GetRequiredService<ICustomTeamTabService>();
            var tab = await tabs.SaveTabAsync(new(null, "Excel People"));
            var table = await tabs.SaveTableAsync(new(
                tab.Id,
                null,
                "People",
                SourceType: CustomTeamTableSourceTypes.ExcelUrl,
                SourceUrl: "https://example.test/people.xlsx",
                SourceWorksheet: "People",
                SourceHeaderRow: 1,
                PrimaryKeySourceHeader: "Employee ID"));

            var firstSync = await tabs.SyncExcelTableAsync(table.Id, "admin@example.com");
            firstSync.Added.Should().Be(2);
            var loaded = (await tabs.GetTabAsync(tab.Slug))!.Tables.Single();
            loaded.Columns.Count(column => column.IsSourceColumn).Should().Be(2);
            loaded.Columns.Single(column => column.IsPrimaryKey).SourceHeader.Should().Be("Employee ID");

            var notes = await tabs.SaveColumnAsync(new(
                table.Id, null, "Team Notes", CustomTeamFieldTypes.LongText, false, []));
            loaded = (await tabs.GetTabAsync(tab.Slug))!.Tables.Single();
            var asha = loaded.Rows.Single(row => row.SourceKey == "E-001");
            await tabs.SaveRowAsync(new(
                table.Id,
                asha.Id,
                asha.Version,
                new Dictionary<string, string?> { [notes.Key] = "Keep this local note" },
                "editor@example.com"));

            reader.Data = ExcelData(
                ("E-002", "Dev updated"),
                ("E-001", "Asha updated"));
            await tabs.SyncExcelTableAsync(table.Id, "admin@example.com");
            loaded = (await tabs.GetTabAsync(tab.Slug))!.Tables.Single();
            asha = loaded.Rows.Single(row => row.SourceKey == "E-001");
            asha.Values[notes.Key].Should().Be("Keep this local note");
            asha.Values[loaded.Columns.Single(column => column.SourceHeader == "Name").Key].Should().Be("Asha updated");

            reader.Data = ExcelData(("E-002", "Dev updated"));
            var removalSync = await tabs.SyncExcelTableAsync(table.Id, "admin@example.com");
            removalSync.Missing.Should().Be(1);
            loaded = (await tabs.GetTabAsync(tab.Slug))!.Tables.Single();
            asha = loaded.Rows.Single(row => row.SourceKey == "E-001");
            asha.IsMissingFromSource.Should().BeTrue();
            asha.Values[notes.Key].Should().Be("Keep this local note");

            await tabs.RemoveRowAsync(table.Id, asha.Id, asha.Version, "editor@example.com");
            (await tabs.GetTabAsync(tab.Slug))!.Tables.Single().Rows.Should().NotContain(row => row.SourceKey == "E-001");

            reader.Data = ExcelData(("E-001", "Asha returned"), ("E-002", "Dev updated"));
            var restoreSync = await tabs.SyncExcelTableAsync(table.Id, "admin@example.com");
            restoreSync.Restored.Should().Be(1);
            asha = (await tabs.GetTabAsync(tab.Slug))!.Tables.Single().Rows.Single(row => row.SourceKey == "E-001");
            asha.IsMissingFromSource.Should().BeFalse();
            asha.Values[notes.Key].Should().Be("Keep this local note");
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task ExcelBackedTable_RejectsMissingAndDuplicatePrimaryKeysWithoutReplacingRows()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            var reader = new FakeExcelTableSourceReader { Data = ExcelData(("E-001", "Asha")) };
            await using var provider = CreateServices(dbPath, reader);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var tabs = scope.ServiceProvider.GetRequiredService<ICustomTeamTabService>();
            var tab = await tabs.SaveTabAsync(new(null, "Excel Validation"));
            var table = await tabs.SaveTableAsync(new(
                tab.Id,
                null,
                "People",
                SourceType: CustomTeamTableSourceTypes.ExcelUrl,
                SourceUrl: "https://example.test/people.xlsx",
                PrimaryKeySourceHeader: "Employee ID"));
            await tabs.SyncExcelTableAsync(table.Id, "admin@example.com");

            reader.Data = ExcelData(("E-001", "First"), ("E-001", "Duplicate"));
            var duplicate = () => tabs.SyncExcelTableAsync(table.Id, "admin@example.com");
            await duplicate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Duplicate primary key*");

            var loaded = (await tabs.GetTabAsync(tab.Slug))!.Tables.Single();
            loaded.Rows.Should().ContainSingle();
            loaded.LastSyncStatus.Should().Be("Failed");
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [Fact]
    public async Task DeleteCustomTab_PermanentlyRemovesItsSchemaRowsAndAuditHistory()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var tabs = scope.ServiceProvider.GetRequiredService<ICustomTeamTabService>();

            var tab = await tabs.SaveTabAsync(new(null, "Temporary Planning"));
            var table = await tabs.SaveTableAsync(new(tab.Id, null, "Draft Schedule"));
            var column = await tabs.SaveColumnAsync(new(
                table.Id, null, "Owner", CustomTeamFieldTypes.Text, true, []));
            await tabs.SaveRowAsync(new(
                table.Id,
                null,
                0,
                new Dictionary<string, string?> { [column.Key] = "Asha" },
                "admin@example.com"));

            await tabs.DeleteTabAsync(tab.Id);

            (await tabs.GetTabAsync(tab.Slug)).Should().BeNull();
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM CustomTeamTabs)
                  + (SELECT COUNT(*) FROM CustomTeamTables)
                  + (SELECT COUNT(*) FROM CustomTeamColumns)
                  + (SELECT COUNT(*) FROM CustomTeamRows)
                  + (SELECT COUNT(*) FROM CustomTeamRowAudits);
                """;
            Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(0);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }
    [Fact]
    public async Task CustomTabs_ValidateRequiredAndTypedColumnValues()
    {
        var dbPath = CreateDatabasePath();
        try
        {
            await using var provider = CreateServices(dbPath);
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>().InitializeAsync();
            var tabs = scope.ServiceProvider.GetRequiredService<ICustomTeamTabService>();
            var tab = await tabs.SaveTabAsync(new(null, "Planning"));
            var table = await tabs.SaveTableAsync(new(tab.Id, null, "Dates"));
            var date = await tabs.SaveColumnAsync(new(
                table.Id, null, "Due date", CustomTeamFieldTypes.Date, true, []));

            var missing = () => tabs.SaveRowAsync(new(
                table.Id, null, 0, new Dictionary<string, string?>(), "user@example.com"));
            var invalid = () => tabs.SaveRowAsync(new(
                table.Id, null, 0, new Dictionary<string, string?> { [date.Key] = "tomorrow" }, "user@example.com"));

            await missing.Should().ThrowAsync<ArgumentException>().WithMessage("*required*");
            await invalid.Should().ThrowAsync<ArgumentException>().WithMessage("*valid value*");
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"teamhub-team-{Guid.NewGuid():N}.db");

    private static ServiceProvider CreateServices(
        string dbPath,
        IExcelTableSourceReader? excelReader = null,
        string? uploadDirectory = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TeamDb"] = $"Data Source={dbPath}",
                ["TeamExcel:UploadDirectory"] = uploadDirectory
            })
            .Build();
        var services = new ServiceCollection();
        services.AddTeamDirectory(configuration);
        if (excelReader is not null) services.AddSingleton(excelReader);
        return services.BuildServiceProvider();
    }

    private static async Task<IReadOnlyList<string>> ColumnNamesAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static MemoryStream CreateXlsxWorkbook()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddZipEntry(archive, "xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="People" sheetId="1" r:id="rId1" /></sheets>
                </workbook>
                """);
            AddZipEntry(archive, "xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Target="worksheets/sheet1.xml" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" />
                </Relationships>
                """);
            AddZipEntry(archive, "xl/sharedStrings.xml", """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="4" uniqueCount="4">
                  <si><t>Employee ID</t></si><si><t>Name</t></si><si><t>00125</t></si><si><t>Asha</t></si>
                </sst>
                """);
            AddZipEntry(archive, "xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="2"><c r="A2" t="s"><v>0</v></c><c r="B2" t="s"><v>1</v></c></row>
                    <row r="3"><c r="A3" t="s"><v>2</v></c><c r="B3" t="s"><v>3</v></c></row>
                  </sheetData>
                </worksheet>
                """);
        }
        stream.Position = 0;
        return stream;
    }

    private static void AddZipEntry(ZipArchive archive, string path, string text)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(text);
    }

    private static ExcelTableSourceData ExcelData(params (string Id, string Name)[] rows) => new(
        "People",
        ["Employee ID", "Name"],
        rows.Select(row => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["Employee ID"] = row.Id,
            ["Name"] = row.Name
        }).ToList());

    private sealed class FakeExcelTableSourceReader : IExcelTableSourceReader
    {
        public ExcelTableSourceData Data { get; set; } = ExcelData();
        public ExcelTableSourceRequest? LastRequest { get; private set; }

        public Task<ExcelTableSourceData> ReadAsync(
            ExcelTableSourceRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Data);
        }
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
