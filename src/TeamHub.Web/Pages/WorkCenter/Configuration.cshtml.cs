using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.Domain.Entities;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Web.Pages.WorkCenter;

[Authorize(Roles = "Admin")]
public class ConfigurationModel(IWorkflowConfigurationService configurationService, WorkflowDbContext dbContext) : PageModel
{
    [BindProperty]
    public WorkflowDraftInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public IReadOnlyList<WorkflowDraftSummaryDto> Drafts { get; private set; } = [];
    public IReadOnlyList<WorkflowDefinition> PublishedDefinitions { get; private set; } = [];
    public bool ShowForm { get; private set; }

    public async Task OnGetAsync(Guid? draftId = null, bool create = false, CancellationToken cancellationToken = default)
    {
        ShowForm = create || draftId.HasValue;
        if (draftId.HasValue)
        {
            var draft = await configurationService.GetDraftAsync(draftId.Value, cancellationToken);
            if (draft is not null)
            {
                Input = WorkflowDraftInput.FromDraft(draft);
            }
        }

        await LoadAsync(cancellationToken);
        EnsureEditableRows();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        ShowForm = true;
        NormalizeInputRows();
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            EnsureEditableRows();
            return Page();
        }

        var draftId = await configurationService.SaveDraftAsync(Input.ToDraft(), cancellationToken);
        StatusMessage = "Workflow draft saved.";
        return RedirectToPage(new { draftId });
    }

    public async Task<IActionResult> OnPostPublishAsync(CancellationToken cancellationToken)
    {
        ShowForm = true;
        NormalizeInputRows();
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            EnsureEditableRows();
            return Page();
        }

        var draftId = await configurationService.SaveDraftAsync(Input.ToDraft(), cancellationToken);
        var result = await configurationService.PublishDraftAsync(draftId, User.Identity?.Name ?? "admin@local", cancellationToken);
        if (!result.Success)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            Input.Id = draftId;
            await LoadAsync(cancellationToken);
            EnsureEditableRows();
            return Page();
        }

        StatusMessage = $"Workflow published: {string.Join("; ", result.ImportedWorkflowSummaries)}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid draftId, CancellationToken cancellationToken)
    {
        await configurationService.DeleteDraftAsync(draftId, cancellationToken);
        StatusMessage = "Workflow draft deleted.";
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Drafts = await configurationService.GetDraftsAsync(cancellationToken);
        PublishedDefinitions = await dbContext.WorkflowDefinitions
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.WorkflowKey)
            .ToListAsync(cancellationToken);
    }

    private void NormalizeInputRows()
    {
        Input.Steps = (Input.Steps ?? [])
            .Where(step => !step.Remove)
            .Where(step => !string.IsNullOrWhiteSpace(step.StepKey)
                || !string.IsNullOrWhiteSpace(step.StepName)
                || !string.IsNullOrWhiteSpace(step.Owner)
                || !string.IsNullOrWhiteSpace(step.Description)
                || !string.IsNullOrWhiteSpace(step.DependsOnCsv))
            .ToList();
    }

    private void EnsureEditableRows()
    {
        Input.Steps.Add(new WorkflowDraftStepInput { Enabled = true, Required = true });
    }

    public sealed class WorkflowDraftInput
    {
        public Guid? Id { get; set; }

        [System.ComponentModel.DataAnnotations.Required]
        public string WorkflowKey { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.Required]
        public string WorkflowName { get; set; } = string.Empty;

        public string? Description { get; set; }
        public bool Enabled { get; set; } = true;
        public List<WorkflowDraftStepInput> Steps { get; set; } = [];

        public static WorkflowDraftInput FromDraft(WorkflowDraftDto draft)
        {
            return new WorkflowDraftInput
            {
                Id = draft.Id,
                WorkflowKey = draft.WorkflowKey,
                WorkflowName = draft.WorkflowName,
                Description = draft.Description,
                Enabled = draft.Enabled,
                Steps = draft.Steps.Select(step => new WorkflowDraftStepInput
                {
                    StepKey = step.StepKey,
                    StepName = step.StepName,
                    Description = step.Description,
                    OwnerType = step.OwnerType,
                    Owner = step.Owner,
                    ExpectedDurationHours = step.ExpectedDurationHours,
                    DependsOnCsv = step.DependsOnCsv,
                    ReminderAfterHours = step.ReminderAfterHours,
                    ReminderRepeatHours = step.ReminderRepeatHours,
                    EscalationAfterHours = step.EscalationAfterHours,
                    EscalationOwner = step.EscalationOwner,
                    Required = step.Required,
                    Enabled = step.Enabled,
                    SortOrder = step.SortOrder
                }).ToList()
            };
        }

        public WorkflowDraftDto ToDraft()
        {
            return new WorkflowDraftDto
            {
                Id = Id,
                WorkflowKey = WorkflowKey,
                WorkflowName = WorkflowName,
                Description = Description,
                Enabled = Enabled,
                Steps = Steps.Select(step => new WorkflowDraftStepDto
                {
                    StepKey = step.StepKey ?? string.Empty,
                    StepName = step.StepName ?? string.Empty,
                    Description = step.Description,
                    OwnerType = string.IsNullOrWhiteSpace(step.OwnerType) ? "Email" : step.OwnerType,
                    Owner = step.Owner ?? string.Empty,
                    ExpectedDurationHours = step.ExpectedDurationHours,
                    DependsOnCsv = step.DependsOnCsv ?? string.Empty,
                    ReminderAfterHours = step.ReminderAfterHours,
                    ReminderRepeatHours = step.ReminderRepeatHours,
                    EscalationAfterHours = step.EscalationAfterHours,
                    EscalationOwner = step.EscalationOwner,
                    Required = step.Required,
                    Enabled = step.Enabled,
                    SortOrder = step.SortOrder
                }).ToList()
            };
        }
    }

    public sealed class WorkflowDraftStepInput
    {
        public string? StepKey { get; set; }
        public string? StepName { get; set; }
        public string? Description { get; set; }
        public string? OwnerType { get; set; } = "Email";
        public string? Owner { get; set; }
        public double ExpectedDurationHours { get; set; }
        public string? DependsOnCsv { get; set; }
        public double? ReminderAfterHours { get; set; }
        public double? ReminderRepeatHours { get; set; }
        public double? EscalationAfterHours { get; set; }
        public string? EscalationOwner { get; set; }
        public bool Required { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public int SortOrder { get; set; }
        public bool Remove { get; set; }
    }
}
