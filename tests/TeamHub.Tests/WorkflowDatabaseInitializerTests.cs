using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamHub.Domain.Entities;
using TeamHub.Domain.Enums;
using TeamHub.Infrastructure.Persistence;

namespace TeamHub.Tests;

public sealed class WorkflowDatabaseInitializerTests
{
    [Fact]
    public async Task InitializeAsync_BackfillsLegacyStepAssigneesAndIsIdempotent()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var context = new WorkflowDbContext(options);
        await context.Database.OpenConnectionAsync();

        var initializer = new WorkflowDatabaseInitializer(context);
        await initializer.InitializeAsync();

        var workflow = new WorkflowDefinition
        {
            WorkflowKey = "LEGACY_WORKFLOW",
            Name = "Legacy workflow",
            Version = 1,
            ConfigHash = "legacy-hash",
            IsActive = true,
            Enabled = true,
            ImportedAtUtc = DateTime.UtcNow
        };
        var instance = new WorkflowInstance
        {
            WorkflowDefinition = workflow,
            InstanceName = "Legacy instance",
            StartedAtUtc = DateTime.UtcNow,
            Status = WorkflowInstanceStatus.InProgress,
            StartedBy = "admin@example.com"
        };
        var step = new WorkflowStepInstance
        {
            WorkflowInstance = instance,
            StepKey = "LEGACY_STEP",
            StepName = "Legacy step",
            Owner = "first@example.com; second@example.com",
            Status = WorkflowStepStatus.InProgress,
            ExpectedDurationHours = 1,
            Required = true,
            SortOrder = 1
        };
        context.WorkflowStepInstances.Add(step);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        var assignees = await context.WorkflowStepAssignees
            .Where(assignee => assignee.WorkflowStepInstanceId == step.Id)
            .OrderBy(assignee => assignee.Email)
            .Select(assignee => assignee.Email)
            .ToListAsync();
        assignees.Should().Equal("first@example.com", "second@example.com");
    }
}
