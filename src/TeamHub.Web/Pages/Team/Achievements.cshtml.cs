using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Team;

namespace TeamHub.Web.Pages.Team;

public sealed class AchievementsModel(ITeamAchievementService achievementService) : PageModel
{
    public IReadOnlyList<TeamAchievementDto> Achievements { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Achievements = await achievementService.GetAchievementsAsync(cancellationToken);
}
