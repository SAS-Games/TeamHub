using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Milestones;
using TeamHub.Studio;

namespace TeamHub.Web.Pages;

public class HeroProjectStudiosModel(
    IStudioDirectoryService studioDirectoryService,
    IMilestoneTrackerService milestoneTrackerService) : PageModel
{
    public IReadOnlyList<HeroProjectStudioItem> Studios { get; private set; } = [];
    public string? ErrorMessage { get; private set; }
    public string? MilestoneWarning { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StudioDetails> studios;
        try
        {
            studios = await studioDirectoryService.GetStudiosAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        IReadOnlyList<MilestoneDto> milestones = [];
        try
        {
            milestones = await milestoneTrackerService.GetMilestonesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or InvalidOperationException)
        {
            MilestoneWarning = $"Release dates are unavailable: {ex.Message}";
        }

        Studios = studios
            .OrderByDescending(studio => studio.IsActive)
            .ThenBy(studio => studio.GroupDisplayOrder ?? int.MaxValue)
            .ThenBy(studio => studio.StudioGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(studio => studio.StudioDisplayOrder ?? int.MaxValue)
            .ThenBy(studio => studio.StudioName, StringComparer.OrdinalIgnoreCase)
            .Select(studio => new HeroProjectStudioItem(
                studio,
                milestones.LastOrDefault(milestone =>
                    milestone.ReleaseDate.HasValue && IsProjectMilestone(studio.ProjectName, milestone))?.ReleaseDate))
            .ToList();
    }

    private static bool IsProjectMilestone(string projectName, MilestoneDto milestone) =>
        string.Equals(milestone.Title, projectName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(milestone.Program, projectName, StringComparison.OrdinalIgnoreCase);
}

public sealed record HeroProjectStudioItem(StudioDetails Studio, DateTime? ReleaseDate);