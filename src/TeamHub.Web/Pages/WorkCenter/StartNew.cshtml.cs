using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Web.Pages.WorkCenter;

[Authorize(Roles = "Admin")]
public class StartNewModel(
    WorkflowDbContext dbContext,
    IWorkflowEngine workflowEngine) : PageModel
{
    [BindProperty]
    public StartInput Input { get; set; } = new();

    public IReadOnlyList<string> WorkflowKeys { get; private set; } = [];
    public string? ResultMessage { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadWorkflowKeysAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadWorkflowKeysAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        await workflowEngine.StartWorkflowAsync(new StartWorkflowRequest
        {
            WorkflowKey = Input.WorkflowKey,
            InstanceName = Input.InstanceName,
            Description = Input.Description,
            StartedBy = "admin@local"
        });

        ResultMessage = "Workflow started successfully.";
        Input = new StartInput();
        return Page();
    }

    private async Task LoadWorkflowKeysAsync()
    {
        WorkflowKeys = await dbContext.WorkflowDefinitions
            .Where(x => x.Enabled)
            .GroupBy(x => x.WorkflowKey)
            .Select(g => g.Key)
            .OrderBy(x => x)
            .ToListAsync();
    }

    public sealed class StartInput
    {
        public string WorkflowKey { get; set; } = string.Empty;
        public string InstanceName { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
