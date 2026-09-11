using System.Net.Mail;
using TeamHub.Application.Interfaces;
using TeamHub.Authentication;

namespace TeamHub.Web.WorkCenter;

public sealed class TeamHubNotificationRecipientResolver(IUserAccessService users) : INotificationRecipientResolver
{
    public async Task<string?> ResolveEmailAsync(string recipient, CancellationToken cancellationToken = default)
    {
        var value = recipient?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (MailAddress.TryCreate(value, out var direct)) return direct.Address;

        var user = await users.FindActiveUserAsync(value, cancellationToken);
        if (user is null)
        {
            user = (await users.ListUsersAsync(cancellationToken))
                .FirstOrDefault(item => item.IsActive
                    && string.Equals(item.DisplayName, value, StringComparison.OrdinalIgnoreCase));
        }
        return user is not null && MailAddress.TryCreate(user.UserId, out var resolved)
            ? resolved.Address
            : null;
    }
}
