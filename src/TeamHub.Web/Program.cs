using System.IO;
using Microsoft.AspNetCore.Authorization;
using TeamHub.Authentication;
using TeamHub.Infrastructure;
using TeamHub.Infrastructure.Persistence;
using TeamHub.Milestones;
using TeamHub.Studio;
using TeamHub.Team;
using TeamHub.Web.Home;
using TeamHub.Web.Navigation;
using TeamHub.Web.Options;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);

builder.Configuration["ConnectionStrings:WorkflowDb"] =
    $"Data Source={Path.Combine(dataDir, "workflow.db")}";

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.Configure<ApplicationOptions>(builder.Configuration.GetSection("Application"));
builder.Services.Configure<List<NavigationTabOptions>>(builder.Configuration.GetSection("NavigationTabs"));
builder.Services.AddWorkflowAuthentication();
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddWorkflowInfrastructure(builder.Configuration);
builder.Services.AddMilestoneTracker(builder.Configuration);
builder.Services.AddTeamDirectory(builder.Configuration);
builder.Services.AddStudioDirectory(builder.Configuration);
builder.Services.Configure<HomeConfigurationOptions>(builder.Configuration.GetSection(HomeConfigurationOptions.SectionName));
builder.Services.AddSingleton<IHomeContentService, HomeContentService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
    db.Database.EnsureCreated();
    var studioDatabase = scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>();
    studioDatabase.InitializeAsync().GetAwaiter().GetResult();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.Run();
