using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Team;

namespace TeamHub.Web.Pages.Team;

public class ResponsibilityMatrixModel(ITeamDirectoryService teamDirectoryService) : PageModel
{
    public TeamDirectoryDto Directory { get; private set; } = new();
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory = await teamDirectoryService.GetTeamDirectoryAsync(cancellationToken);
        }
        catch (FileNotFoundException)
        {
            ErrorMessage = "TeamInfo.xlsx was not found. Add it to the configured path and reload this page.";
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
