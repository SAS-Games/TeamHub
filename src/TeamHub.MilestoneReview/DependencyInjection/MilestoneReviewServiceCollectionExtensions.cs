using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TeamHub.MilestoneReview;

public static class MilestoneReviewServiceCollectionExtensions
{
    public static IServiceCollection AddMilestoneReview(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<MilestoneReviewDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("MilestoneReviewDb")
                ?? "Data Source=data/milestone-review.db";
            options.UseSqlite(connectionString);
        });
        services.AddScoped<IMilestoneReviewDatabaseInitializer, SqliteMilestoneReviewDatabaseInitializer>();
        services.AddScoped<IMilestoneReviewService, MilestoneReviewService>();
        return services;
    }
}
