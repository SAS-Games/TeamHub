using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using TeamHub.Studio;

namespace TeamHub.Tests;

public sealed class SupportSummaryPlanningServiceTests
{
    [Fact]
    public async Task PlanningTables_AreEmptyByDefaultAndPersistEditableRows()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-support-summary-{Guid.NewGuid():N}.db");
        try
        {
            var service = CreateService(databasePath);

            (await service.GetExpectedDeliveriesAsync()).Should().BeEmpty();
            (await service.GetBuildReviewsAsync()).Should().BeEmpty();

            var delivery = await service.SaveExpectedDeliveryAsync(new ExpectedMilestoneDelivery(
                Guid.Empty,
                "Project Alpha",
                "Feature complete",
                new DateOnly(2026, 10, 12)));
            var review = await service.SaveBuildReviewAsync(new MilestoneBuildReview(
                Guid.Empty,
                "Project Beta",
                MilestoneBuildReviewStatuses.InReview,
                new DateOnly(2026, 10, 15)));

            await service.SaveExpectedDeliveryAsync(delivery with
            {
                MilestoneDescription = "Release candidate",
                ExpectedDeliveryDate = new DateOnly(2026, 10, 14)
            });
            await service.SaveBuildReviewAsync(review with
            {
                Status = MilestoneBuildReviewStatuses.ReviewMeeting,
                Eta = new DateOnly(2026, 10, 18)
            });

            (await service.GetExpectedDeliveriesAsync()).Should().ContainSingle().Which.Should().Be(
                delivery with
                {
                    MilestoneDescription = "Release candidate",
                    ExpectedDeliveryDate = new DateOnly(2026, 10, 14)
                });
            (await service.GetBuildReviewsAsync()).Should().ContainSingle().Which.Should().Be(
                review with
                {
                    Status = MilestoneBuildReviewStatuses.ReviewMeeting,
                    Eta = new DateOnly(2026, 10, 18)
                });

            await service.DeleteExpectedDeliveryAsync(delivery.Id);
            await service.DeleteBuildReviewAsync(review.Id);

            (await service.GetExpectedDeliveriesAsync()).Should().BeEmpty();
            (await service.GetBuildReviewsAsync()).Should().BeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task SaveBuildReviewAsync_RejectsAnUnknownStatus()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-support-summary-{Guid.NewGuid():N}.db");
        try
        {
            var service = CreateService(databasePath);

            var action = () => service.SaveBuildReviewAsync(new MilestoneBuildReview(
                Guid.Empty,
                "Project Alpha",
                "Unknown",
                new DateOnly(2026, 10, 15)));

            await action.Should().ThrowAsync<ArgumentException>()
                .WithMessage("Select a valid review status.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static SqliteSupportSummaryPlanningService CreateService(string databasePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:StudioDb"] = $"Data Source={databasePath}"
            })
            .Build();
        return new SqliteSupportSummaryPlanningService(configuration);
    }
}
