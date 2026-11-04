using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using TeamHub.AI.Usage.Contracts;
using TeamHub.Web.AI;
using TeamHub.Web.Pages.Configuration;

namespace TeamHub.Tests;

public sealed class AiUsagePageTests
{
    [Fact]
    public async Task Admin_report_builds_an_inclusive_utc_date_query_and_supports_user_feature_model_grouping()
    {
        var ledger = new RecordingLedger
        {
            FilterResult = new AiUsageFilterOptions(
                ["General", "WeeklyWorkSummary"],
                ["TeamHub.Web"],
                ["PfbAiGateway"],
                ["model-a"]),
            Result = new AiUsageReport(
                new AiUsageTotals(4, 3, 100, 25, 125),
                [new AiUsageAggregateRow("user@example.com", "Chat", 4, 3, 100, 25, 125)
                    { TertiaryKey = "model-a" }])
        };
        var page = new AIUsageModel(ledger)
        {
            FromDate = new DateOnly(2026, 1, 1),
            ToDate = new DateOnly(2026, 1, 31),
            UserId = " user@example.com ",
            Feature = " Chat ",
            Provider = " PfbAiGateway ",
            ModelName = " model-a ",
            Dimension = AiUsageDimension.UserFeatureModel
        };

        await page.OnGetAsync(CancellationToken.None);

        ledger.Dimension.Should().Be(AiUsageDimension.UserFeatureModel);
        ledger.Query.Should().NotBeNull();
        ledger.Query!.FromUtc.Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        ledger.Query.ToUtc.Should().Be(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        ledger.Query.UserId.Should().Be("user@example.com");
        ledger.Query.Feature.Should().Be("Chat");
        ledger.Query.Provider.Should().Be("PfbAiGateway");
        ledger.Query.Model.Should().Be("model-a");
        page.Report.Totals.TotalTokens.Should().Be(125);
        page.MeasurementCoverage.Should().Be(75m);
        page.PrimaryHeading.Should().Be("User");
        page.SecondaryHeading.Should().Be("Feature");
        page.TertiaryHeading.Should().Be("Model");
    }

    [Fact]
    public async Task Admin_report_rejects_an_end_date_before_the_start_without_querying_the_ledger()
    {
        var ledger = new RecordingLedger();
        var page = new AIUsageModel(ledger)
        {
            FromDate = new DateOnly(2026, 2, 1),
            ToDate = new DateOnly(2026, 1, 31)
        };

        await page.OnGetAsync(CancellationToken.None);

        page.ModelState.IsValid.Should().BeFalse();
        ledger.Query.Should().BeNull();
    }

    [Fact]
    public async Task Admin_report_loads_tool_activity_with_shared_and_tool_specific_filters()
    {
        var ledger = new RecordingLedger
        {
            FilterResult = new AiUsageFilterOptions(
                [], [AiUsageApplications.TeamHubWeb], [], [])
        };
        var tools = new RecordingToolLedger
        {
            FilterResult = new AiToolActivityFilterOptions(
                ["get_studio_jira_tickets"],
                ["Jira"]),
            Result = new AiToolActivityReport(
                new AiToolActivityTotals(2, 1, 1, 0, 2, 50),
                [new AiToolActivityAggregateRow(
                    "user@example.com", "get_studio_jira_tickets", 2, 1, 1, 0, 25)])
        };
        var page = new AIUsageModel(ledger, toolActivities: tools)
        {
            FromDate = new DateOnly(2026, 2, 1),
            ToDate = new DateOnly(2026, 2, 28),
            UserId = " user@example.com ",
            Feature = AiUsageFeatures.Chat,
            Application = AiUsageApplications.TeamHubWeb,
            ToolName = " get_studio_jira_tickets ",
            ToolStatus = AiToolActivityStatus.Failed,
            ToolExternalSource = " Jira ",
            ToolDimension = AiToolActivityDimension.UserTool
        };

        await page.OnGetAsync(CancellationToken.None);

        tools.Dimension.Should().Be(AiToolActivityDimension.UserTool);
        tools.Query.Should().NotBeNull();
        tools.Query!.UserId.Should().Be("user@example.com");
        tools.Query.Feature.Should().Be(AiUsageFeatures.Chat);
        tools.Query.Application.Should().Be(AiUsageApplications.TeamHubWeb);
        tools.Query.ToolName.Should().Be("get_studio_jira_tickets");
        tools.Query.Status.Should().Be(AiToolActivityStatus.Failed);
        tools.Query.ExternalSource.Should().Be("Jira");
        page.ToolReport.Totals.CallCount.Should().Be(2);
        page.ToolPrimaryHeading.Should().Be("User");
        page.ToolSecondaryHeading.Should().Be("Tool");
    }

    [Fact]
    public void Exporter_creates_safe_csv_and_a_two_sheet_excel_workbook()
    {
        var context = new AiUsageReportExportContext(
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 1, 31),
            "User, feature and model",
            "User",
            "Feature",
            "Model",
            new AiUsageReport(
                new AiUsageTotals(2, 2, 100, 20, 120),
                [new AiUsageAggregateRow("=unsafe@example.com", "Chat", 2, 2, 100, 20, 120)
                    { TertiaryKey = "model-a" }]),
            "User and tool",
            "User",
            "Tool",
            new AiToolActivityReport(
                new AiToolActivityTotals(3, 2, 1, 0, 1, 90),
                [new AiToolActivityAggregateRow("user@example.com", "list_studios", 3, 2, 1, 0, 30)]));
        var exporter = new AiUsageReportExporter();

        var csv = Encoding.UTF8.GetString(exporter.ExportUsageCsv(context));
        csv.Should().StartWith("\uFEFFUser,Feature,Model,Calls,Measured calls");
        csv.Should().Contain("'=unsafe@example.com,Chat,model-a,2,2,100,20,120");

        using var stream = new MemoryStream(exporter.ExportExcel(context));
        using var workbook = new XLWorkbook(stream);
        workbook.Worksheets.Select(sheet => sheet.Name).Should().Equal("Usage", "Tool activity");
        workbook.Worksheet("Usage").Cell("A1").GetString().Should().Be("AI usage");
        workbook.Worksheet("Usage").Cell("A9").GetString().Should().Be("=unsafe@example.com");
        workbook.Worksheet("Usage").Cell("H9").GetValue<long>().Should().Be(120);
        workbook.Worksheet("Tool activity").Cell("A9").GetString().Should().Be("user@example.com");
        workbook.Worksheet("Tool activity").Cell("G9").GetValue<long>().Should().Be(30);
    }

