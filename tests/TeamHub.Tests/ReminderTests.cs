using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.Domain.Entities;
using TeamHub.Domain.Enums;
using TeamHub.Infrastructure.Persistence;
using TeamHub.Infrastructure.Services;

namespace TeamHub.Tests;

public class ReminderTests
{
    [Fact]
    public async Task DuplicateReminderIsNotSentInsideRepeatInterval()
    {
        await using var context = CreateDbContext();
        var clock = new TestClock(new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc));
        var notification = new TestNotificationService();
        var engine = new WorkflowEngineService(context, clock, notification);

        var definition = new WorkflowDefinition
        {
            WorkflowKey = "WF",
            Name = "Workflow",
            Version = 1,
            ConfigHash = "h",
            IsActive = true,
            Enabled = true,
            ImportedAtUtc = clock.UtcNow
        };

        var step = new WorkflowStepDefinition
        {
            WorkflowDefinition = definition,
            StepKey = "S1",
            Name = "Step 1",
            Owner = "owner@x.com",
            OwnerType = "Email",
            ExpectedDurationHours = 1,
            ReminderAfterHours = 1,
            ReminderRepeatHours = 2,
            Required = true,
            Enabled = true,
            SortOrder = 1
        };

        context.AddRange(definition, step);
        await context.SaveChangesAsync();

        await engine.StartWorkflowAsync(new StartWorkflowRequest { WorkflowKey = "WF", InstanceName = "Inst", StartedBy = "admin" });

        clock.UtcNow = clock.UtcNow.AddHours(2);
        var first = await engine.RunReminderCycleAsync();

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var second = await engine.RunReminderCycleAsync();

        first.Should().Be(1);
        second.Should().Be(0);
    }

    [Fact]
    public async Task ReminderIsNotSent_WhenTaskHasNoReminderConfiguration()
    {
        await using var context = CreateDbContext();
        var clock = new TestClock(new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc));
        var notification = new TestNotificationService();
        var engine = new WorkflowEngineService(context, clock, notification);
        var definition = new WorkflowDefinition
        {
            WorkflowKey = "NO_REMINDER",
            Name = "No reminder",
            Version = 1,
            ConfigHash = "no-reminder",
            IsActive = true,
            Enabled = true,
            ImportedAtUtc = clock.UtcNow
        };
        definition.Steps.Add(new WorkflowStepDefinition
        {
            WorkflowDefinition = definition,
            StepKey = "TASK",
            Name = "Task",
            Owner = "owner@x.com",
            ExpectedDurationHours = 1,
            Required = true,
            Enabled = true,
            SortOrder = 1
        });
        context.Add(definition);
        await context.SaveChangesAsync();

        await engine.StartWorkflowAsync(new StartWorkflowRequest { WorkflowKey = "NO_REMINDER", InstanceName = "Instance", StartedBy = "admin" });
        clock.UtcNow = clock.UtcNow.AddHours(2);

        (await engine.RunReminderCycleAsync()).Should().Be(0);
    }

    private static WorkflowDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WorkflowDbContext(options);
    }

    private sealed class TestClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = utcNow;
    }

    private sealed class TestNotificationService : INotificationService
    {
        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
