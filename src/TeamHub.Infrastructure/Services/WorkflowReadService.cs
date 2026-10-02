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
            OverdueSteps = await dbContext.WorkflowStepInstances.CountAsync(x => x.WorkflowInstance.Status == WorkflowInstanceStatus.InProgress && x.Status == WorkflowStepStatus.InProgress && x.DueAtUtc < now, cancellationToken),
            CompletedThisWeek = await dbContext.WorkflowInstances.CountAsync(x => x.Status == WorkflowInstanceStatus.Completed && x.CompletedAtUtc >= now.AddDays(-7), cancellationToken),
            ActiveSteps = await dbContext.WorkflowStepInstances.CountAsync(x => x.WorkflowInstance.Status == WorkflowInstanceStatus.InProgress && x.Status == WorkflowStepStatus.InProgress, cancellationToken)
        };
    }

    public async Task<IReadOnlyList<WorkflowSummaryDto>> GetWorkflowSummariesAsync(
        WorkflowInstanceStatus? status = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var query = dbContext.WorkflowInstances
            .Include(x => x.WorkflowDefinition)
            .Include(x => x.Steps)
            .AsQueryable();

        if (status.HasValue)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        query = query.OrderByDescending(x => x.CompletedAtUtc ?? x.CancelledAtUtc ?? x.StartedAtUtc);
        if (limit is > 0)
        {
            query = query.Take(limit.Value);
        }

        var instances = await query.ToListAsync(cancellationToken);

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
                CompletedAtUtc = instance.CompletedAtUtc,
                CancelledAtUtc = instance.CancelledAtUtc,
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
        var normalizedOwner = (owner ?? string.Empty).Trim().ToLowerInvariant();

        var assignments = await dbContext.WorkflowStepAssignees
            .Include(assignment => assignment.WorkflowStepInstance)
            .ThenInclude(step => step.Assignees)
            .Include(assignment => assignment.WorkflowStepInstance)
            .ThenInclude(step => step.WorkflowInstance)
            .ThenInclude(instance => instance.WorkflowDefinition)
            .Where(assignment => !assignment.CompletedAtUtc.HasValue)
            .Where(assignment => assignment.WorkflowStepInstance.Status == WorkflowStepStatus.InProgress)
            .Where(assignment => assignment.WorkflowStepInstance.WorkflowInstance.Status == WorkflowInstanceStatus.InProgress)
            .Where(assignment => includeAllOwners || assignment.Email.ToLower() == normalizedOwner)
            .OrderBy(assignment => assignment.WorkflowStepInstance.DueAtUtc)
            .ThenBy(assignment => assignment.Email)
            .ToListAsync(cancellationToken);

        return assignments.Select(assignment =>
        {
            var step = assignment.WorkflowStepInstance;
            return new TaskItemDto
            {
                StepInstanceId = step.Id,
                WorkflowInstanceId = step.WorkflowInstanceId,
                InstanceName = step.WorkflowInstance.InstanceName,
                WorkflowName = step.WorkflowInstance.WorkflowDefinition.Name,
                StepName = step.StepName,
                Owner = assignment.Email,
                DueAtUtc = step.DueAtUtc,
                IsOverdue = step.DueAtUtc < now,
                CompletedAssignees = step.Assignees.Count(item => item.CompletedAtUtc.HasValue),
                TotalAssignees = step.Assignees.Count
            };
        }).ToList();
    }
}
