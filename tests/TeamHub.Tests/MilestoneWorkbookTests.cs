using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.Excel;
using TeamHub.Milestones;

namespace TeamHub.Tests;

public sealed class MilestoneWorkbookTests
{
    [Fact]
    public async Task ReadsMilestoneTextAndDates_FromSavedExcelWorkbook()
    {
        var path = Path.Combine(Path.GetTempPath(), $"teamhub-milestones-{Guid.NewGuid():N}.xlsx");
        var delivery = new DateTime(2026, 10, 15);
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Milestones");
                string[] headers = ["Sl. No", "Program", "Title", "Developer", "Milestone", "Description", "Delivery Date", "MS Approval Date", "Release Date"];
                for (var column = 0; column < headers.Length; column++)
                {
                    sheet.Cell(1, column + 1).Value = headers[column];
                }

                sheet.Cell(2, 1).Value = "1";
                sheet.Cell(2, 2).Value = "Hero Project";
                sheet.Cell(2, 3).Value = "Test Game";
                sheet.Cell(2, 4).Value = "Test Studio";
                sheet.Cell(2, 5).Value = "Alpha";
                sheet.Cell(2, 6).Value = "Playable build";
                sheet.Cell(2, 7).Value = delivery;
                sheet.Cell(2, 8).Value = delivery.AddDays(7);
                workbook.SaveAs(path);
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MilestoneConfiguration:ExcelPath"] = path
            }).Build();
            using var services = new ServiceCollection().AddMilestoneTracker(configuration).BuildServiceProvider();
            using var scope = services.CreateScope();
            var milestones = await scope.ServiceProvider.GetRequiredService<IMilestoneTrackerService>().GetMilestonesAsync();

            var milestone = Assert.Single(milestones);
            Assert.Equal("Test Game", milestone.Title);
            Assert.Equal("Playable build", milestone.Description);
            Assert.Equal(delivery, milestone.DeliveryDate);
            Assert.Equal(delivery.AddDays(7), milestone.MsApprovalDate);
            Assert.Null(milestone.ReleaseDate);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadsMilestonesThroughSharedMicrosoftGraphWorkbookSource()
    {
        var delivery = new DateTime(2026, 11, 20);
        var source = new FakeExcelWorkbookSource(CreateWorkbook(delivery));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MilestoneConfiguration:SourceType"] = ExcelWorkbookSourceTypes.MicrosoftGraphExcel,
            ["MilestoneConfiguration:SourceDriveId"] = "drive-123",
            ["MilestoneConfiguration:SourceItemId"] = "item-456"
        }).Build();
        var services = new ServiceCollection();
        services.AddMilestoneTracker(configuration);
        services.AddSingleton<IExcelWorkbookSource>(source);
        using var provider = services.BuildServiceProvider();

        var milestones = await provider.GetRequiredService<IMilestoneTrackerService>().GetMilestonesAsync();

        milestones.Should().ContainSingle();
        milestones[0].DeliveryDate.Should().Be(delivery);
        source.LastRequest.Should().Be(new ExcelWorkbookSourceRequest(
            ExcelWorkbookSourceTypes.MicrosoftGraphExcel,
            DriveId: "drive-123",
            ItemId: "item-456"));
    }

    [Fact]
    public async Task ReadsMilestonesThroughMicrosoftGraphSharingLinkWithoutWorkbookIds()
    {
        const string sharingUrl = "https://company-my.sharepoint.com/:x:/r/personal/user/Documents/Milestones.xlsx?web=1";
        var source = new FakeExcelWorkbookSource(CreateWorkbook(new DateTime(2026, 12, 4)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MilestoneConfiguration:SourceType"] = ExcelWorkbookSourceTypes.MicrosoftGraphExcel,
            ["MilestoneConfiguration:SourceUrl"] = sharingUrl
        }).Build();
        var services = new ServiceCollection();
        services.AddMilestoneTracker(configuration);
        services.AddSingleton<IExcelWorkbookSource>(source);
        using var provider = services.BuildServiceProvider();

        var milestones = await provider.GetRequiredService<IMilestoneTrackerService>().GetMilestonesAsync();

        milestones.Should().ContainSingle();
        source.LastRequest.Should().Be(new ExcelWorkbookSourceRequest(
            ExcelWorkbookSourceTypes.MicrosoftGraphExcel,
            SharingUrl: sharingUrl));
    }

    [Fact]
    public async Task ReadsMilestonesThroughUploadedWorkbookReference()
    {
        var source = new FakeExcelWorkbookSource(CreateWorkbook(new DateTime(2027, 1, 8)));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MilestoneConfiguration:SourceType"] = ExcelWorkbookSourceTypes.UploadedExcel,
            ["MilestoneConfiguration:SourceUrl"] = "uploaded-milestones.xlsx"
        }).Build();
        var services = new ServiceCollection();
        services.AddMilestoneTracker(configuration);
        services.AddSingleton<IExcelWorkbookSource>(source);
        using var provider = services.BuildServiceProvider();

        var milestones = await provider.GetRequiredService<IMilestoneTrackerService>().GetMilestonesAsync();

        milestones.Should().ContainSingle();
        source.LastRequest.Should().Be(new ExcelWorkbookSourceRequest(
            ExcelWorkbookSourceTypes.UploadedExcel,
            ManagedReference: "uploaded-milestones.xlsx"));
    }

    [Fact]
    public async Task MilestoneConfiguration_PersistsInTeamDatabaseAndOverridesDefaults()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-milestones-{Guid.NewGuid():N}.db");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TeamDb"] = $"Data Source={databasePath}",
                ["MilestoneConfiguration:SourceType"] = ExcelWorkbookSourceTypes.LocalFile,
                ["MilestoneConfiguration:ExcelPath"] = "default.xlsx"
            }).Build();
            using var provider = new ServiceCollection()
                .AddMilestoneTracker(configuration)
                .BuildServiceProvider();
            using var scope = provider.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IMilestoneConfigurationService>();

            await settings.SaveSettingsAsync(new MilestoneSourceSettings
            {
                SourceType = ExcelWorkbookSourceTypes.MicrosoftGraphExcel,
                ExcelPath = "keep-for-later.xlsx",
                SourceDriveId = "drive-from-page",
                SourceItemId = "item-from-page"
            });
            var loaded = await settings.GetSettingsAsync();

            loaded.SourceType.Should().Be(ExcelWorkbookSourceTypes.MicrosoftGraphExcel);
            loaded.ExcelPath.Should().Be("keep-for-later.xlsx");
            loaded.SourceDriveId.Should().Be("drive-from-page");
            loaded.SourceItemId.Should().Be("item-from-page");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task MilestoneConfiguration_AcceptsMicrosoftGraphSharingLinkWithoutWorkbookIds()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-milestones-{Guid.NewGuid():N}.db");
        const string sharingUrl = "https://company.sharepoint.com/:x:/r/sites/Team/Documents/Milestones.xlsx?web=1";
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TeamDb"] = $"Data Source={databasePath}"
            }).Build();
            using var provider = new ServiceCollection().AddMilestoneTracker(configuration).BuildServiceProvider();
            using var scope = provider.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IMilestoneConfigurationService>();

            await settings.SaveSettingsAsync(new MilestoneSourceSettings
            {
                SourceType = ExcelWorkbookSourceTypes.MicrosoftGraphExcel,
                SourceUrl = sharingUrl
            });

            var loaded = await settings.GetSettingsAsync();
            loaded.SourceUrl.Should().Be(sharingUrl);
            loaded.SourceDriveId.Should().BeEmpty();
            loaded.SourceItemId.Should().BeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task MilestoneConfiguration_UpgradesExistingSettingsForUploadedWorkbookDisplayName()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-milestones-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE MilestoneSourceSettings (
                        Id INTEGER NOT NULL PRIMARY KEY,
                        SourceType TEXT NOT NULL,
                        ExcelPath TEXT NOT NULL,
                        SourceUrl TEXT NOT NULL,
                        SourceDriveId TEXT NOT NULL,
                        SourceItemId TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL
                    );
                    INSERT INTO MilestoneSourceSettings
                        (Id, SourceType, ExcelPath, SourceUrl, SourceDriveId, SourceItemId, UpdatedAtUtc)
                    VALUES
                        (1, 'LocalFile', 'config/MilestoneTracker.xlsx', '', '', '', '2026-01-01T00:00:00Z');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TeamDb"] = $"Data Source={databasePath}"
            }).Build();
            using var provider = new ServiceCollection().AddMilestoneTracker(configuration).BuildServiceProvider();
            using var scope = provider.CreateScope();

            var loaded = await scope.ServiceProvider.GetRequiredService<IMilestoneConfigurationService>().GetSettingsAsync();

            loaded.ExcelPath.Should().Be("config/MilestoneTracker.xlsx");
            loaded.SourceDisplayName.Should().BeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task MilestoneConfiguration_PersistsAndLoadsGraphCredentials()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-milestones-{Guid.NewGuid():N}.db");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TeamDb"] = $"Data Source={databasePath}"
            }).Build();
            using var provider = new ServiceCollection().AddMilestoneTracker(configuration).BuildServiceProvider();
            using var scope = provider.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IMilestoneConfigurationService>();

            await settings.SaveSettingsAsync(new MilestoneSourceSettings
            {
                SourceType = ExcelWorkbookSourceTypes.MicrosoftGraphExcel,
                SourceUrl = "https://company.sharepoint.com/:x:/r/sites/Team/Documents/Milestones.xlsx",
                TenantId = "tenant-guid-123",
                ClientId = "client-guid-456",
                ClientSecret = "secret-value-789"
            });

            var loaded = await settings.GetSettingsAsync();
            loaded.TenantId.Should().Be("tenant-guid-123");
            loaded.ClientId.Should().Be("client-guid-456");
            loaded.ClientSecret.Should().Be("secret-value-789");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task MilestoneConfiguration_AllowsEmptyOptionalGraphCredentials()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-milestones-{Guid.NewGuid():N}.db");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TeamDb"] = $"Data Source={databasePath}"
            }).Build();
            using var provider = new ServiceCollection().AddMilestoneTracker(configuration).BuildServiceProvider();
            using var scope = provider.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IMilestoneConfigurationService>();

            await settings.SaveSettingsAsync(new MilestoneSourceSettings
            {
                SourceType = ExcelWorkbookSourceTypes.MicrosoftGraphExcel,
                SourceUrl = "https://company.sharepoint.com/:x:/r/sites/Team/Documents/Milestones.xlsx",
                TenantId = null,
                ClientId = "",
                ClientSecret = "   "
            });

            var loaded = await settings.GetSettingsAsync();
            loaded.TenantId.Should().BeEmpty();
            loaded.ClientId.Should().BeEmpty();
            loaded.ClientSecret.Should().BeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void MicrosoftCloudLinkHelper_TransformsSharePointDocAspxUrl()
    {
        const string docAspxUrl = "https://sonyapc-my.sharepoint.com/personal/sujith_sukumaran_sony_com/_layouts/15/Doc.aspx?sourcedoc=%7BEB3BC51E-3B47-4001-BFC1-CBE12FDB17DF%7D&file=SIE%20Hero%20Project_Milestone%20Tracker%201.3.xlsx&action=default&mobileredirect=true&DefaultItemOpen=1";

        MicrosoftCloudLinkHelper.IsMicrosoftCloudUrl(docAspxUrl).Should().BeTrue();
        var candidates = MicrosoftCloudLinkHelper.GetDirectDownloadCandidateUrls(docAspxUrl);
        candidates.Should().NotBeEmpty();
        candidates.Should().Contain(u => u.Contains("download.aspx") && u.Contains("EB3BC51E-3B47-4001-BFC1-CBE12FDB17DF", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MicrosoftCloudLinkHelper_TransformsOneDriveConsumerUrl()
    {
        const string oneDriveUrl = "https://onedrive.live.com/view.aspx?resid=123456!789&ithint=file%2cxlsx";

        MicrosoftCloudLinkHelper.IsMicrosoftCloudUrl(oneDriveUrl).Should().BeTrue();
        var candidates = MicrosoftCloudLinkHelper.GetDirectDownloadCandidateUrls(oneDriveUrl);
        candidates.Should().Contain(u => u.Contains("download.aspx"));
    }

    [Fact]
    public void MicrosoftCloudLinkHelper_ValidatesExcelStreamSignatures()
    {
        var validExcel = CreateWorkbook(DateTime.UtcNow);
        using var validStream = new MemoryStream(validExcel);
        MicrosoftCloudLinkHelper.IsSupportedExcelStream(validStream).Should().BeTrue();

        var htmlSignInPage = System.Text.Encoding.UTF8.GetBytes("<html><head><title>Sign in to your account</title></head><body>login.microsoftonline.com</body></html>");
        using var htmlStream = new MemoryStream(htmlSignInPage);
        MicrosoftCloudLinkHelper.IsSupportedExcelStream(htmlStream).Should().BeFalse();
    }

    private static byte[] CreateWorkbook(DateTime delivery)
    {
        using var stream = new MemoryStream();
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Milestones");
            string[] headers = ["Sl. No", "Program", "Title", "Developer", "Milestone", "Description", "Delivery Date", "MS Approval Date", "Release Date"];
            for (var column = 0; column < headers.Length; column++)
            {
                sheet.Cell(1, column + 1).Value = headers[column];
            }
            sheet.Cell(2, 1).Value = "1";
            sheet.Cell(2, 2).Value = "Hero Project";
            sheet.Cell(2, 3).Value = "Test Game";
            sheet.Cell(2, 4).Value = "Test Studio";
            sheet.Cell(2, 5).Value = "Alpha";
            sheet.Cell(2, 6).Value = "Playable build";
            sheet.Cell(2, 7).Value = delivery;
            workbook.SaveAs(stream);
        }
        return stream.ToArray();
    }

    private sealed class FakeExcelWorkbookSource(byte[] workbook) : IExcelWorkbookSource
    {
        public ExcelWorkbookSourceRequest? LastRequest { get; private set; }

        public Task<MemoryStream> OpenAsync(
            ExcelWorkbookSourceRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new MemoryStream(workbook, writable: false));
        }
    }
}
