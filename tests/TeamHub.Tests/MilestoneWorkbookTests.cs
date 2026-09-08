using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
}
