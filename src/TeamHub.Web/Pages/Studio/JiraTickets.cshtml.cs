using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Studio;

namespace TeamHub.Web.Pages.Studio;

public class JiraTicketsModel(IStudioDirectoryService studioDirectoryService, IStudioJiraTicketService jiraTicketService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? StudioId { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool ActiveSprintOnly { get; set; } = true;

    [BindProperty(SupportsGet = true)]
    public DateOnly? StartDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? EndDate { get; set; }

    public StudioDetails? Studio { get; private set; }
    public IReadOnlyList<StudioDetails> Studios { get; private set; } = [];
    public StudioJiraTicketResult TicketResult { get; private set; } = new();
    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Studios = (await studioDirectoryService.GetStudiosAsync(cancellationToken))
            .Where(studio => studio.IsActive)
            .OrderBy(studio => studio.StudioGroup)
            .ThenBy(studio => studio.StudioName)
            .ToList();
        Studio = SelectStudio();
        if (Studio is null)
        {
            ErrorMessage = "No studio has been configured yet.";
            return;
        }

        TicketResult = await jiraTicketService.GetTicketsAsync(new StudioJiraTicketQuery
        {
            StudioId = Studio.Id,
            RequestingUserId = User.Identity?.Name ?? string.Empty,
            AllowPrivilegedDefaultCredential = User.IsInRole("Privileged"),
            ActiveSprintOnly = ActiveSprintOnly,
            StartDate = StartDate,
            EndDate = EndDate
        }, cancellationToken);
    }

    private StudioDetails? SelectStudio()
    {
        if (Studios.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(StudioId))
        {
            return Studios[0];
        }

        return Studios.FirstOrDefault(studio => string.Equals(studio.Id, StudioId, StringComparison.OrdinalIgnoreCase)) ?? Studios[0];
    }
}
