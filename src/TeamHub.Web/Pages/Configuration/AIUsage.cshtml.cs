using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.AI.Usage.Contracts;
using TeamHub.Authentication;
using TeamHub.Web.AI;

namespace TeamHub.Web.Pages.Configuration;

[Authorize(Roles = TeamHubUserTypes.Admin)]
public sealed class AIUsageModel(
    IAiUsageLedger ledger,
    IAiUsageLimitService? limits = null,
    IUserAccessService? users = null,
    IAiToolActivityLedger? toolActivities = null,
    IAiUsageReportExporter? exporter = null) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public DateOnly? FromDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? ToDate { get; set; }

    [BindProperty(SupportsGet = true), StringLength(320)]
    public string? UserId { get; set; }

    [BindProperty(SupportsGet = true), StringLength(100)]
    public string? Feature { get; set; }

    [BindProperty(SupportsGet = true), StringLength(100)]
    public string? Subfeature { get; set; }

    [BindProperty(SupportsGet = true), StringLength(100)]
    public string? Application { get; set; }

    [BindProperty(SupportsGet = true), StringLength(100)]
    public string? Provider { get; set; }

    [BindProperty(SupportsGet = true), StringLength(256)]
    public string? ModelName { get; set; }

    [BindProperty(SupportsGet = true)]
    public AiUsageDimension Dimension { get; set; } = AiUsageDimension.UserFeatureModel;

    [BindProperty(SupportsGet = true), StringLength(64)]
    public string? ToolName { get; set; }

    [BindProperty(SupportsGet = true)]
    public AiToolActivityStatus? ToolStatus { get; set; }

    [BindProperty(SupportsGet = true), StringLength(100)]
    public string? ToolExternalSource { get; set; }

    [BindProperty(SupportsGet = true)]
    public AiToolActivityDimension ToolDimension { get; set; } = AiToolActivityDimension.UserTool;

    [BindProperty]
    public AiUsageLimitInput LimitInput { get; set; } = new();

    public AiUsageReport Report { get; private set; } =
        new(new AiUsageTotals(0, 0, 0, 0, 0), []);
    public AiUsageFilterOptions FilterOptions { get; private set; } = new([], [], [], []);
    public AiToolActivityReport ToolReport { get; private set; } =
        new(new AiToolActivityTotals(0, 0, 0, 0, 0, 0), []);
    public AiToolActivityFilterOptions ToolFilterOptions { get; private set; } = new([], []);
    public IReadOnlyList<TimeZoneOption> TimeZones { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public string PrimaryHeading => Dimension switch
    {
        AiUsageDimension.User or AiUsageDimension.UserFeature or AiUsageDimension.UserFeatureModel => "User",
        AiUsageDimension.Feature or AiUsageDimension.FeatureModel => "Feature",
        AiUsageDimension.Subfeature => "Subfeature",
        AiUsageDimension.Application => "Application",
        AiUsageDimension.Provider => "Provider",
        AiUsageDimension.Model => "Model",
        AiUsageDimension.Day => "UTC day",
        _ => "Group"
    };

    public string? SecondaryHeading => Dimension switch
    {
        AiUsageDimension.Model => "Provider",
        AiUsageDimension.UserFeature or AiUsageDimension.UserFeatureModel => "Feature",
        AiUsageDimension.FeatureModel => "Model",
        _ => null
    };

    public string? TertiaryHeading =>
        Dimension == AiUsageDimension.UserFeatureModel ? "Model" : null;

    public decimal MeasurementCoverage => Report.Totals.RequestCount == 0
        ? 0
        : decimal.Round(
            Report.Totals.MeasuredRequestCount * 100m / Report.Totals.RequestCount,
            1);

    public string ToolPrimaryHeading => ToolDimension switch
    {
        AiToolActivityDimension.User or AiToolActivityDimension.UserTool => "User",
        AiToolActivityDimension.Tool or AiToolActivityDimension.ToolResource => "Tool",
        AiToolActivityDimension.ExternalSource => "External source",
        AiToolActivityDimension.Status => "Status",
        AiToolActivityDimension.Day => "UTC day",
        _ => "Group"
    };

    public string? ToolSecondaryHeading => ToolDimension switch
    {
        AiToolActivityDimension.UserTool => "Tool",
        AiToolActivityDimension.ToolResource => "Resource",
        _ => null
    };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadLimitInputAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        FromDate ??= today.AddDays(-29);
        ToDate ??= today;

        if (!Enum.IsDefined(Dimension))
            ModelState.AddModelError(nameof(Dimension), "Select a supported grouping.");
        if (!Enum.IsDefined(ToolDimension))
            ModelState.AddModelError(nameof(ToolDimension), "Select a supported tool-activity grouping.");
        if (ToolStatus.HasValue && !Enum.IsDefined(ToolStatus.Value))
            ModelState.AddModelError(nameof(ToolStatus), "Select a supported tool status.");
        if (FromDate > ToDate)
            ModelState.AddModelError(nameof(ToDate), "The end date must be on or after the start date.");
        if (FromDate > ToDate) return;

        var fromUtc = new DateTimeOffset(FromDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toUtc = new DateTimeOffset(ToDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        FilterOptions = await ledger.GetFilterOptionsAsync(fromUtc, toUtc, cancellationToken);
        if (toolActivities is not null)
            ToolFilterOptions = await toolActivities.GetToolFilterOptionsAsync(fromUtc, toUtc, cancellationToken);
        ValidateSelection(Subfeature, FilterOptions.Subfeatures, nameof(Subfeature), "subfeature");
        ValidateSelection(Application, FilterOptions.Applications, nameof(Application), "application");
        ValidateSelection(Provider, FilterOptions.Providers, nameof(Provider), "provider");
        ValidateSelection(ModelName, FilterOptions.Models, nameof(ModelName), "model");
        ValidateSelection(ToolName, ToolFilterOptions.ToolNames, nameof(ToolName), "tool");
        ValidateSelection(
            ToolExternalSource,
            ToolFilterOptions.ExternalSources,
            nameof(ToolExternalSource),
            "external source");
        if (!ModelState.IsValid) return;

        Report = await ledger.GetReportAsync(
            new AiUsageQuery(
                fromUtc,
                toUtc,
                Clean(UserId),
                Clean(Feature),
                Clean(Subfeature),
                Clean(Application),
                Clean(Provider),
                Clean(ModelName)),
            Dimension,
            cancellationToken);
        if (toolActivities is not null)
        {
            ToolReport = await toolActivities.GetToolReportAsync(
                new AiToolActivityQuery(
                    fromUtc,
                    toUtc,
                    Clean(UserId),
                    Clean(Feature),
                    Clean(Subfeature),
                    Clean(Application),
                    Clean(ToolName),
                    ToolStatus,
                    Clean(ToolExternalSource)),
                ToolDimension,
                cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostSaveLimitsAsync(CancellationToken cancellationToken)
    {
        if (limits is null || users is null) return NotFound();
        if (!ModelState.IsValid)
        {
            StatusMessage = "Daily AI consumption limits were not saved. Enter valid non-negative token limits.";
            return RedirectToPage();
        }
        try
        {
            var activeUsers = (await users.ListUsersAsync(cancellationToken))
                .Where(user => user.IsActive)
                .ToDictionary(user => user.UserId, StringComparer.OrdinalIgnoreCase);
            var overrides = new List<AiUsageUserDailyLimit>();
            foreach (var row in LimitInput.UserLimits.Where(row => row.DailyTokenLimit.HasValue))
            {
                var userId = row.UserId?.Trim() ?? string.Empty;
                if (!activeUsers.ContainsKey(userId))
                    throw new ArgumentException($"The user '{userId}' is not an active authorized user.");
                overrides.Add(new AiUsageUserDailyLimit(userId, row.DailyTokenLimit!.Value));
            }

            await limits.SaveSettingsAsync(new AiUsageLimitSettings(
                LimitInput.DefaultDailyTokenLimit,
                LimitInput.ResetTimeZoneId,
                overrides,
                LimitInput.NotificationsEnabled,
                ParseNotificationThresholds(LimitInput.NotificationThresholds)), cancellationToken);
            StatusMessage = "Daily AI consumption limits saved.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            StatusMessage = $"Daily AI consumption limits were not saved: {exception.Message}";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetExportUsageCsvAsync(CancellationToken cancellationToken)
    {
        var context = await LoadExportContextAsync(cancellationToken);
        if (context is null || exporter is null) return BadRequest("Correct the report filters before exporting.");
        return File(
            exporter.ExportUsageCsv(context),
            "text/csv; charset=utf-8",
            $"ai-usage-{context.FromDate:yyyyMMdd}-to-{context.ToDate:yyyyMMdd}.csv");
    }

    public async Task<IActionResult> OnGetExportToolCsvAsync(CancellationToken cancellationToken)
    {
        var context = await LoadExportContextAsync(cancellationToken);
        if (context is null || exporter is null) return BadRequest("Correct the report filters before exporting.");
        return File(
            exporter.ExportToolActivityCsv(context),
            "text/csv; charset=utf-8",
            $"ai-tool-activity-{context.FromDate:yyyyMMdd}-to-{context.ToDate:yyyyMMdd}.csv");
    }

    public async Task<IActionResult> OnGetExportExcelAsync(CancellationToken cancellationToken)
    {
        var context = await LoadExportContextAsync(cancellationToken);
        if (context is null || exporter is null) return BadRequest("Correct the report filters before exporting.");
        return File(
            exporter.ExportExcel(context),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"ai-usage-ledger-{context.FromDate:yyyyMMdd}-to-{context.ToDate:yyyyMMdd}.xlsx");
    }

    public static string DimensionLabel(AiUsageDimension dimension) => dimension switch
    {
        AiUsageDimension.User => "User",
        AiUsageDimension.Feature => "Feature",
        AiUsageDimension.Subfeature => "Subfeature",
        AiUsageDimension.Application => "Application",
        AiUsageDimension.Provider => "Provider",
        AiUsageDimension.Model => "Model and provider",
        AiUsageDimension.UserFeature => "User and feature",
        AiUsageDimension.FeatureModel => "Feature and model",
        AiUsageDimension.UserFeatureModel => "User, feature and model",
        AiUsageDimension.Day => "UTC day",
        _ => dimension.ToString()
    };

    public static string ToolDimensionLabel(AiToolActivityDimension dimension) => dimension switch
    {
        AiToolActivityDimension.Tool => "Tool",
        AiToolActivityDimension.User => "User",
        AiToolActivityDimension.UserTool => "User and tool",
        AiToolActivityDimension.ToolResource => "Tool and resource",
        AiToolActivityDimension.ExternalSource => "External source",
        AiToolActivityDimension.Status => "Status",
        AiToolActivityDimension.Day => "UTC day",
        _ => dimension.ToString()
    };

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<AiUsageReportExportContext?> LoadExportContextAsync(
        CancellationToken cancellationToken)
    {
        await OnGetAsync(cancellationToken);
        if (!ModelState.IsValid || !FromDate.HasValue || !ToDate.HasValue) return null;
        return new AiUsageReportExportContext(
            FromDate.Value,
            ToDate.Value,
            DimensionLabel(Dimension),
            PrimaryHeading,
            SecondaryHeading,
            TertiaryHeading,
            Report,
            ToolDimensionLabel(ToolDimension),
            ToolPrimaryHeading,
            ToolSecondaryHeading,
            ToolReport);
    }

    private static IReadOnlyList<int> ParseNotificationThresholds(string value)
    {
        var values = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0 || values.Length > 10
            || values.Any(item => !int.TryParse(item, out var percentage)
                || percentage is < 1 or > 100))
            throw new ArgumentException(
                "Notification thresholds must be 1 to 10 comma-separated percentages from 1 through 100.");
        return values.Select(int.Parse).Distinct().Order().ToList();
    }

    private void ValidateSelection(
        string? selection,
        IReadOnlyList<string> available,
        string propertyName,
        string label)
    {
        if (string.IsNullOrWhiteSpace(selection)) return;
        if (available.Any(value => string.Equals(value, selection.Trim(), StringComparison.OrdinalIgnoreCase)))
            return;
        ModelState.AddModelError(propertyName, $"Select an available {label}.");
    }

    private async Task LoadLimitInputAsync(CancellationToken cancellationToken)
    {
        if (limits is null || users is null) return;
        var settings = await limits.GetSettingsAsync(cancellationToken);
        var overrides = settings.UserLimits.ToDictionary(
            item => item.UserId,
            StringComparer.OrdinalIgnoreCase);
        LimitInput = new AiUsageLimitInput
        {
            DefaultDailyTokenLimit = settings.DefaultDailyTokenLimit,
            ResetTimeZoneId = settings.ResetTimeZoneId,
            NotificationsEnabled = settings.NotificationsEnabled,
            NotificationThresholds = string.Join(", ",
                settings.NotificationThresholdPercentages ?? [75, 90, 100]),
            UserLimits = (await users.ListUsersAsync(cancellationToken))
                .Where(user => user.IsActive)
                .OrderBy(user => user.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(user => user.UserId, StringComparer.OrdinalIgnoreCase)
                .Select(user => new AiUsageUserLimitInput
                {
                    UserId = user.UserId,
                    DisplayName = user.DisplayName,
                    DailyTokenLimit = overrides.GetValueOrDefault(user.UserId)?.DailyTokenLimit
                })
                .ToList()
        };
        TimeZones = TimeZoneInfo.GetSystemTimeZones()
            .Select(timeZone => new TimeZoneOption(timeZone.Id, timeZone.DisplayName))
            .ToList();
    }
}

public sealed class AiUsageLimitInput
{
    [Range(0, long.MaxValue)]
    public long? DefaultDailyTokenLimit { get; set; }

    [Required, StringLength(100)]
    public string ResetTimeZoneId { get; set; } = "Asia/Kolkata";

    public bool NotificationsEnabled { get; set; } = true;

    [Required, StringLength(100)]
    public string NotificationThresholds { get; set; } = "75, 90, 100";

    public List<AiUsageUserLimitInput> UserLimits { get; set; } = [];
}

public sealed class AiUsageUserLimitInput
{
    [Required, StringLength(320)]
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    [Range(0, long.MaxValue)]
    public long? DailyTokenLimit { get; set; }
}

public sealed record TimeZoneOption(string Id, string DisplayName);
