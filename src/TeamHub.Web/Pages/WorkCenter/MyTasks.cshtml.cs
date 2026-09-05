using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;

namespace TeamHub.Web.Pages.WorkCenter;

public class MyTasksModel(IWorkflowReadService readService, IWorkflowEngine workflowEngine) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Owner { get; set; } = "it@company.com";

    public bool IsAdmin => User.IsInRole("Admin");

    public IReadOnlyList<TaskItemDto> Tasks { get; private set; } = [];
    public string? ResultMessage { get; private set; }

    public async Task OnGetAsync()
    {
        Owner = User.Identity?.Name ?? string.Empty;
        Tasks = await readService.GetMyTasksAsync(Owner, IsAdmin);
    }

    public async Task<IActionResult> OnPostCompleteAsync(Guid stepInstanceId)
    {
        Owner = User.Identity?.Name ?? string.Empty;
        await workflowEngine.CompleteStepAsync(new StepCompletionRequest
        {
            StepInstanceId = stepInstanceId,
            Actor = Owner,
            IsAdminOverride = User.IsInRole("Admin"),
            Comment = Comment
        });

        ResultMessage = "Task completed.";
        Comment = null;
        Tasks = await readService.GetMyTasksAsync(Owner, IsAdmin);
        return Page();
    }

    [BindProperty]
    public string? Comment { get; set; }
}
