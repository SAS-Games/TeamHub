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
    public async Task<IReadOnlyList<WorkflowDraftSummaryDto>> GetDraftsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDraftTablesAsync(cancellationToken);

        return await dbContext.WorkflowDraftDefinitions
            .AsNoTracking()
            .Where(x => x.PublishedAtUtc == null)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => new WorkflowDraftSummaryDto
            {
                Id = x.Id,
                WorkflowKey = x.WorkflowKey,
                WorkflowName = x.Name,
                UpdatedAtUtc = x.UpdatedAtUtc,
                StepCount = x.Steps.Count
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkflowDraftDto?> GetDraftAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureDraftTablesAsync(cancellationToken);

        var draft = await dbContext.WorkflowDraftDefinitions
            .AsNoTracking()
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == id && x.PublishedAtUtc == null, cancellationToken);

        return draft is null ? null : ToDraftDto(draft);
    }

    public async Task<Guid> SaveDraftAsync(WorkflowDraftDto draft, CancellationToken cancellationToken = default)
    {
        await EnsureDraftTablesAsync(cancellationToken);

        WorkflowDraftDefinition entity;
        if (draft.Id.HasValue)
        {
            entity = await dbContext.WorkflowDraftDefinitions
                .FirstOrDefaultAsync(x => x.Id == draft.Id.Value && x.PublishedAtUtc == null, cancellationToken)
                ?? new WorkflowDraftDefinition { Id = draft.Id.Value, CreatedAtUtc = clock.UtcNow };
        }
        else
        {
            entity = new WorkflowDraftDefinition { CreatedAtUtc = clock.UtcNow };
            dbContext.WorkflowDraftDefinitions.Add(entity);
        }

        entity.WorkflowKey = NormalizeKey(draft.WorkflowKey);
        entity.Name = draft.WorkflowName.Trim();
        entity.Description = string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description.Trim();
        entity.Enabled = draft.Enabled;
        entity.UpdatedAtUtc = clock.UtcNow;

        if (dbContext.Entry(entity).State == EntityState.Detached)
        {
            dbContext.WorkflowDraftDefinitions.Add(entity);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (dbContext.Database.IsRelational())
        {
            await dbContext.WorkflowDraftStepDefinitions
                .Where(x => x.WorkflowDraftDefinitionId == entity.Id)
                .ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            var existingSteps = await dbContext.WorkflowDraftStepDefinitions
                .Where(x => x.WorkflowDraftDefinitionId == entity.Id)
                .ToListAsync(cancellationToken);
            dbContext.WorkflowDraftStepDefinitions.RemoveRange(existingSteps);
        }

        dbContext.WorkflowDraftStepDefinitions.AddRange(CreateDraftSteps(entity.Id, draft.Steps));
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task DeleteDraftAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureDraftTablesAsync(cancellationToken);

        var draft = await dbContext.WorkflowDraftDefinitions
            .FirstOrDefaultAsync(x => x.Id == id && x.PublishedAtUtc == null, cancellationToken);
        if (draft is null)
        {
            return;
        }

        dbContext.WorkflowDraftDefinitions.Remove(draft);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ConfigurationSyncResult> PublishDraftAsync(Guid id, string actor, CancellationToken cancellationToken = default)
    {
        await EnsureDraftTablesAsync(cancellationToken);

        var result = new ConfigurationSyncResult { SyncedAtUtc = clock.UtcNow };
        var draft = await dbContext.WorkflowDraftDefinitions
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == id && x.PublishedAtUtc == null, cancellationToken);

        if (draft is null)
        {
            result.Errors.Add("Workflow draft was not found.");
            return result;
        }

        var definition = ToDefinitionDto(draft);
        var validationErrors = ValidateDefinitions([definition]);
        if (validationErrors.Count > 0)
        {
            result.Errors.AddRange(validationErrors);
            return result;
        }

        var hash = ComputeHash(definition);
        var existingHash = await dbContext.WorkflowDefinitions
            .AsNoTracking()
            .Where(x => x.WorkflowKey == definition.WorkflowKey && x.ConfigHash == hash)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingHash == Guid.Empty)
        {
            var currentVersion = await dbContext.WorkflowDefinitions
                .Where(x => x.WorkflowKey == definition.WorkflowKey)
                .MaxAsync(x => (int?)x.Version, cancellationToken) ?? 0;

            var activeDefinitions = await dbContext.WorkflowDefinitions
                .Where(x => x.WorkflowKey == definition.WorkflowKey && x.IsActive)
                .ToListAsync(cancellationToken);
            foreach (var activeDefinition in activeDefinitions)
            {
                activeDefinition.IsActive = false;
            }

            var publishedDefinition = new WorkflowDefinition
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
                publishedDefinition.Steps.Add(new WorkflowStepDefinition
                {
                    WorkflowDefinition = publishedDefinition,
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
                });
            }

            dbContext.WorkflowDefinitions.Add(publishedDefinition);
            await dbContext.SaveChangesAsync(cancellationToken);

            var stepByKey = publishedDefinition.Steps.ToDictionary(x => x.StepKey, StringComparer.OrdinalIgnoreCase);
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

            result.ImportedWorkflowSummaries.Add($"{definition.WorkflowKey}: v{publishedDefinition.Version}");
        }
        else
        {
            result.ImportedWorkflowSummaries.Add($"{definition.WorkflowKey}: unchanged");
        }

        draft.PublishedAtUtc = clock.UtcNow;
        draft.UpdatedAtUtc = clock.UtcNow;
        dbContext.AuditLogs.Add(new AuditLog
        {
            WorkflowInstanceId = Guid.Empty,
            WorkflowStepInstanceId = null,
            EventType = "ConfigurationPublished",
            Actor = actor,
            TimestampUtc = clock.UtcNow,
            DetailsJson = JsonSerializer.Serialize(new { definition.WorkflowKey, hash })
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        result.Success = true;
        return result;
    }

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

    private async Task EnsureDraftTablesAsync(CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return;
        }

        await dbContext.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS WorkflowDraftDefinitions (
                Id TEXT NOT NULL CONSTRAINT PK_WorkflowDraftDefinitions PRIMARY KEY,
                WorkflowKey TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT NULL,
                Enabled INTEGER NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                PublishedAtUtc TEXT NULL
            );
            """, cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_WorkflowDraftDefinitions_PublishedAtUtc
            ON WorkflowDraftDefinitions (PublishedAtUtc);
            """, cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS WorkflowDraftStepDefinitions (
                Id TEXT NOT NULL CONSTRAINT PK_WorkflowDraftStepDefinitions PRIMARY KEY,
                WorkflowDraftDefinitionId TEXT NOT NULL,
                StepKey TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT NULL,
                OwnerType TEXT NOT NULL,
                Owner TEXT NOT NULL,
                ExpectedDurationHours REAL NOT NULL,
                DependsOnCsv TEXT NOT NULL,
                ReminderAfterHours REAL NULL,
                ReminderRepeatHours REAL NULL,
                EscalationAfterHours REAL NULL,
                EscalationOwner TEXT NULL,
                Required INTEGER NOT NULL,
                Enabled INTEGER NOT NULL,
                SortOrder INTEGER NOT NULL,
                CONSTRAINT FK_WorkflowDraftStepDefinitions_WorkflowDraftDefinitions_WorkflowDraftDefinitionId
                    FOREIGN KEY (WorkflowDraftDefinitionId) REFERENCES WorkflowDraftDefinitions (Id) ON DELETE CASCADE
            );
            """, cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_WorkflowDraftStepDefinitions_WorkflowDraftDefinitionId_SortOrder
            ON WorkflowDraftStepDefinitions (WorkflowDraftDefinitionId, SortOrder);
            """, cancellationToken);
    }

    private static WorkflowDraftDto ToDraftDto(WorkflowDraftDefinition draft)
    {
        return new WorkflowDraftDto
        {
            Id = draft.Id,
            WorkflowKey = draft.WorkflowKey,
            WorkflowName = draft.Name,
            Description = draft.Description,
            Enabled = draft.Enabled,
            Steps = draft.Steps
                .OrderBy(x => x.SortOrder)
                .Select(x => new WorkflowDraftStepDto
                {
                    StepKey = x.StepKey,
                    StepName = x.Name,
                    Description = x.Description,
                    OwnerType = x.OwnerType,
                    Owner = x.Owner,
                    ExpectedDurationHours = x.ExpectedDurationHours,
                    DependsOnCsv = x.DependsOnCsv,
                    ReminderAfterHours = x.ReminderAfterHours,
                    ReminderRepeatHours = x.ReminderRepeatHours,
                    EscalationAfterHours = x.EscalationAfterHours,
                    EscalationOwner = x.EscalationOwner,
                    Required = x.Required,
                    Enabled = x.Enabled,
                    SortOrder = x.SortOrder
                })
                .ToList()
        };
    }

    private static WorkflowDefinitionDto ToDefinitionDto(WorkflowDraftDefinition draft)
    {
        return new WorkflowDefinitionDto
        {
            WorkflowKey = draft.WorkflowKey,
            WorkflowName = draft.Name,
            Description = draft.Description,
            Enabled = draft.Enabled,
            Steps = draft.Steps
                .OrderBy(x => x.SortOrder)
                .Select(x => new WorkflowStepDefinitionDto
                {
                    StepKey = x.StepKey,
                    StepName = x.Name,
                    Description = x.Description,
                    OwnerType = x.OwnerType,
                    Owner = x.Owner,
                    ExpectedDurationHours = x.ExpectedDurationHours,
                    DependsOn = ParseDependsOn(x.DependsOnCsv),
                    ReminderAfterHours = x.ReminderAfterHours,
                    ReminderRepeatHours = x.ReminderRepeatHours,
                    EscalationAfterHours = x.EscalationAfterHours,
                    EscalationOwner = x.EscalationOwner,
                    Required = x.Required,
                    Enabled = x.Enabled,
                    SortOrder = x.SortOrder
                })
                .ToList()
        };
    }

    private static IEnumerable<WorkflowDraftStepDefinition> CreateDraftSteps(Guid draftId, IEnumerable<WorkflowDraftStepDto> steps)
    {
        var sortOrder = 1;
        foreach (var step in steps.Where(HasStepValue))
        {
            yield return new WorkflowDraftStepDefinition
            {
                WorkflowDraftDefinitionId = draftId,
                StepKey = NormalizeKey(step.StepKey),
                Name = step.StepName.Trim(),
                Description = string.IsNullOrWhiteSpace(step.Description) ? null : step.Description.Trim(),
                OwnerType = string.IsNullOrWhiteSpace(step.OwnerType) ? "Email" : step.OwnerType.Trim(),
                Owner = step.Owner.Trim(),
                ExpectedDurationHours = step.ExpectedDurationHours,
                DependsOnCsv = string.Join(", ", ParseDependsOn(step.DependsOnCsv)),
                ReminderAfterHours = step.ReminderAfterHours,
                ReminderRepeatHours = step.ReminderRepeatHours,
                EscalationAfterHours = step.EscalationAfterHours,
                EscalationOwner = string.IsNullOrWhiteSpace(step.EscalationOwner) ? null : step.EscalationOwner.Trim(),
                Required = step.Required,
                Enabled = step.Enabled,
                SortOrder = step.SortOrder > 0 ? step.SortOrder : sortOrder
            };
            sortOrder++;
        }
    }

    private static bool HasStepValue(WorkflowDraftStepDto step)
        => !string.IsNullOrWhiteSpace(step.StepKey)
            || !string.IsNullOrWhiteSpace(step.StepName)
            || !string.IsNullOrWhiteSpace(step.Owner)
            || !string.IsNullOrWhiteSpace(step.Description)
            || !string.IsNullOrWhiteSpace(step.DependsOnCsv);

    private static List<string> ParseDependsOn(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeKey)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static string NormalizeKey(string value)
        => string.Join('_', value.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
