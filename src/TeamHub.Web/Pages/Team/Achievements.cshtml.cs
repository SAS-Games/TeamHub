using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Team;

namespace TeamHub.Web.Pages.Team;

public sealed class AchievementsModel(
    ITeamAchievementService achievementService,
    IPageTextAppearanceService appearanceService) : PageModel
{
    public IReadOnlyList<TeamAchievementDto> Achievements { get; private set; } = [];
    public PageTextAppearanceDto Appearance { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Achievements = await achievementService.GetAchievementsAsync(cancellationToken);
        Appearance = await appearanceService.GetPageAppearanceAsync(
            TeamPageAppearanceCatalog.Achievements,
            cancellationToken);
    }
}
