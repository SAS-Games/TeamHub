using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.Domain.Entities;
using TeamHub.Domain.Enums;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Infrastructure.Services;

public sealed class WorkflowEngineService(
    WorkflowDbContext dbContext,
    IClock clock,
    INotificationService notificationService) : IWorkflowEngine
{
    public async Task<Guid> StartWorkflowAsync(StartWorkflowRequest request, CancellationToken cancellationToken = default)
    {
        var definition = await dbContext.WorkflowDefinitions
            .Include(x => x.Steps)
            .ThenInclude(x => x.Dependencies)
            .Where(x => x.WorkflowKey == request.WorkflowKey && x.IsActive && x.Enabled)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (definition is null)
        {
            throw new InvalidOperationException($"No active workflow definition found for key '{request.WorkflowKey}'.");
        }

        var hasDuplicateActiveInstance = await dbContext.WorkflowInstances
            .AnyAsync(x => x.WorkflowDefinitionId == definition.Id
                && x.Status == WorkflowInstanceStatus.InProgress
                && x.InstanceName == request.InstanceName, cancellationToken);

        if (hasDuplicateActiveInstance)
        {
            throw new InvalidOperationException($"An active instance for workflow '{request.WorkflowKey}' and person '{request.InstanceName}' already exists.");
        }

        var now = clock.UtcNow;
        var instance = new WorkflowInstance
        {
            WorkflowDefinitionId = definition.Id,
            InstanceName = request.InstanceName,
            Description = request.Description,
            MetadataJson = request.MetadataJson,
            Status = WorkflowInstanceStatus.InProgress,
            StartedAtUtc = now,
            StartedBy = request.StartedBy
        };

        var enabledSteps = definition.Steps.Where(x => x.Enabled).ToList();
        var stepInstances = enabledSteps.Select(step => new WorkflowStepInstance
        {
            WorkflowInstance = instance,
            StepDefinitionId = step.Id,
            StepKey = step.StepKey,
            StepName = step.Name,
            Description = step.Description,
            OwnerType = step.OwnerType,
            Owner = step.Owner,
            Status = WorkflowStepStatus.Pending,
            ExpectedDurationHours = step.ExpectedDurationHours,
            Required = step.Required,
            SortOrder = step.SortOrder,
            ReminderAfterHours = step.ReminderAfterHours,
            ReminderRepeatHours = step.ReminderRepeatHours,
            EscalationAfterHours = step.EscalationAfterHours,
            EscalationOwner = step.EscalationOwner
        }).ToList();

        var stepByDefinitionId = stepInstances
            .Where(x => x.StepDefinitionId.HasValue)
            .ToDictionary(x => x.StepDefinitionId!.Value);

        foreach (var stepDefinition in enabledSteps)
        {
            foreach (var dependency in stepDefinition.Dependencies)
            {
                dbContext.WorkflowStepInstanceDependencies.Add(new WorkflowStepInstanceDependency
                {
                    WorkflowStepInstance = stepByDefinitionId[stepDefinition.Id],
                    DependsOnWorkflowStepInstanceId = stepByDefinitionId[dependency.DependsOnStepDefinitionId].Id
                });
            }
        }

        dbContext.WorkflowInstances.Add(instance);
        dbContext.WorkflowStepInstances.AddRange(stepInstances);
        dbContext.AuditLogs.Add(new AuditLog
        {
            WorkflowInstanceId = instance.Id,
            EventType = "WorkflowStarted",
            Actor = request.StartedBy,
            TimestampUtc = now,
            DetailsJson = request.Description
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        var rootSteps = stepInstances
            .Where(step => !dbContext.WorkflowStepInstanceDependencies
                .Any(dep => dep.WorkflowStepInstanceId == step.Id))
            .ToList();

        await ActivateStepsAsync(rootSteps, "System", cancellationToken);

        return instance.Id;
    }

    public async Task<bool> CompleteStepAsync(StepCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;

        var step = await dbContext.WorkflowStepInstances
            .Include(x => x.WorkflowInstance)
            .ThenInclude(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == request.StepInstanceId, cancellationToken);

        if (step is null)
        {
            return false;
        }

        if (step.Status is WorkflowStepStatus.Completed or WorkflowStepStatus.Skipped or WorkflowStepStatus.Cancelled)
        {
            return true;
        }

        if (!request.IsAdminOverride && !string.Equals(step.Owner, request.Actor, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Only assigned owner or admin can complete this task.");
        }

        if (step.Status != WorkflowStepStatus.InProgress)
        {
            throw new InvalidOperationException("Only in-progress steps can be completed.");
        }

        step.Status = WorkflowStepStatus.Completed;
        step.CompletedAtUtc = now;
        step.CompletedBy = request.Actor;

        dbContext.AuditLogs.Add(new AuditLog
        {
            WorkflowInstanceId = step.WorkflowInstanceId,
            WorkflowStepInstanceId = step.Id,
            EventType = request.IsAdminOverride ? "AdminOverride" : "StepCompleted",
            Actor = request.Actor,
            TimestampUtc = now,
            DetailsJson = JsonSerializer.Serialize(new
            {
                StepKey = step.StepKey,
                Comment = request.Comment
            })
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        var pendingSteps = await dbContext.WorkflowStepInstances
            .Where(x => x.WorkflowInstanceId == step.WorkflowInstanceId && x.Status == WorkflowStepStatus.Pending)
            .ToListAsync(cancellationToken);

        var dependencies = await dbContext.WorkflowStepInstanceDependencies
            .Where(x => pendingSteps.Select(p => p.Id).Contains(x.WorkflowStepInstanceId))
            .ToListAsync(cancellationToken);

        var completedStepIds = await dbContext.WorkflowStepInstances
            .Where(x => x.WorkflowInstanceId == step.WorkflowInstanceId && (x.Status == WorkflowStepStatus.Completed || x.Status == WorkflowStepStatus.Skipped))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var completedSet = completedStepIds.ToHashSet();
        var activatable = pendingSteps.Where(p => dependencies
            .Where(d => d.WorkflowStepInstanceId == p.Id)
            .All(d => completedSet.Contains(d.DependsOnWorkflowStepInstanceId))).ToList();

        await ActivateStepsAsync(activatable, request.Actor, cancellationToken);

        var remainingRequired = await dbContext.WorkflowStepInstances
            .AnyAsync(x => x.WorkflowInstanceId == step.WorkflowInstanceId
                && x.Required
                && x.Status != WorkflowStepStatus.Completed
                && x.Status != WorkflowStepStatus.Skipped, cancellationToken);

        if (!remainingRequired)
        {
            var instance = step.WorkflowInstance;
            instance.Status = WorkflowInstanceStatus.Completed;
            instance.CompletedAtUtc = now;

            dbContext.AuditLogs.Add(new AuditLog
            {
                WorkflowInstanceId = instance.Id,
                EventType = "WorkflowCompleted",
                Actor = request.Actor,
                TimestampUtc = now,
                DetailsJson = null
            });

            await dbContext.SaveChangesAsync(cancellationToken);

            await notificationService.SendAsync(new NotificationMessage
            {
                WorkflowInstanceId = instance.Id,
                Type = NotificationType.WorkflowCompleted,
                Recipient = request.Actor,
                Subject = $"Workflow Completed - {instance.InstanceName}",
                Body = $"Workflow instance '{instance.InstanceName}' has been completed."
            }, cancellationToken);
        }

        return true;
    }

    public async Task<int> RunReminderCycleAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var activeSteps = await dbContext.WorkflowStepInstances
            .Include(x => x.WorkflowInstance)
            .Where(x => x.WorkflowInstance.Status == WorkflowInstanceStatus.InProgress && x.Status == WorkflowStepStatus.InProgress)
            .ToListAsync(cancellationToken);

        var notificationsSent = 0;

        foreach (var step in activeSteps)
        {
            if (step.StartedAtUtc is null || step.DueAtUtc is null)
            {
                continue;
            }

            var reminderThreshold = step.StartedAtUtc.Value.AddHours(step.ReminderAfterHours ?? 0);
            if (now < reminderThreshold)
            {
                continue;
            }

            var reminderType = step.DueAtUtc.Value < now ? NotificationType.Overdue : NotificationType.Reminder;

            var shouldSendReminder = false;
            if (step.LastReminderAtUtc is null)
            {
                shouldSendReminder = true;
            }
            else if (step.ReminderRepeatHours.HasValue && step.ReminderRepeatHours.Value > 0)
            {
                shouldSendReminder = (now - step.LastReminderAtUtc.Value).TotalHours >= step.ReminderRepeatHours.Value;
            }

            if (shouldSendReminder)
            {
                await notificationService.SendAsync(new NotificationMessage
                {
                    WorkflowInstanceId = step.WorkflowInstanceId,
                    WorkflowStepInstanceId = step.Id,
                    Type = reminderType,
                    Recipient = step.Owner,
                    Subject = $"Reminder - {step.StepName}",
                    Body = $"Task '{step.StepName}' for instance '{step.WorkflowInstance.InstanceName}' is overdue."
                }, cancellationToken);
                step.LastReminderAtUtc = now;
                notificationsSent++;
            }

            if (step.DueAtUtc.Value < now
                && step.EscalationAfterHours.HasValue
                && !string.IsNullOrWhiteSpace(step.EscalationOwner)
                && (now - step.DueAtUtc.Value).TotalHours >= step.EscalationAfterHours.Value
                && step.LastEscalationAtUtc is null)
            {
                await notificationService.SendAsync(new NotificationMessage
                {
                    WorkflowInstanceId = step.WorkflowInstanceId,
                    WorkflowStepInstanceId = step.Id,
                    Type = NotificationType.Escalation,
                    Recipient = step.EscalationOwner!,
                    Subject = $"Escalation - {step.StepName}",
                    Body = $"Task '{step.StepName}' for instance '{step.WorkflowInstance.InstanceName}' is overdue and requires escalation."
                }, cancellationToken);

                step.LastEscalationAtUtc = now;
                notificationsSent++;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return notificationsSent;
    }

    public async Task CancelWorkflowAsync(Guid workflowInstanceId, string actor, CancellationToken cancellationToken = default)
    {
        var workflow = await dbContext.WorkflowInstances
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == workflowInstanceId, cancellationToken);

        if (workflow is null || workflow.Status == WorkflowInstanceStatus.Cancelled)
        {
            return;
        }

        workflow.Status = WorkflowInstanceStatus.Cancelled;
        workflow.CancelledAtUtc = clock.UtcNow;

        foreach (var step in workflow.Steps.Where(s => s.Status is WorkflowStepStatus.Pending or WorkflowStepStatus.InProgress))
        {
            step.Status = WorkflowStepStatus.Cancelled;
        }

        dbContext.AuditLogs.Add(new AuditLog
        {
            WorkflowInstanceId = workflow.Id,
            EventType = "WorkflowCancelled",
            Actor = actor,
            TimestampUtc = clock.UtcNow
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ActivateStepsAsync(IReadOnlyList<WorkflowStepInstance> steps, string actor, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        foreach (var step in steps.Where(s => s.Status == WorkflowStepStatus.Pending))
        {
            step.Status = WorkflowStepStatus.InProgress;
            step.StartedAtUtc = now;
            step.DueAtUtc = now.AddHours(step.ExpectedDurationHours);

            dbContext.AuditLogs.Add(new AuditLog
            {
                WorkflowInstanceId = step.WorkflowInstanceId,
                WorkflowStepInstanceId = step.Id,
                EventType = "StepActivated",
                Actor = actor,
                TimestampUtc = now,
                DetailsJson = step.StepKey
            });

            await notificationService.SendAsync(new NotificationMessage
            {
                WorkflowInstanceId = step.WorkflowInstanceId,
                WorkflowStepInstanceId = step.Id,
                Type = NotificationType.Assignment,
                Recipient = step.Owner,
                Subject = $"Action Required - {step.StepName}",
                Body = $"Task '{step.StepName}' is now active and due at {step.DueAtUtc:yyyy-MM-dd HH:mm} UTC."
            }, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
