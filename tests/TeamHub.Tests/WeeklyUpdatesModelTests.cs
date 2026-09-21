using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Studio;
using TeamHub.Web.Pages.Studio;

namespace TeamHub.Tests;

public sealed class WeeklyUpdatesModelTests
{
    [Fact]
    public async Task OnGetAsync_UsesConfiguredGroupAndStudioOrder_InConsolidatedReport()
    {
        var studios = new[]
        {
            Studio("alpha", "Alpha Studio", "Alpha", groupOrder: 2, studioOrder: 1),
            Studio("zulu-two", "Zulu Two", "Zulu", groupOrder: 1, studioOrder: 2),
            Studio("zulu-one", "Zulu One", "Zulu", groupOrder: 1, studioOrder: 1),
            Studio("unranked", "Unranked Studio", "Unranked", groupOrder: null, studioOrder: null)
        };
        var returnedUpdates = new[]
        {
            Update("unranked"),
            Update("alpha"),
            Update("zulu-two"),
            Update("zulu-one")
        };
        var model = new WeeklyUpdatesModel(
            new StubStudioDirectoryService(studios),
            new StubConfluenceUpdateService(returnedUpdates))
        {
            StartDate = new DateOnly(2026, 9, 21),
            EndDate = new DateOnly(2026, 9, 25),
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

        await model.OnGetAsync(CancellationToken.None);

        model.Studios.Select(studio => studio.Id).Should().Equal(
            "zulu-one", "zulu-two", "alpha", "unranked");
        model.UpdateResult.Updates.Select(update => update.StudioId).Should().Equal(
            "zulu-one", "zulu-two", "alpha", "unranked");
    }

    private static StudioDetails Studio(
        string id,
        string name,
        string group,
        int? groupOrder,
        int? studioOrder) =>
        new()
        {
            Id = id,
            StudioName = name,
            ProjectName = $"{name} Project",
            StudioGroup = group,
            GroupDisplayOrder = groupOrder,
            StudioDisplayOrder = studioOrder,
            IsActive = true
        };

    private static StudioConfluenceWeeklyUpdate Update(string studioId) =>
        new()
        {
            StudioId = studioId,
            WeekStart = new DateOnly(2026, 9, 21),
            WeekEnd = new DateOnly(2026, 9, 25)
        };

    private sealed class StubStudioDirectoryService(IReadOnlyList<StudioDetails> studios) : IStudioDirectoryService
    {
        public Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(studios);

        public Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(studios.FirstOrDefault(studio => studio.Id == id));

        public Task<StudioDetails> SaveStudioAsync(StudioDetails studio, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubConfluenceUpdateService(IReadOnlyList<StudioConfluenceWeeklyUpdate> updates)
        : IStudioConfluenceUpdateService
    {
        public Task<StudioConfluenceUpdateResult> GetUpdatesAsync(
            StudioConfluenceUpdateQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StudioConfluenceUpdateResult> GetConsolidatedUpdatesAsync(
            ConsolidatedStudioConfluenceUpdateQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new StudioConfluenceUpdateResult
            {
                IsConfigured = true,
                Updates = updates
            });
    }
}
