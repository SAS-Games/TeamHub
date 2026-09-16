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