    [Fact]
    public async Task Excel_export_handler_uses_the_active_report_filters()
    {
        var ledger = new RecordingLedger
        {
            Result = new AiUsageReport(
                new AiUsageTotals(1, 1, 10, 2, 12),
                [new AiUsageAggregateRow("user@example.com", null, 1, 1, 10, 2, 12)])
        };
        var page = new AIUsageModel(ledger, exporter: new AiUsageReportExporter())
        {
            FromDate = new DateOnly(2026, 3, 1),
            ToDate = new DateOnly(2026, 3, 31),
            UserId = " user@example.com ",
            Dimension = AiUsageDimension.User
        };

        var result = await page.OnGetExportExcelAsync(CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.FileDownloadName.Should().Be("ai-usage-ledger-20260301-to-20260331.xlsx");
        file.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        file.FileContents.Should().NotBeEmpty();
        ledger.Query!.UserId.Should().Be("user@example.com");
    }

    private sealed class RecordingLedger : IAiUsageLedger
    {
        public AiUsageQuery? Query { get; private set; }
        public AiUsageDimension? Dimension { get; private set; }
        public AiUsageFilterOptions FilterResult { get; init; } = new([], [], [], []);
        public AiUsageReport Result { get; init; } =
            new(new AiUsageTotals(0, 0, 0, 0, 0), []);

        public Task<AiUsageFilterOptions> GetFilterOptionsAsync(
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken = default) => Task.FromResult(FilterResult);

        public Task<AiUsageReport> GetReportAsync(
            AiUsageQuery query,
            AiUsageDimension dimension,
            CancellationToken cancellationToken = default)
        {
            Query = query;
            Dimension = dimension;
            return Task.FromResult(Result);
        }

        public Task<int> DeleteBeforeAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class RecordingToolLedger : IAiToolActivityLedger
    {
        public AiToolActivityQuery? Query { get; private set; }
        public AiToolActivityDimension? Dimension { get; private set; }
        public AiToolActivityFilterOptions FilterResult { get; init; } = new([], []);
        public AiToolActivityReport Result { get; init; } =
            new(new AiToolActivityTotals(0, 0, 0, 0, 0, 0), []);

        public Task<AiToolActivityFilterOptions> GetToolFilterOptionsAsync(
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken = default) => Task.FromResult(FilterResult);

        public Task<AiToolActivityReport> GetToolReportAsync(
            AiToolActivityQuery query,
            AiToolActivityDimension dimension,
            CancellationToken cancellationToken = default)
        {
            Query = query;
            Dimension = dimension;
            return Task.FromResult(Result);
        }
    }
}
