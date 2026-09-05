using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.Domain.Entities;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Infrastructure.Services;

public sealed class WorkflowConfigurationService(
    WorkflowDbContext dbContext,
    IWorkflowDefinitionProvider definitionProvider,
    IClock clock) : IWorkflowConfigurationService
{
    public async Task<ConfigurationSyncResult> SyncAsync(string actor, CancellationToken cancellationToken = default)
    {
        var result = new ConfigurationSyncResult { SyncedAtUtc = clock.UtcNow };
        IReadOnlyList<WorkflowDefinitionDto> definitions;

        try
        {
            definitions = await definitionProvider.LoadDefinitionsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add(ex.Message);
            return result;
        }

        var validationErrors = ValidateDefinitions(definitions);
        if (validationErrors.Count > 0)
        {
            result.Success = false;
            result.Errors.AddRange(validationErrors);
            return result;
        }

        foreach (var definition in definitions)
        {
            var hash = ComputeHash(definition);

            var existingHash = await dbContext.WorkflowDefinitions
                .AsNoTracking()
                .Where(x => x.WorkflowKey == definition.WorkflowKey && x.ConfigHash == hash)
                .Select(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (existingHash != Guid.Empty)
            {
                result.ImportedWorkflowSummaries.Add($"{definition.WorkflowKey}: unchanged");
                continue;
            }

            var currentVersion = await dbContext.WorkflowDefinitions
                .Where(x => x.WorkflowKey == definition.WorkflowKey)
                .MaxAsync(x => (int?)x.Version, cancellationToken) ?? 0;

            var newDefinition = new WorkflowDefinition
            {
                WorkflowKey = definition.WorkflowKey,
                Name = definition.WorkflowName,
                Description = definition.Description,
                Version = currentVersion + 1,
                ConfigHash = hash,
                IsActive = true,
                Enabled = definition.Enabled,
                ImportedAtUtc = clock.UtcNow
            };

            foreach (var step in definition.Steps)
            {
                var stepEntity = new WorkflowStepDefinition
                {
                    WorkflowDefinition = newDefinition,
                    StepKey = step.StepKey,
                    Name = step.StepName,
                    Description = step.Description,
                    OwnerType = step.OwnerType,
                    Owner = step.Owner,
                    ExpectedDurationHours = step.ExpectedDurationHours,
                    ReminderAfterHours = step.ReminderAfterHours,
                    ReminderRepeatHours = step.ReminderRepeatHours,
                    EscalationAfterHours = step.EscalationAfterHours,
                    EscalationOwner = step.EscalationOwner,
                    Required = step.Required,
                    Enabled = step.Enabled,
                    SortOrder = step.SortOrder
                };

                newDefinition.Steps.Add(stepEntity);
            }

            dbContext.WorkflowDefinitions.Add(newDefinition);
            await dbContext.SaveChangesAsync(cancellationToken);

            var stepByKey = newDefinition.Steps.ToDictionary(x => x.StepKey, StringComparer.OrdinalIgnoreCase);
            foreach (var step in definition.Steps)
            {
                var targetStep = stepByKey[step.StepKey];
                foreach (var dependsOn in step.DependsOn)
                {
                    dbContext.WorkflowStepDependencies.Add(new WorkflowStepDependency
                    {
                        WorkflowStepDefinitionId = targetStep.Id,
                        DependsOnStepDefinitionId = stepByKey[dependsOn].Id
                    });
                }
            }

            dbContext.AuditLogs.Add(new AuditLog
            {
                WorkflowInstanceId = Guid.Empty,
                WorkflowStepInstanceId = null,
                EventType = "ConfigurationImported",
                Actor = actor,
                TimestampUtc = clock.UtcNow,
                DetailsJson = JsonSerializer.Serialize(new { definition.WorkflowKey, Version = newDefinition.Version, hash })
            });

            result.ImportedWorkflowSummaries.Add($"{definition.WorkflowKey}: v{newDefinition.Version}");
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        result.Success = true;
        return result;
    }

    public static List<string> ValidateDefinitions(IReadOnlyList<WorkflowDefinitionDto> definitions)
    {
        var errors = new List<string>();

        var duplicateWorkflowKeys = definitions
            .GroupBy(x => x.WorkflowKey, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        foreach (var key in duplicateWorkflowKeys)
        {
            errors.Add($"Duplicate WorkflowKey: {key}");
        }

        foreach (var workflow in definitions)
        {
            var enabledSteps = workflow.Steps.Where(x => x.Enabled).ToList();
            if (enabledSteps.Count == 0)
            {
                errors.Add($"Workflow {workflow.WorkflowKey} has no enabled steps.");
            }

            var duplicateStepKeys = workflow.Steps
                .GroupBy(x => x.StepKey, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            foreach (var stepKey in duplicateStepKeys)
            {
                errors.Add($"Workflow {workflow.WorkflowKey} has duplicate StepKey: {stepKey}");
            }

            var stepKeys = workflow.Steps.Select(x => x.StepKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var step in workflow.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Owner))
                {
                    errors.Add($"Workflow {workflow.WorkflowKey}: Step {step.StepKey} owner is required.");
                }

                if (step.ExpectedDurationHours < 0)
                {
                    errors.Add($"Workflow {workflow.WorkflowKey}: Step {step.StepKey} expected duration cannot be negative.");
                }

                if (step.EscalationAfterHours.HasValue && string.IsNullOrWhiteSpace(step.EscalationOwner))
                {
                    errors.Add($"Workflow {workflow.WorkflowKey}: Step {step.StepKey} escalation owner is required when escalation is configured.");
                }

                foreach (var dependency in step.DependsOn)
                {
                    if (!stepKeys.Contains(dependency))
                    {
                        errors.Add($"Workflow {workflow.WorkflowKey}: Step {step.StepKey} references unknown dependency {dependency}.");
                    }
                }
            }

            if (HasCycle(workflow))
            {
                errors.Add($"Workflow {workflow.WorkflowKey} has circular dependencies.");
            }
        }

        return errors;
    }

    private static bool HasCycle(WorkflowDefinitionDto workflow)
    {
        var graph = workflow.Steps.ToDictionary(x => x.StepKey, x => x.DependsOn, StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Dfs(string node)
        {
            if (visiting.Contains(node))
            {
                return true;
            }

            if (visited.Contains(node))
            {
                return false;
            }

            visiting.Add(node);
            foreach (var dependency in graph[node])
            {
                if (graph.ContainsKey(dependency) && Dfs(dependency))
                {
                    return true;
                }
            }

            visiting.Remove(node);
            visited.Add(node);
            return false;
        }

        foreach (var step in workflow.Steps)
        {
            if (Dfs(step.StepKey))
            {
                return true;
            }
        }

        return false;
    }

    private static string ComputeHash(WorkflowDefinitionDto definition)
    {
        var normalized = new
        {
            definition.WorkflowKey,
            definition.WorkflowName,
            definition.Description,
            definition.Enabled,
            Steps = definition.Steps
                .OrderBy(x => x.StepKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    x.StepKey,
                    x.StepName,
                    x.Description,
                    x.OwnerType,
                    x.Owner,
                    x.ExpectedDurationHours,
                    DependsOn = x.DependsOn.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
                    x.ReminderAfterHours,
                    x.ReminderRepeatHours,
                    x.EscalationAfterHours,
                    x.EscalationOwner,
                    x.Required,
                    x.Enabled,
                    x.SortOrder
                })
        };

        var json = JsonSerializer.Serialize(normalized);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hashBytes);
    }
}
