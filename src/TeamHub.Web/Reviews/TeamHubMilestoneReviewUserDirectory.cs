using TeamHub.Authentication;
using TeamHub.MilestoneReview;

namespace TeamHub.Web.Reviews;

public sealed class TeamHubMilestoneReviewUserDirectory(IUserAccessService users) : IMilestoneReviewUserDirectory
{
    public async Task<IReadOnlyList<MilestoneReviewUser>> GetActiveUsersAsync(
        CancellationToken cancellationToken = default) =>
        (await users.ListUsersAsync(cancellationToken))
        .Where(user => user.IsActive && user.IsRegistered)
        .Select(user => new MilestoneReviewUser(
            user.Id, user.DisplayName, user.UserId, user.IsActive, user.IsRegistered))
        .OrderBy(user => user.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
