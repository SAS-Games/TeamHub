using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TeamHub.Team;

public static class TeamDirectoryServiceCollectionExtensions
{
    public static IServiceCollection AddTeamDirectory(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<TeamDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("TeamDb") ?? "Data Source=data/team.db";
            options.UseSqlite(connectionString);
        });
        services.AddScoped<ITeamDatabaseInitializer, SqliteTeamDatabaseInitializer>();
        services.AddScoped<SqliteTeamDirectoryService>();
        services.AddScoped<ITeamDirectoryService>(provider => provider.GetRequiredService<SqliteTeamDirectoryService>());
        services.AddScoped<ITeamConfigurationService>(provider => provider.GetRequiredService<SqliteTeamDirectoryService>());
        return services;
    }
}
