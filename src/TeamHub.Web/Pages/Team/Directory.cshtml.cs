using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Team;

namespace TeamHub.Web.Pages.Team;

public class DirectoryModel(ITeamDirectoryService teamDirectoryService) : PageModel
{
    public TeamDirectoryDto Directory { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Directory = await teamDirectoryService.GetTeamDirectoryAsync(cancellationToken);
}
