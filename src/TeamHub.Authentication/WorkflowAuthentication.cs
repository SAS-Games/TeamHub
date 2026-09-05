using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TeamHub.Authentication;

public sealed class WorkflowUser
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = "User";
}

public interface IWorkflowAuthenticationService
{
    WorkflowUser? ValidateCredentials(string username, string password);
}

internal sealed class ConfigurationWorkflowAuthenticationService(IConfiguration configuration) : IWorkflowAuthenticationService
{
    public WorkflowUser? ValidateCredentials(string username, string password)
    {
        var users = configuration.GetSection("WorkflowUsers").Get<List<WorkflowUser>>() ?? [];
        var normalizedUsername = username.Trim();
        return users.FirstOrDefault(user =>
            string.Equals(user.Username.Trim(), normalizedUsername, StringComparison.OrdinalIgnoreCase)
            && user.Password == password);
    }
}

public static class WorkflowAuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowAuthentication(this IServiceCollection services)
    {
        services.AddSingleton<IWorkflowAuthenticationService, ConfigurationWorkflowAuthenticationService>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.LoginPath = "/Login");
        return services;
    }
}
