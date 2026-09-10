using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Web.Pages.WorkCenter;

[Authorize]
public class StartNewModel(
    WorkflowDbContext dbContext,
    IWorkflowEngine workflowEngine) : PageModel
{
    [BindProperty]
    public StartInput Input { get; set; } = new();

    public IReadOnlyList<WorkflowOption> Workflows { get; private set; } = [];
    public string? ResultMessage { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadWorkflowsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadWorkflowsAsync();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        await workflowEngine.StartWorkflowAsync(new StartWorkflowRequest
        {
            WorkflowKey = Input.WorkflowKey,
            InstanceName = Input.InstanceName,
            Description = Input.Description,
            StartedBy = User.Identity?.Name ?? "admin@local"
        });

        ResultMessage = "Workflow started successfully.";
        Input = new StartInput();
        return Page();
    }

    private async Task LoadWorkflowsAsync()
    {
        Workflows = await dbContext.WorkflowDefinitions
            .Where(definition => definition.Enabled && definition.IsActive)
            .OrderBy(definition => definition.Name)
            .Select(definition => new WorkflowOption(definition.WorkflowKey, definition.Name, definition.Version))
            .ToListAsync();
    }

    public sealed class StartInput
    {
        public string WorkflowKey { get; set; } = string.Empty;
        public string InstanceName { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    public sealed record WorkflowOption(string Key, string Name, int Version);
}
