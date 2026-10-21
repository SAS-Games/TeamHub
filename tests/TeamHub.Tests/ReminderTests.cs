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

    [Fact]
    public async Task StepReminderTemplateRendersWorkflowAndMetadataPlaceholders()
    {
        await using var context = CreateDbContext();
        var clock = new TestClock(new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc));
        var notification = new TestNotificationService();
        var engine = new WorkflowEngineService(context, clock, notification);
        var definition = new WorkflowDefinition
        {
            WorkflowKey = "MILESTONE_REVIEW",
            Name = "Milestone Review",
            Version = 1,
            ConfigHash = "custom-reminder",
            IsActive = true,
            Enabled = true,
            ImportedAtUtc = clock.UtcNow
        };
        definition.Steps.Add(new WorkflowStepDefinition
        {
            WorkflowDefinition = definition,
            StepKey = "REVIEW",
            Name = "Review milestone",
            Owner = "reviewer@x.com",
            ExpectedDurationHours = 8,
            ReminderAfterHours = 1,
            ReminderSubjectTemplate = "{ProjectName} - {StepName}",
            ReminderBodyTemplate = "Hello {Recipient}. Review {MilestoneName} by {DueDate}; due hours: {DueHours}; workflow: {WorkflowName}.",
            Required = true,
            Enabled = true,
            SortOrder = 1
        });
        context.Add(definition);
        await context.SaveChangesAsync();

        await engine.StartWorkflowAsync(new StartWorkflowRequest
        {
            WorkflowKey = definition.WorkflowKey,
            InstanceName = "Project Alpha Beta M2",
            StartedBy = "admin",
            MetadataJson = """{"ProjectName":"Project Alpha","MilestoneName":"M2"}"""
        });
        notification.Messages.Clear();
        clock.UtcNow = clock.UtcNow.AddHours(2);

        (await engine.RunReminderCycleAsync()).Should().Be(1);
        notification.Messages.Single().Subject.Should().Be("Project Alpha - Review milestone");
        notification.Messages.Single().Body.Should().Be(
            "Hello reviewer@x.com. Review M2 by 2026-09-04 18:00 UTC; due hours: 8; workflow: Milestone Review.");
    }

    [Fact]
    public async Task EscalationLevelsNotifyEveryRecipientOnceAtTheirConfiguredThresholds()
    {
        await using var context = CreateDbContext();
        var clock = new TestClock(new DateTime(2026, 9, 4, 10, 0, 0, DateTimeKind.Utc));
        var notification = new TestNotificationService();
        var engine = new WorkflowEngineService(context, clock, notification);
        var definition = new WorkflowDefinition
        {
            WorkflowKey = "ESCALATION_LEVELS",
            Name = "Escalation levels",
            Version = 1,
            ConfigHash = "escalation-levels",
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
            EscalationAfterHours = 2,
            EscalationOwner = "lead@x.com; manager@x.com",
            EscalationLevel2AfterHours = 6,
            EscalationLevel2Owners = "director@x.com",
            EscalationSubjectTemplate = "Level {EscalationLevel}: {StepName}",
            EscalationBodyTemplate = "Escalate {InstanceName} for {OutstandingAssignees} to {Recipient}.",
            Required = true,
            Enabled = true,
            SortOrder = 1
        });
        context.Add(definition);
        await context.SaveChangesAsync();

        await engine.StartWorkflowAsync(new StartWorkflowRequest
        {
            WorkflowKey = definition.WorkflowKey,
            InstanceName = "Instance",
            StartedBy = "admin"
        });
        notification.Messages.Clear();

        clock.UtcNow = clock.UtcNow.AddHours(3);
        (await engine.RunReminderCycleAsync()).Should().Be(2);
        notification.Messages.Select(message => message.Recipient)
            .Should().BeEquivalentTo("lead@x.com", "manager@x.com");
        notification.Messages.Should().OnlyContain(message => message.Subject == "Level 1: Task");
        notification.Messages.Should().OnlyContain(message =>
            message.Body == $"Escalate Instance for owner@x.com to {message.Recipient}.");

        (await engine.RunReminderCycleAsync()).Should().Be(0);

        clock.UtcNow = clock.UtcNow.AddHours(4);
        (await engine.RunReminderCycleAsync()).Should().Be(1);
        notification.Messages.Last().Recipient.Should().Be("director@x.com");
        notification.Messages.Last().Subject.Should().Be("Level 2: Task");
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
        public List<NotificationMessage> Messages { get; } = [];

        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
