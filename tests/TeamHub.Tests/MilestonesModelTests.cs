using FluentAssertions;
using TeamHub.Milestones;
using TeamHub.Studio;
using TeamHub.Web.Pages;

namespace TeamHub.Tests;

public sealed class MilestonesModelTests
{
    [Fact]
    public async Task OnGetAsync_ShowsOnlyMilestonesForActiveConfiguredStudios()
    {
        var milestones = new StubMilestoneTrackerService(
        [
            Milestone("Active by title", title: "Project One", program: "Program A"),
            Milestone("Active by program", title: "Game Two", program: "Project Two"),
            Milestone("Inactive", title: "Inactive Project", program: "Program C"),
            Milestone("Unconfigured", title: "Unknown Project", program: "Program D")
        ]);
        var studios = new StubStudioDirectoryService(
        [
            new StudioDetails { ProjectName = "project one", IsActive = true },
            new StudioDetails { ProjectName = "PROJECT TWO", IsActive = true },
            new StudioDetails { ProjectName = "Inactive Project", IsActive = false }
        ]);
        var model = new MilestonesModel(milestones, studios);

        await model.OnGetAsync(CancellationToken.None);

        model.Milestones.Select(milestone => milestone.Description)
            .Should().Equal("Active by title", "Active by program");
    }

    private static MilestoneDto Milestone(string description, string title, string program) => new()
    {
        Description = description,
        Title = title,
        Program = program,
        Milestone = "Alpha"
    };

    private sealed class StubMilestoneTrackerService(IReadOnlyList<MilestoneDto> milestones) : IMilestoneTrackerService
    {
        public Task<IReadOnlyList<MilestoneDto>> GetMilestonesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(milestones);

        public Task<MilestoneSourceTestResult> TestSourceAsync(
            MilestoneSourceSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MilestoneSourceTestResult(milestones.Count));
    }

    private sealed class StubStudioDirectoryService(IReadOnlyList<StudioDetails> studios) : IStudioDirectoryService
    {
        public Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(studios);

        public Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<StudioDetails?>(null);

        public Task<StudioDetails> SaveStudioAsync(StudioDetails studio, CancellationToken cancellationToken = default) =>
            Task.FromResult(studio);

        public Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}