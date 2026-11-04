using FluentAssertions;
using TeamHub.AI.Usage.Contracts;
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
