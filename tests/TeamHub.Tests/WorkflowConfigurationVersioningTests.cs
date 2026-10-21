using Microsoft.EntityFrameworkCore;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.Infrastructure.Persistence;
using TeamHub.Infrastructure.Services;

namespace TeamHub.Tests;

public class WorkflowConfigurationVersioningTests
{
    [Fact]
    public async Task PublishDraft_CreatesActiveWorkflowDefinition_AndRemovesDraftFromSavedList()
    {
        await using var context = CreateDbContext();
        var service = new WorkflowConfigurationService(context, new TestClock(DateTime.UtcNow));

        var draftId = await service.SaveDraftAsync(new WorkflowDraftDto
        {
            WorkflowKey = "GAME_RELEASE",
            WorkflowName = "Game Release",
            Enabled = true,
            Steps =
            [
                new WorkflowDraftStepDto
                {
                    StepKey = "QA_SIGNOFF",
                    StepName = "QA Signoff",
                    Owner = "qa@example.com",
                    ExpectedDurationHours = 8,
                    ReminderSubjectTemplate = "Reminder: {StepName}",
                    ReminderBodyTemplate = "Due {DueDate}",
                    OverdueSubjectTemplate = "Overdue: {StepName}",
                    OverdueBodyTemplate = "Overdue for {Recipient}",
                    EscalationSubjectTemplate = "Level {EscalationLevel}: {StepName}",
                    EscalationBodyTemplate = "Outstanding: {OutstandingAssignees}",
                    Required = true,
                    Enabled = true,
                    SortOrder = 1
                }
            ]
        });

        var result = await service.PublishDraftAsync(draftId, "admin");

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Empty(await service.GetDraftsAsync());
        var definition = await context.WorkflowDefinitions.Include(x => x.Steps).SingleAsync(x => x.WorkflowKey == "GAME_RELEASE");
        Assert.True(definition.IsActive);
        Assert.Equal("Game Release", definition.Name);
        var step = Assert.Single(definition.Steps);
        Assert.Equal("QA_SIGNOFF", step.StepKey);
        Assert.Equal("Reminder: {StepName}", step.ReminderSubjectTemplate);
        Assert.Equal("Due {DueDate}", step.ReminderBodyTemplate);
        Assert.Equal("Overdue: {StepName}", step.OverdueSubjectTemplate);
        Assert.Equal("Overdue for {Recipient}", step.OverdueBodyTemplate);
        Assert.Equal("Level {EscalationLevel}: {StepName}", step.EscalationSubjectTemplate);
        Assert.Equal("Outstanding: {OutstandingAssignees}", step.EscalationBodyTemplate);
    }

    [Fact]
    public async Task CreatesVersionOnlyWhenConfigurationChanges()
    {
        await using var context = CreateDbContext();
        var service = new WorkflowConfigurationService(context, new TestClock(DateTime.UtcNow));

        var first = await service.PublishDraftAsync(
            await service.SaveDraftAsync(BuildDraft("Workflow A")),
            "admin");
        var second = await service.PublishDraftAsync(
            await service.SaveDraftAsync(BuildDraft("Workflow A")),
            "admin");
        var third = await service.PublishDraftAsync(
            await service.SaveDraftAsync(BuildDraft("Workflow A Updated")),
            "admin");

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.True(third.Success);

        var versions = await context.WorkflowDefinitions
            .Where(x => x.WorkflowKey == "WF_A")
            .Select(x => x.Version)
            .OrderBy(x => x)
            .ToListAsync();

        Assert.Equal([1, 2], versions);
    }

    [Fact]
    public async Task SaveDraft_GeneratesTaskIdAndNormalizesEscalationDelays()
    {
        await using var context = CreateDbContext();
        var service = new WorkflowConfigurationService(context, new TestClock(DateTime.UtcNow));

        var id = await service.SaveDraftAsync(new WorkflowDraftDto
        {
            WorkflowKey = "RELEASE",
            WorkflowName = "Release",
            Steps =
            [
                new WorkflowDraftStepDto
                {
                    StepName = "QA Signoff",
                    Owner = "qa@example.com",
                    ExpectedDurationHours = 24,
                    EscalationAfterHours = 8,
                    EscalationOwner = "lead@example.com",
                    EscalationLevel2AfterHours = 4,
                    EscalationLevel2Owners = "manager@example.com",
                    EscalationLevel3AfterHours = 4,
                    EscalationLevel3Owners = "director@example.com",
                    Enabled = true
                }
            ]
        });

        var step = Assert.Single((await service.GetDraftAsync(id))!.Steps);
        Assert.Equal("QA_SIGNOFF", step.StepKey);
        Assert.Equal(8.5, step.EscalationLevel2AfterHours);
        Assert.Equal(9, step.EscalationLevel3AfterHours);
    }

    private static WorkflowDraftDto BuildDraft(string name)
    {
        return new WorkflowDraftDto
        {
            WorkflowKey = "WF_A",
            WorkflowName = name,
            Enabled = true,
            Steps =
            {
                new WorkflowDraftStepDto
                {
                    StepKey = "S1",
                    StepName = "Step 1",
                    Owner = "owner@x.com",
                    ExpectedDurationHours = 1,
                    Required = true,
                    Enabled = true,
                    SortOrder = 1
                }
            }
        };
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
}
