using FluentAssertions;
using TeamHub.Milestones;
using TeamHub.Studio;
using TeamHub.Web.Pages;

namespace TeamHub.Tests;

public sealed class HeroProjectStudiosModelTests
{
    [Fact]
    public async Task OnGetAsync_ListsActiveThenInactiveStudiosWithMatchedReleaseDates()
    {
        var inactive = new StudioDetails
        {
            StudioName = "Alpha Studio", ProjectName = "Old Game", AboutGame = "Retired title", IsActive = false
        };
        var active = new StudioDetails
        {
            StudioName = "Zeta Studio", ProjectName = "New Game", AboutGame = "Current title", IsActive = true
        };
        var model = new HeroProjectStudiosModel(
            new StubStudioDirectoryService([inactive, active]),
            new StubMilestoneTrackerService(
            [
                new MilestoneDto { Title = "New Game", ReleaseDate = new DateTime(2027, 2, 1) },
                new MilestoneDto { Program = "Old Game", ReleaseDate = new DateTime(2025, 6, 15) }
            ]));

        await model.OnGetAsync(CancellationToken.None);

        model.Studios.Select(item => item.Studio.StudioName)
            .Should().Equal("Zeta Studio", "Alpha Studio");
        model.Studios[0].ReleaseDate.Should().Be(new DateTime(2027, 2, 1));
        model.Studios[1].ReleaseDate.Should().Be(new DateTime(2025, 6, 15));
    }

    private sealed class StubStudioDirectoryService(IReadOnlyList<StudioDetails> studios) : IStudioDirectoryService
    {
        public Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default) => Task.FromResult(studios);
        public Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<StudioDetails?>(null);
        public Task<StudioDetails> SaveStudioAsync(StudioDetails studio, CancellationToken cancellationToken = default) => Task.FromResult(studio);
        public Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubMilestoneTrackerService(IReadOnlyList<MilestoneDto> milestones) : IMilestoneTrackerService
    {
        public Task<IReadOnlyList<MilestoneDto>> GetMilestonesAsync(CancellationToken cancellationToken = default) => Task.FromResult(milestones);
        public Task<MilestoneSourceTestResult> TestSourceAsync(MilestoneSourceSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MilestoneSourceTestResult(milestones.Count));
    }
}