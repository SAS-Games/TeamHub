using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Studio;

namespace TeamHub.Web.Pages.Studio;

public sealed class WeeklyUpdatesModel(
    IStudioDirectoryService studioDirectoryService,
    IStudioConfluenceUpdateService confluenceUpdateService) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? StudioId { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? StartDate { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? EndDate { get; set; }

    public StudioDetails? Studio { get; private set; }
    public IReadOnlyList<StudioDetails> Studios { get; private set; } = [];
    public StudioConfluenceUpdateResult UpdateResult { get; private set; } = new();
    public string? ErrorMessage { get; private set; }
    public bool IsConsolidated => string.IsNullOrWhiteSpace(StudioId)
        || string.Equals(StudioId, "all", StringComparison.OrdinalIgnoreCase);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Studios = (await studioDirectoryService.GetStudiosAsync(cancellationToken))
            .Where(studio => studio.IsActive)
            .OrderBy(studio => studio.StudioGroup)
            .ThenBy(studio => studio.StudioName)
            .ToList();
        if (Studios.Count == 0)
        {
            ErrorMessage = "No active studio project has been configured yet.";
            return;
        }

        SetDefaultDateRange();
        if (IsConsolidated)
        {
            UpdateResult = await confluenceUpdateService.GetConsolidatedUpdatesAsync(
                new ConsolidatedStudioConfluenceUpdateQuery
                {
                    StudioIds = Studios.Select(studio => studio.Id).ToList(),
                    RequestingUserId = User.Identity?.Name ?? string.Empty,
                    AllowPrivilegedDefaultCredential = User.IsInRole("Privileged"),
                    StartDate = StartDate,
                    EndDate = EndDate
                }, cancellationToken);
            return;
        }

        Studio = Studios.FirstOrDefault(studio => string.Equals(studio.Id, StudioId, StringComparison.OrdinalIgnoreCase));
        if (Studio is null)
        {
            ErrorMessage = "The selected studio is no longer available.";
            return;
        }

        UpdateResult = await confluenceUpdateService.GetUpdatesAsync(new StudioConfluenceUpdateQuery
        {
            StudioId = Studio.Id,
            RequestingUserId = User.Identity?.Name ?? string.Empty,
            AllowPrivilegedDefaultCredential = User.IsInRole("Privileged"),
            StartDate = StartDate,
            EndDate = EndDate
        }, cancellationToken);
    }

    public string GetStudioName(string studioId) =>
        FindStudio(studioId)?.StudioName ?? studioId;

    public string GetStudioGroup(string studioId) =>
        FindStudio(studioId)?.StudioGroup ?? "Ungrouped";

    private StudioDetails? FindStudio(string studioId) =>
        Studios.FirstOrDefault(studio => string.Equals(studio.Id, studioId, StringComparison.OrdinalIgnoreCase));

    private void SetDefaultDateRange()
    {
        if (StartDate.HasValue && EndDate.HasValue) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var currentWeekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        StartDate ??= EndDate ?? currentWeekStart;
        EndDate ??= StartDate.Value.AddDays(4);
    }
}
