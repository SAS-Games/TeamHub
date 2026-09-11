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

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Studios = await studioDirectoryService.GetStudiosAsync(cancellationToken);
        Studio = SelectStudio();
        if (Studio is null)
        {
            ErrorMessage = "No studio has been configured yet.";
            return;
        }

        SetDefaultDateRange();
        UpdateResult = await confluenceUpdateService.GetUpdatesAsync(new StudioConfluenceUpdateQuery
        {
            StudioId = Studio.Id,
            RequestingUserId = User.Identity?.Name ?? string.Empty,
            AllowPrivilegedDefaultCredential = User.IsInRole("Privileged"),
            StartDate = StartDate,
            EndDate = EndDate
        }, cancellationToken);
    }

    private void SetDefaultDateRange()
    {
        if (StartDate.HasValue && EndDate.HasValue) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var currentWeekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        StartDate ??= EndDate ?? currentWeekStart;
        EndDate ??= StartDate.Value.AddDays(4);
    }

    private StudioDetails? SelectStudio()
    {
        if (Studios.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(StudioId)) return Studios[0];
        return Studios.FirstOrDefault(studio => string.Equals(studio.Id, StudioId, StringComparison.OrdinalIgnoreCase)) ?? Studios[0];
    }
}
