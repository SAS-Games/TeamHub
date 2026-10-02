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

    public async Task OnGetAsync(string? workflowKey, bool published, CancellationToken cancellationToken)
    {
        await LoadWorkflowsAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(workflowKey)
            && Workflows.Any(workflow => string.Equals(workflow.Key, workflowKey, StringComparison.OrdinalIgnoreCase)))
        {
            Input.WorkflowKey = workflowKey;
        }

        if (published)
        {
            ResultMessage = "Workflow published successfully and is now available to start.";
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await LoadWorkflowsAsync(cancellationToken);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await workflowEngine.StartWorkflowAsync(new StartWorkflowRequest
            {
                WorkflowKey = Input.WorkflowKey,
                InstanceName = Input.InstanceName,
                Description = Input.Description,
                StartedBy = User.Identity?.Name ?? "admin@local"
            }, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }

        ResultMessage = "Workflow started successfully.";
        Input = new StartInput();
        return Page();
    }

    private async Task LoadWorkflowsAsync(CancellationToken cancellationToken)
    {
        Workflows = await dbContext.WorkflowDefinitions
            .AsNoTracking()
            .Where(definition => definition.Enabled && definition.IsActive)
            .OrderBy(definition => definition.Name)
            .Select(definition => new WorkflowOption(
                definition.WorkflowKey,
                definition.Name,
                definition.Version,
                definition.Description,
                definition.Steps.Count(step => step.Enabled),
                definition.ImportedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public sealed class StartInput
    {
        [System.ComponentModel.DataAnnotations.Required]
        public string WorkflowKey { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.Required]
        public string InstanceName { get; set; } = string.Empty;

        public string? Description { get; set; }
    }

    public sealed record WorkflowOption(
        string Key,
        string Name,
        int Version,
        string? Description,
        int StepCount,
        DateTime PublishedAtUtc);
}
