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
        var service = new WorkflowConfigurationService(context, new TestProvider([]), new TestClock(DateTime.UtcNow));

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
        Assert.Equal("QA_SIGNOFF", Assert.Single(definition.Steps).StepKey);
    }

    [Fact]
    public async Task CreatesVersionOnlyWhenConfigurationChanges()
    {
        await using var context = CreateDbContext();
        var provider = new TestProvider(BuildDefinition("Workflow A"));
        var service = new WorkflowConfigurationService(context, provider, new TestClock(DateTime.UtcNow));

        var first = await service.SyncAsync("admin");
        var second = await service.SyncAsync("admin");

        provider.Definitions = BuildDefinition("Workflow A Updated");
        var third = await service.SyncAsync("admin");

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

    private static List<WorkflowDefinitionDto> BuildDefinition(string name)
    {
        return
        [
            new WorkflowDefinitionDto
            {
                WorkflowKey = "WF_A",
                WorkflowName = name,
                Enabled = true,
                Steps =
                {
                    new WorkflowStepDefinitionDto
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
            }
        ];
    }

    private static WorkflowDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WorkflowDbContext(options);
    }

    private sealed class TestProvider(List<WorkflowDefinitionDto> definitions) : IWorkflowDefinitionProvider
    {
        public List<WorkflowDefinitionDto> Definitions { get; set; } = definitions;

        public Task<IReadOnlyList<WorkflowDefinitionDto>> LoadDefinitionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowDefinitionDto>>(Definitions);
    }

    private sealed class TestClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; set; } = utcNow;
    }
}
