using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace TeamHub.Authentication;

public sealed class BootstrapAdminOptions
{
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public interface IWorkflowAuthenticationService
{
    Task<AuthorizedUserRecord?> ValidateCredentialsAsync(string username, string password, CancellationToken cancellationToken = default);
}

internal sealed class AuthorizedListWorkflowAuthenticationService(IUserAccessService users) : IWorkflowAuthenticationService
{
    public Task<AuthorizedUserRecord?> ValidateCredentialsAsync(string username, string password, CancellationToken cancellationToken = default) =>
        users.ValidateCredentialsAsync(username, password, cancellationToken);
}

public static class WorkflowAuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowAuthentication(
        this IServiceCollection services,
        string connectionString,
        Action<BootstrapAdminOptions>? configureBootstrapAdmin = null)
    {
        if (configureBootstrapAdmin is not null) services.Configure(configureBootstrapAdmin);
        else services.Configure<BootstrapAdminOptions>(_ => { });
        services.AddDbContextFactory<AccessControlDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IUserAccessService, UserAccessService>();
        services.AddScoped<IWorkflowAuthenticationService, AuthorizedListWorkflowAuthenticationService>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/Login";
                options.AccessDeniedPath = "/AccessDenied";
            });
        return services;
    }
}
