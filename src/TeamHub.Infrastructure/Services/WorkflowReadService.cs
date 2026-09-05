using Microsoft.EntityFrameworkCore;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.Domain.Enums;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Infrastructure.Services;

public sealed class WorkflowReadService(WorkflowDbContext dbContext, IClock clock) : IWorkflowReadService
{
    public async Task<DashboardSummaryDto> GetDashboardSummaryAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        return new DashboardSummaryDto
        {
            ActiveWorkflows = await dbContext.WorkflowInstances.CountAsync(x => x.Status == WorkflowInstanceStatus.InProgress, cancellationToken),
            OverdueSteps = await dbContext.WorkflowStepInstances.CountAsync(x => x.Status == WorkflowStepStatus.InProgress && x.DueAtUtc < now, cancellationToken),
            CompletedThisWeek = await dbContext.WorkflowInstances.CountAsync(x => x.Status == WorkflowInstanceStatus.Completed && x.CompletedAtUtc >= now.AddDays(-7), cancellationToken),
            ActiveSteps = await dbContext.WorkflowStepInstances.CountAsync(x => x.Status == WorkflowStepStatus.InProgress, cancellationToken)
        };
    }

    public async Task<IReadOnlyList<WorkflowSummaryDto>> GetWorkflowSummariesAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var instances = await dbContext.WorkflowInstances
            .Include(x => x.WorkflowDefinition)
            .Include(x => x.Steps)
            .OrderByDescending(x => x.StartedAtUtc)
            .ToListAsync(cancellationToken);

        return instances.Select(instance =>
        {
            var requiredSteps = instance.Steps.Where(x => x.Required).ToList();
            var completedRequired = requiredSteps.Count(x => x.Status is WorkflowStepStatus.Completed or WorkflowStepStatus.Skipped);
            var totalRequired = requiredSteps.Count;
            var progress = totalRequired == 0 ? 100 : (int)Math.Round((double)completedRequired * 100 / totalRequired);

            var activeSteps = instance.Steps.Where(x => x.Status == WorkflowStepStatus.InProgress).ToList();

            return new WorkflowSummaryDto
            {
                Id = instance.Id,
                InstanceName = instance.InstanceName,
                WorkflowName = instance.WorkflowDefinition.Name,
                Version = instance.WorkflowDefinition.Version,
                Status = instance.Status,
                StartedAtUtc = instance.StartedAtUtc,
                NextDueAtUtc = activeSteps.Where(x => x.DueAtUtc.HasValue).Select(x => x.DueAtUtc).Min(),
                ProgressPercent = progress,
                OverdueCount = activeSteps.Count(x => x.DueAtUtc < now),
                ActiveOwners = string.Join(", ", activeSteps.Select(x => x.Owner).Distinct(StringComparer.OrdinalIgnoreCase)),
                ActiveSteps = string.Join(", ", activeSteps.Select(x => x.StepName))
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<TaskItemDto>> GetMyTasksAsync(string owner, bool includeAllOwners = false, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var normalizedOwner = owner ?? string.Empty;
        var normalizedOwnerForQuery = normalizedOwner.ToLowerInvariant();

        var steps = await dbContext.WorkflowStepInstances
            .Include(x => x.WorkflowInstance)
            .ThenInclude(x => x.WorkflowDefinition)
            .Where(x => x.Status == WorkflowStepStatus.InProgress)
            .Where(x => !string.IsNullOrWhiteSpace(x.Owner))
            .Where(x => includeAllOwners || x.Owner!.ToLower() == normalizedOwnerForQuery)
            .OrderBy(x => x.DueAtUtc)
            .ToListAsync(cancellationToken);

        return steps.Select(step => new TaskItemDto
        {
            StepInstanceId = step.Id,
            WorkflowInstanceId = step.WorkflowInstanceId,
            InstanceName = step.WorkflowInstance?.InstanceName ?? string.Empty,
            WorkflowName = step.WorkflowInstance?.WorkflowDefinition?.Name ?? string.Empty,
            StepName = step.StepName,
            Owner = step.Owner ?? string.Empty,
            DueAtUtc = step.DueAtUtc,
            IsOverdue = step.DueAtUtc < now
        }).ToList();
    }
}
