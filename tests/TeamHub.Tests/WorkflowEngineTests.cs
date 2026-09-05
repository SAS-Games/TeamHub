using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.Domain.Entities;
using TeamHub.Domain.Enums;
using TeamHub.Infrastructure.Persistence;
using TeamHub.Infrastructure.Services;

namespace TeamHub.Tests;

public class WorkflowEngineTests
{
    [Fact]
    public async Task RootStepsActivate_WhenWorkflowStarts()
    {
        await using var context = CreateDbContext();
        var clock = new TestClock(new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc));
        var notification = new TestNotificationService();
        var engine = new WorkflowEngineService(context, clock, notification);

        var definition = SeedSimpleWorkflow(context);

        var instanceId = await engine.StartWorkflowAsync(new StartWorkflowRequest
        {
            WorkflowKey = definition.WorkflowKey,
            InstanceName = "Amit Sharma",
            StartedBy = "admin@local"
        });

        var steps = await context.WorkflowStepInstances.Where(x => x.WorkflowInstanceId == instanceId).ToListAsync();
        steps.Single(x => x.StepKey == "EMPLOYEE_ID").Status.Should().Be(WorkflowStepStatus.InProgress);
        steps.Single(x => x.StepKey == "LAPTOP").Status.Should().Be(WorkflowStepStatus.Pending);
        steps.Single(x => x.StepKey == "EMAIL").Status.Should().Be(WorkflowStepStatus.Pending);
    }

    [Fact]
    public async Task DependentStepActivates_OnlyAfterAllDependenciesComplete()
    {
        await using var context = CreateDbContext();
        var clock = new TestClock(new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc));
        var notification = new TestNotificationService();
        var engine = new WorkflowEngineService(context, clock, notification);
        var definition = SeedSimpleWorkflow(context);

        var instanceId = await engine.StartWorkflowAsync(new StartWorkflowRequest
        {
            WorkflowKey = definition.WorkflowKey,
            InstanceName = "Priya Singh",
            StartedBy = "admin@local"
        });

        var employeeStep = await context.WorkflowStepInstances.FirstAsync(x => x.WorkflowInstanceId == instanceId && x.StepKey == "EMPLOYEE_ID");
        await engine.CompleteStepAsync(new StepCompletionRequest { StepInstanceId = employeeStep.Id, Actor = "hr@company.com" });

        var laptop = await context.WorkflowStepInstances.FirstAsync(x => x.WorkflowInstanceId == instanceId && x.StepKey == "LAPTOP");
        var email = await context.WorkflowStepInstances.FirstAsync(x => x.WorkflowInstanceId == instanceId && x.StepKey == "EMAIL");
        var access = await context.WorkflowStepInstances.FirstAsync(x => x.WorkflowInstanceId == instanceId && x.StepKey == "ACCESS");

        laptop.Status.Should().Be(WorkflowStepStatus.InProgress);
        email.Status.Should().Be(WorkflowStepStatus.InProgress);
        access.Status.Should().Be(WorkflowStepStatus.Pending);

        await engine.CompleteStepAsync(new StepCompletionRequest { StepInstanceId = laptop.Id, Actor = "it@company.com" });
        access = await context.WorkflowStepInstances.FirstAsync(x => x.WorkflowInstanceId == instanceId && x.StepKey == "ACCESS");
        access.Status.Should().Be(WorkflowStepStatus.Pending);

        await engine.CompleteStepAsync(new StepCompletionRequest { StepInstanceId = email.Id, Actor = "it@company.com" });
        access = await context.WorkflowStepInstances.FirstAsync(x => x.WorkflowInstanceId == instanceId && x.StepKey == "ACCESS");
        access.Status.Should().Be(WorkflowStepStatus.InProgress);
    }

    [Fact]
    public async Task StartingSameWorkflowTwice_Throws_WhenActiveInstanceExists()
    {
        await using var context = CreateDbContext();
        var clock = new TestClock(new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc));
        var notification = new TestNotificationService();
        var engine = new WorkflowEngineService(context, clock, notification);
        var definition = SeedSimpleWorkflow(context);

        await engine.StartWorkflowAsync(new StartWorkflowRequest
        {
            WorkflowKey = definition.WorkflowKey,
            InstanceName = "Amit",
            StartedBy = "admin"
        });

        var act = () => engine.StartWorkflowAsync(new StartWorkflowRequest
        {
            WorkflowKey = definition.WorkflowKey,
            InstanceName = "Amit",
            StartedBy = "another-admin"
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*active instance*");
    }

    [Fact]
    public async Task MyTasks_DoesNotThrow_ForNullOwnerValues()
    {
        await using var context = CreateDbContext();
        var clock = new TestClock(new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc));
        var service = new WorkflowReadService(context, clock);

        var workflow = new WorkflowDefinition
        {
            WorkflowKey = "WF",
            Name = "Workflow",
            Version = 1,
            ConfigHash = "HASH",
            IsActive = true,
            Enabled = true,
            ImportedAtUtc = clock.UtcNow
        };

        var instance = new WorkflowInstance
        {
            WorkflowDefinition = workflow,
            InstanceName = "Sample",
            StartedAtUtc = clock.UtcNow,
            Status = WorkflowInstanceStatus.InProgress,
            StartedBy = "admin"
        };

        context.WorkflowDefinitions.Add(workflow);
        context.WorkflowInstances.Add(instance);
        context.WorkflowStepInstances.Add(new WorkflowStepInstance
        {
            WorkflowInstance = instance,
            StepKey = "STEP_1",
            StepName = "Task 1",
            Owner = string.Empty,
            Status = WorkflowStepStatus.InProgress,
            ExpectedDurationHours = 1,
            Required = true,
            SortOrder = 1,
            DueAtUtc = clock.UtcNow.AddHours(2)
        });

        await context.SaveChangesAsync();

        var tasks = await service.GetMyTasksAsync("hr@company.com");
        tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task MyTasks_MatchesOwnerCaseInsensitively()
    {
        await using var context = CreateDbContext();
        var clock = new TestClock(new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc));
        var service = new WorkflowReadService(context, clock);
        var workflow = new WorkflowDefinition
        {
            WorkflowKey = "WF",
            Name = "Workflow",
            Version = 1,
            ConfigHash = "HASH",
            IsActive = true,
            Enabled = true,
            ImportedAtUtc = clock.UtcNow
        };
        var instance = new WorkflowInstance
        {
            WorkflowDefinition = workflow,
            InstanceName = "Sample",
            StartedAtUtc = clock.UtcNow,
            Status = WorkflowInstanceStatus.InProgress,
            StartedBy = "admin"
        };

        context.WorkflowDefinitions.Add(workflow);
        context.WorkflowInstances.Add(instance);
        context.WorkflowStepInstances.Add(new WorkflowStepInstance
        {
            WorkflowInstance = instance,
            StepKey = "STEP_1",
            StepName = "Task 1",
            Owner = "HR@COMPANY.COM",
            Status = WorkflowStepStatus.InProgress,
            ExpectedDurationHours = 1,
            Required = true,
            SortOrder = 1,
            DueAtUtc = clock.UtcNow.AddHours(2)
        });
        await context.SaveChangesAsync();

        var tasks = await service.GetMyTasksAsync("hr@company.com");

        tasks.Should().ContainSingle();
    }

    [Fact]
    public async Task CompletingOneInstance_DoesNotAffectAnother()
    {
        await using var context = CreateDbContext();
        var clock = new TestClock(new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc));
        var notification = new TestNotificationService();
        var engine = new WorkflowEngineService(context, clock, notification);
        var definitionA = SeedSimpleWorkflow(context, "NEW_HIRE_ONBOARDING");
        var definitionB = SeedSimpleWorkflow(context, "IT_SETUP");

        var instanceA = await engine.StartWorkflowAsync(new StartWorkflowRequest { WorkflowKey = definitionA.WorkflowKey, InstanceName = "Amit", StartedBy = "admin" });
        var instanceB = await engine.StartWorkflowAsync(new StartWorkflowRequest { WorkflowKey = definitionB.WorkflowKey, InstanceName = "Priya", StartedBy = "admin" });

        var employeeA = await context.WorkflowStepInstances.FirstAsync(x => x.WorkflowInstanceId == instanceA && x.StepKey == "EMPLOYEE_ID");
        await engine.CompleteStepAsync(new StepCompletionRequest { StepInstanceId = employeeA.Id, Actor = "hr@company.com" });

        var employeeB = await context.WorkflowStepInstances.FirstAsync(x => x.WorkflowInstanceId == instanceB && x.StepKey == "EMPLOYEE_ID");
        employeeB.Status.Should().Be(WorkflowStepStatus.InProgress);

        var laptopB = await context.WorkflowStepInstances.FirstAsync(x => x.WorkflowInstanceId == instanceB && x.StepKey == "LAPTOP");
        laptopB.Status.Should().Be(WorkflowStepStatus.Pending);
    }

    [Fact]
    public async Task CompletionIsIdempotent()
    {
        await using var context = CreateDbContext();
        var clock = new TestClock(new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc));
        var notification = new TestNotificationService();
        var engine = new WorkflowEngineService(context, clock, notification);
        var definition = SeedSimpleWorkflow(context);

        var instanceId = await engine.StartWorkflowAsync(new StartWorkflowRequest { WorkflowKey = definition.WorkflowKey, InstanceName = "John", StartedBy = "admin" });
        var employee = await context.WorkflowStepInstances.FirstAsync(x => x.WorkflowInstanceId == instanceId && x.StepKey == "EMPLOYEE_ID");

        var first = await engine.CompleteStepAsync(new StepCompletionRequest
        {
            StepInstanceId = employee.Id,
            Actor = "hr@company.com",
            Comment = "Employee ID verified."
        });
        var second = await engine.CompleteStepAsync(new StepCompletionRequest { StepInstanceId = employee.Id, Actor = "hr@company.com" });

        first.Should().BeTrue();
        second.Should().BeTrue();

        var auditCount = await context.AuditLogs.CountAsync(x => x.WorkflowStepInstanceId == employee.Id && x.EventType == "StepCompleted");
        auditCount.Should().Be(1);
        var audit = await context.AuditLogs.SingleAsync(x => x.WorkflowStepInstanceId == employee.Id && x.EventType == "StepCompleted");
        audit.DetailsJson.Should().Contain("Employee ID verified.");
    }

    private static WorkflowDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new WorkflowDbContext(options);
    }

    private static WorkflowDefinition SeedSimpleWorkflow(WorkflowDbContext context, string workflowKey = "NEW_HIRE_ONBOARDING")
    {
        var definition = new WorkflowDefinition
        {
            WorkflowKey = workflowKey,
            Name = workflowKey.Replace("_", " "),
            Version = 1,
            ConfigHash = "HASH",
            IsActive = true,
            Enabled = true,
            ImportedAtUtc = DateTime.UtcNow
        };

        var employee = new WorkflowStepDefinition
        {
            WorkflowDefinition = definition,
            StepKey = "EMPLOYEE_ID",
            Name = "Create Employee ID",
            Owner = "hr@company.com",
            OwnerType = "Email",
            ExpectedDurationHours = 24,
            Required = true,
            Enabled = true,
            SortOrder = 1
        };

        var laptop = new WorkflowStepDefinition
        {
            WorkflowDefinition = definition,
            StepKey = "LAPTOP",
            Name = "Laptop Setup",
            Owner = "it@company.com",
            OwnerType = "Email",
            ExpectedDurationHours = 24,
            Required = true,
            Enabled = true,
            SortOrder = 2
        };

        var email = new WorkflowStepDefinition
        {
            WorkflowDefinition = definition,
            StepKey = "EMAIL",
            Name = "Email Account",
            Owner = "it@company.com",
            OwnerType = "Email",
            ExpectedDurationHours = 24,
            Required = true,
            Enabled = true,
            SortOrder = 3
        };

        var access = new WorkflowStepDefinition
        {
            WorkflowDefinition = definition,
            StepKey = "ACCESS",
            Name = "Project Access",
            Owner = "mgr@company.com",
            OwnerType = "Email",
            ExpectedDurationHours = 24,
            Required = true,
            Enabled = true,
            SortOrder = 4
        };

        context.WorkflowDefinitions.Add(definition);
        context.WorkflowStepDefinitions.AddRange(employee, laptop, email, access);
        context.SaveChanges();

        context.WorkflowStepDependencies.AddRange(
            new WorkflowStepDependency { WorkflowStepDefinitionId = laptop.Id, DependsOnStepDefinitionId = employee.Id },
            new WorkflowStepDependency { WorkflowStepDefinitionId = email.Id, DependsOnStepDefinitionId = employee.Id },
            new WorkflowStepDependency { WorkflowStepDefinitionId = access.Id, DependsOnStepDefinitionId = laptop.Id },
            new WorkflowStepDependency { WorkflowStepDefinitionId = access.Id, DependsOnStepDefinitionId = email.Id });

        context.SaveChanges();
        return definition;
    }

    private sealed class TestClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = utcNow;
    }

    private sealed class TestNotificationService : INotificationService
    {
        public List<NotificationMessage> Sent { get; } = new();

        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
