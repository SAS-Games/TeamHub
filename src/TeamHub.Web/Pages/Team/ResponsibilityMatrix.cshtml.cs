using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Team;

namespace TeamHub.Web.Pages.Team;

public class ResponsibilityMatrixModel(
    ITeamDirectoryService teamDirectoryService,
    IPageTextAppearanceService appearanceService) : PageModel
{
    public TeamDirectoryDto Directory { get; private set; } = new();
    public PageTextAppearanceDto Appearance { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Directory = await teamDirectoryService.GetTeamDirectoryAsync(cancellationToken);
        Appearance = await appearanceService.GetPageAppearanceAsync(
            TeamPageAppearanceCatalog.ResponsibilityMatrix,
            cancellationToken);
    }
}
