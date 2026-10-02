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

        var enabledSteps = definition.Steps.Where(step => step.Enabled).ToList();
        var stepWithoutAssignees = enabledSteps.FirstOrDefault(step =>
            WorkflowAssigneeEmails.Parse(step.Owner).Count == 0);
        if (stepWithoutAssignees is not null)
        {
            throw new InvalidOperationException($"Step '{stepWithoutAssignees.Name}' has no valid assignee email.");
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

        var stepInstances = enabledSteps.Select(step => new WorkflowStepInstance
        {
            WorkflowInstance = instance,
            StepDefinitionId = step.Id,
            StepKey = step.StepKey,
            StepName = step.Name,
            Description = step.Description,
            OwnerType = step.OwnerType,
            Owner = WorkflowAssigneeEmails.Format(WorkflowAssigneeEmails.Parse(step.Owner)),
            Status = WorkflowStepStatus.Pending,
            ExpectedDurationHours = step.ExpectedDurationHours,
            Required = step.Required,
            SortOrder = step.SortOrder,
            ReminderAfterHours = step.ReminderAfterHours,
            ReminderRepeatHours = step.ReminderRepeatHours,
            EscalationAfterHours = step.EscalationAfterHours,
            EscalationOwner = step.EscalationOwner,
            Assignees = WorkflowAssigneeEmails.Parse(step.Owner)
                .Select(email => new WorkflowStepAssignee { Email = email })
                .ToList()
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
            .Include(item => item.Assignees)
            .Include(item => item.WorkflowInstance)
            .ThenInclude(instance => instance.Steps)
            .FirstOrDefaultAsync(item => item.Id == request.StepInstanceId, cancellationToken);

        if (step is null)
        {
            return false;
        }

        if (step.Status is WorkflowStepStatus.Completed or WorkflowStepStatus.Skipped or WorkflowStepStatus.Cancelled)
        {
            return true;
        }

        if (step.Status != WorkflowStepStatus.InProgress)
        {
            throw new InvalidOperationException("Only in-progress steps can be completed.");
        }

        if (step.Assignees.Count == 0)
        {
            foreach (var email in WorkflowAssigneeEmails.Parse(step.Owner))
            {
                step.Assignees.Add(new WorkflowStepAssignee
                {
                    WorkflowStepInstance = step,
                    Email = email
                });
            }
        }

        var requestedEmail = request.IsAdminOverride && !string.IsNullOrWhiteSpace(request.AssigneeEmail)
            ? request.AssigneeEmail
            : request.Actor;
        var assignee = step.Assignees.FirstOrDefault(item =>
            string.Equals(item.Email, requestedEmail, StringComparison.OrdinalIgnoreCase));
        if (assignee is null && request.IsAdminOverride && step.Assignees.Count == 1)
        {
            assignee = step.Assignees.Single();
        }
        if (assignee is null)
        {
            throw new UnauthorizedAccessException("Only an assigned email or an administrator can complete this task.");
        }
        if (assignee.CompletedAtUtc.HasValue)
        {
            return true;
        }

        assignee.CompletedAtUtc = now;
        assignee.CompletedBy = request.Actor;
        assignee.CompletionComment = request.Comment;

        dbContext.AuditLogs.Add(new AuditLog
        {
            WorkflowInstanceId = step.WorkflowInstanceId,
            WorkflowStepInstanceId = step.Id,
            EventType = request.IsAdminOverride ? "AssigneeAdminOverride" : "AssigneeCompleted",
            Actor = request.Actor,
            TimestampUtc = now,
            DetailsJson = JsonSerializer.Serialize(new
            {
                step.StepKey,
                AssigneeEmail = assignee.Email,
                Comment = request.Comment
            })
        });

        if (step.Assignees.Any(item => !item.CompletedAtUtc.HasValue))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        step.Status = WorkflowStepStatus.Completed;
        step.CompletedAtUtc = now;
        step.CompletedBy = request.Actor;

        dbContext.AuditLogs.Add(new AuditLog
        {
            WorkflowInstanceId = step.WorkflowInstanceId,
            WorkflowStepInstanceId = step.Id,
            EventType = "StepCompleted",
            Actor = request.Actor,
            TimestampUtc = now,
            DetailsJson = JsonSerializer.Serialize(new
            {
                step.StepKey,
                CompletedAssignees = step.Assignees.Select(item => item.Email).ToArray(),
                Comment = request.Comment
            })
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        var remainingRequired = await dbContext.WorkflowStepInstances
            .AnyAsync(item => item.WorkflowInstanceId == step.WorkflowInstanceId
                && item.Required
                && item.Status != WorkflowStepStatus.Completed
                && item.Status != WorkflowStepStatus.Skipped, cancellationToken);

        if (!remainingRequired)
        {
            var remainingOptionalSteps = await dbContext.WorkflowStepInstances
                .Where(item => item.WorkflowInstanceId == step.WorkflowInstanceId
                    && !item.Required
                    && (item.Status == WorkflowStepStatus.Pending || item.Status == WorkflowStepStatus.InProgress))
                .ToListAsync(cancellationToken);

            foreach (var optionalStep in remainingOptionalSteps)
            {
                optionalStep.Status = WorkflowStepStatus.Skipped;
                optionalStep.CompletedAtUtc = now;
                optionalStep.CompletedBy = request.Actor;
                dbContext.AuditLogs.Add(new AuditLog
                {
                    WorkflowInstanceId = optionalStep.WorkflowInstanceId,
                    WorkflowStepInstanceId = optionalStep.Id,
                    EventType = "StepSkipped",
                    Actor = request.Actor,
                    TimestampUtc = now,
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        optionalStep.StepKey,
                        Reason = "Automatically skipped when all required work completed."
                    })
                });
            }

            var instance = step.WorkflowInstance;
            instance.Status = WorkflowInstanceStatus.Completed;
            instance.CompletedAtUtc = now;

            dbContext.AuditLogs.Add(new AuditLog
            {
                WorkflowInstanceId = instance.Id,
                EventType = "WorkflowCompleted",
                Actor = request.Actor,
                TimestampUtc = now,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    AutoSkippedOptionalSteps = remainingOptionalSteps.Count
                })
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

            return true;
        }

        var pendingSteps = await dbContext.WorkflowStepInstances
            .Where(item => item.WorkflowInstanceId == step.WorkflowInstanceId && item.Status == WorkflowStepStatus.Pending)
            .ToListAsync(cancellationToken);

        var dependencies = await dbContext.WorkflowStepInstanceDependencies
            .Where(item => pendingSteps.Select(pending => pending.Id).Contains(item.WorkflowStepInstanceId))
            .ToListAsync(cancellationToken);

        var completedStepIds = await dbContext.WorkflowStepInstances
            .Where(item => item.WorkflowInstanceId == step.WorkflowInstanceId
                && (item.Status == WorkflowStepStatus.Completed || item.Status == WorkflowStepStatus.Skipped))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        var completedSet = completedStepIds.ToHashSet();
        var activatable = pendingSteps.Where(pending => dependencies
            .Where(dependency => dependency.WorkflowStepInstanceId == pending.Id)
            .All(dependency => completedSet.Contains(dependency.DependsOnWorkflowStepInstanceId)))
            .ToList();

        await ActivateStepsAsync(activatable, request.Actor, cancellationToken);
        return true;
    }

    public async Task<int> RunReminderCycleAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var activeSteps = await dbContext.WorkflowStepInstances
            .Include(step => step.WorkflowInstance)
            .Include(step => step.Assignees)
            .Where(step => step.WorkflowInstance.Status == WorkflowInstanceStatus.InProgress
                && step.Status == WorkflowStepStatus.InProgress)
            .ToListAsync(cancellationToken);

        var notificationsSent = 0;

        foreach (var step in activeSteps)
        {
            if (step.StartedAtUtc is null || step.DueAtUtc is null)
            {
                continue;
            }

            var outstandingAssignees = step.Assignees
                .Where(assignee => !assignee.CompletedAtUtc.HasValue)
                .ToList();

            if (step.ReminderAfterHours.HasValue)
            {
                var reminderThreshold = step.StartedAtUtc.Value.AddHours(step.ReminderAfterHours.Value);
                foreach (var assignee in outstandingAssignees)
                {
                    var shouldSendReminder = now >= reminderThreshold
                        && (assignee.LastReminderAtUtc is null
                            || step.ReminderRepeatHours.HasValue
                                && step.ReminderRepeatHours.Value > 0
                                && (now - assignee.LastReminderAtUtc.Value).TotalHours >= step.ReminderRepeatHours.Value);

                    if (!shouldSendReminder)
                    {
                        continue;
                    }

                    var overdue = step.DueAtUtc.Value < now;
                    await notificationService.SendAsync(new NotificationMessage
                    {
                        WorkflowInstanceId = step.WorkflowInstanceId,
                        WorkflowStepInstanceId = step.Id,
                        Type = overdue ? NotificationType.Overdue : NotificationType.Reminder,
                        Recipient = assignee.Email,
                        Subject = $"Reminder - {step.StepName}",
                        Body = overdue
                            ? $"Task '{step.StepName}' for instance '{step.WorkflowInstance.InstanceName}' is overdue."
                            : $"Task '{step.StepName}' for instance '{step.WorkflowInstance.InstanceName}' is due at {step.DueAtUtc:yyyy-MM-dd HH:mm} UTC."
                    }, cancellationToken);
                    assignee.LastReminderAtUtc = now;
                    notificationsSent++;
                }
            }

            if (step.DueAtUtc.Value < now
                && step.EscalationAfterHours.HasValue
                && !string.IsNullOrWhiteSpace(step.EscalationOwner)
                && (now - step.DueAtUtc.Value).TotalHours >= step.EscalationAfterHours.Value
                && step.LastEscalationAtUtc is null
                && outstandingAssignees.Count > 0)
            {
                await notificationService.SendAsync(new NotificationMessage
                {
                    WorkflowInstanceId = step.WorkflowInstanceId,
                    WorkflowStepInstanceId = step.Id,
                    Type = NotificationType.Escalation,
                    Recipient = step.EscalationOwner!,
                    Subject = $"Escalation - {step.StepName}",
                    Body = $"Task '{step.StepName}' for instance '{step.WorkflowInstance.InstanceName}' is overdue for: {string.Join(", ", outstandingAssignees.Select(item => item.Email))}."
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

        foreach (var step in steps.Where(item => item.Status == WorkflowStepStatus.Pending))
        {
            step.Status = WorkflowStepStatus.InProgress;
            step.StartedAtUtc = now;
            step.DueAtUtc = now.AddHours(step.ExpectedDurationHours);

            if (step.Assignees.Count == 0)
            {
                var storedAssignees = await dbContext.WorkflowStepAssignees
                    .Where(item => item.WorkflowStepInstanceId == step.Id)
                    .ToListAsync(cancellationToken);
                foreach (var storedAssignee in storedAssignees)
                {
                    step.Assignees.Add(storedAssignee);
                }
            }
            if (step.Assignees.Count == 0)
            {
                foreach (var email in WorkflowAssigneeEmails.Parse(step.Owner))
                {
                    step.Assignees.Add(new WorkflowStepAssignee
                    {
                        WorkflowStepInstance = step,
                        Email = email
                    });
                }
            }

            dbContext.AuditLogs.Add(new AuditLog
            {
                WorkflowInstanceId = step.WorkflowInstanceId,
                WorkflowStepInstanceId = step.Id,
                EventType = "StepActivated",
                Actor = actor,
                TimestampUtc = now,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    step.StepKey,
                    Assignees = step.Assignees.Select(item => item.Email).ToArray()
                })
            });

            foreach (var assignee in step.Assignees.Where(item => !item.CompletedAtUtc.HasValue))
            {
                await notificationService.SendAsync(new NotificationMessage
                {
                    WorkflowInstanceId = step.WorkflowInstanceId,
                    WorkflowStepInstanceId = step.Id,
                    Type = NotificationType.Assignment,
                    Recipient = assignee.Email,
                    Subject = $"Action Required - {step.StepName}",
                    Body = $"Task '{step.StepName}' is now active and due at {step.DueAtUtc:yyyy-MM-dd HH:mm} UTC. Every assignee must complete their part."
                }, cancellationToken);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
