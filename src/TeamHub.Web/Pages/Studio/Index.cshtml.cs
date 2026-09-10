using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using TeamHub.Milestones;
using TeamHub.Studio;

namespace TeamHub.Web.Pages.Studio;

public class IndexModel(IStudioDirectoryService studioDirectoryService, IMilestoneTrackerService milestoneTrackerService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? StudioId { get; set; }

    public DateTime TodayUtc { get; private set; } = DateTime.UtcNow.Date;
    public IReadOnlyList<StudioDashboardItem> Studios { get; private set; } = [];
    public StudioDashboardItem? SelectedStudio { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? SelectionMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        TodayUtc = nowUtc.Date;
        var studios = await studioDirectoryService.GetStudiosAsync(cancellationToken);
        IReadOnlyList<MilestoneDto> milestones = [];

        try
        {
            milestones = await milestoneTrackerService.GetMilestonesAsync(cancellationToken);
        }
        catch (FileNotFoundException)
        {
            ErrorMessage = "Milestone data was not found. Studio details are still available.";
        }
        catch (InvalidDataException ex)
        {
            ErrorMessage = ex.Message;
        }

        Studios = studios
            .Select(studio =>
            {
                var timeZone = ResolveTimeZone(studio.TimeZoneId);
                return new StudioDashboardItem(
                    studio,
                    milestones
                    .Where(milestone => IsProjectMilestone(studio.ProjectName, milestone))
                    .Where(milestone => !milestone.Milestone.StartsWith("Total MS", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(milestone => milestone.DeliveryDate ?? DateTime.MaxValue)
                    .ToList(),
                    TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone),
                    timeZone.DisplayName);
            })
            .ToList();

        SelectedStudio = SelectStudio(Studios);
    }

    private StudioDashboardItem? SelectStudio(IReadOnlyList<StudioDashboardItem> studios)
    {
        if (studios.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(StudioId))
        {
            return studios[0];
        }

        var selected = studios.FirstOrDefault(item => string.Equals(item.Studio.Id, StudioId, StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            return selected;
        }

        SelectionMessage = "The selected studio was not found. Showing the first configured studio.";
        return studios[0];
    }

    private static bool IsProjectMilestone(string projectName, MilestoneDto milestone)
    {
        return string.Equals(milestone.Title, projectName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(milestone.Program, projectName, StringComparison.OrdinalIgnoreCase);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

public sealed record StudioDashboardItem(
    StudioDetails Studio,
    IReadOnlyList<MilestoneDto> Milestones,
    DateTime LocalTime,
    string TimeZoneDisplayName);
