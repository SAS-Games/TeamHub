using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Milestones;

namespace TeamHub.Web.Pages;

public class MilestonesModel(IMilestoneTrackerService milestoneTrackerService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string View { get; set; } = "table";

    public DateTime TodayUtc { get; private set; } = DateTime.UtcNow.Date;
    public IReadOnlyList<MilestoneDto> Milestones { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        TodayUtc = DateTime.UtcNow.Date;
        View = string.Equals(View, "roadmap", StringComparison.OrdinalIgnoreCase) ? "roadmap" : "table";
        try
        {
            Milestones = await milestoneTrackerService.GetMilestonesAsync(cancellationToken);
        }
        catch (FileNotFoundException)
        {
            ErrorMessage = "Milestones.xlsx was not found. Add it to the configured path and reload this page.";
        }
        catch (InvalidDataException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}