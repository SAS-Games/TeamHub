using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.AI.Usage.Contracts;
using TeamHub.Authentication;

namespace TeamHub.Web.Pages.Configuration;

[Authorize(Roles = TeamHubUserTypes.Admin)]
public sealed class AIUsageModel(
    IAiUsageLedger ledger,
    IAiUsageLimitService? limits = null,
    IUserAccessService? users = null) : PageModel
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

    [BindProperty]
    public AiUsageLimitInput LimitInput { get; set; } = new();

    public AiUsageReport Report { get; private set; } =
        new(new AiUsageTotals(0, 0, 0, 0, 0), []);
    public AiUsageFilterOptions FilterOptions { get; private set; } = new([], [], [], []);
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

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadLimitInputAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        FromDate ??= today.AddDays(-29);
        ToDate ??= today;

        if (!Enum.IsDefined(Dimension))
            ModelState.AddModelError(nameof(Dimension), "Select a supported grouping.");
        if (FromDate > ToDate)
            ModelState.AddModelError(nameof(ToDate), "The end date must be on or after the start date.");
        if (FromDate > ToDate) return;

        var fromUtc = new DateTimeOffset(FromDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toUtc = new DateTimeOffset(ToDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        FilterOptions = await ledger.GetFilterOptionsAsync(fromUtc, toUtc, cancellationToken);
        ValidateSelection(Subfeature, FilterOptions.Subfeatures, nameof(Subfeature), "subfeature");
        ValidateSelection(Application, FilterOptions.Applications, nameof(Application), "application");
        ValidateSelection(Provider, FilterOptions.Providers, nameof(Provider), "provider");
        ValidateSelection(ModelName, FilterOptions.Models, nameof(ModelName), "model");
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
                overrides), cancellationToken);
            StatusMessage = "Daily AI consumption limits saved.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            StatusMessage = $"Daily AI consumption limits were not saved: {exception.Message}";
        }
        return RedirectToPage();
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

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
