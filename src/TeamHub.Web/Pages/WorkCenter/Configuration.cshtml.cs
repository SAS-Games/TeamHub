using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TeamHub.Application.Interfaces;
using TeamHub.Domain.Entities;
using TeamHub.Infrastructure.Options;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Web.Pages.WorkCenter;

[Authorize(Roles = "Admin")]
public class ConfigurationModel(
    IWorkflowConfigurationService configurationService,
    WorkflowDbContext dbContext,
    IOptions<WorkflowConfigurationOptions> options,
    IOptions<EmailNotificationOptions> emailOptions) : PageModel
{
    public string ExcelPath { get; private set; } = options.Value.ExcelPath;
    public bool EmailEnabled { get; private set; } = emailOptions.Value.Enabled;
    public string EmailHost { get; private set; } = emailOptions.Value.Host;
    public string FromAddress { get; private set; } = emailOptions.Value.FromAddress;
    public string? LastResult { get; private set; }
    public IReadOnlyList<WorkflowDefinition> Definitions { get; private set; } = [];

    public async Task OnGetAsync()
    {
        await LoadDefinitionsAsync();
    }

    public async Task<IActionResult> OnPostSyncAsync()
    {
        var result = await configurationService.SyncAsync("admin@local");
        LastResult = result.Success
            ? $"Sync completed: {string.Join("; ", result.ImportedWorkflowSummaries)}"
            : $"Sync failed: {string.Join("; ", result.Errors)}";

        await LoadDefinitionsAsync();
        return Page();
    }

    private async Task LoadDefinitionsAsync()
    {
        Definitions = await dbContext.WorkflowDefinitions
            .OrderBy(x => x.WorkflowKey)
            .ThenByDescending(x => x.Version)
            .ToListAsync();
    }
}
