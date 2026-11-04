using System.IO;
using System.Net.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using TeamHub.Authentication;
using TeamHub.AI;
using TeamHub.AI.Chat;
using TeamHub.AI.Chat.Contracts;
using TeamHub.AI.ReportGenerator;
using TeamHub.AI.FlowDesigner;
using TeamHub.AI.ReportGenerator.Persistence;
using TeamHub.AI.Usage;
using TeamHub.AI.Usage.Contracts;
using TeamHub.Application.Interfaces;
using TeamHub.Web.AccessControl;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.DependencyInjection;
using TeamHub.FlowDesigner.Web;
using TeamHub.Infrastructure;
using TeamHub.Infrastructure.Persistence;
using TeamHub.Milestones;
using TeamHub.MilestoneReview;
using TeamHub.Notifications;
using TeamHub.Notifications.DependencyInjection;
using TeamHub.Notifications.Persistence;
using TeamHub.Studio;
using TeamHub.Team;
using TeamHub.Web.Home;
using TeamHub.Web.Navigation;
using TeamHub.Web.Options;
using TeamHub.Web.FlowDesigner;
using TeamHub.Web.WorkCenter;
using TeamHub.Web.Formatting;
using TeamHub.Web.ExternalProcesses;
using TeamHub.Web.ExternalProcesses.MilestoneReviewReport;
using TeamHub.Web.ExternalProcesses.WorklogAnalytics;
using TeamHub.Web.Reports;
using TeamHub.Web.Reviews;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();
if (string.IsNullOrWhiteSpace(builder.Configuration["MilestoneConfiguration:SourceType"]))
{
    builder.Configuration["MilestoneConfiguration:SourceType"] = "LocalFile";
}
if (string.IsNullOrWhiteSpace(builder.Configuration["MilestoneConfiguration:ExcelPath"]))
{
    builder.Configuration["MilestoneConfiguration:ExcelPath"] = "config/MilestoneTracker.xlsx";
}
ConfigurationFilePaths.ResolveConfiguredPaths(builder.Configuration, builder.Environment.ContentRootPath);
var studioLogosDirectory = Path.GetDirectoryName(
    ConfigurationFilePaths.Resolve("config/Studios/.keep", builder.Environment.ContentRootPath))!;
Directory.CreateDirectory(studioLogosDirectory);
builder.Configuration["StudioLogos:Directory"] = studioLogosDirectory;

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);

var workflowConnectionString = $"Data Source={Path.Combine(dataDir, "workflow.db")}";
builder.Configuration["ConnectionStrings:WorkflowDb"] = workflowConnectionString;
builder.Configuration["ConnectionStrings:TeamDb"] = $"Data Source={Path.Combine(dataDir, "team.db")}";
builder.Configuration["ConnectionStrings:StudioDb"] = $"Data Source={Path.Combine(dataDir, "studio.db")}";
builder.Configuration["TeamExcel:UploadDirectory"] = Path.Combine(dataDir, "team-excel");
builder.Configuration["ConnectionStrings:AiReportsDb"] = $"Data Source={Path.Combine(dataDir, "ai-reports.db")}";
builder.Configuration["ConnectionStrings:AiUsageDb"] = $"Data Source={Path.Combine(dataDir, "ai-usage.db")}";
builder.Configuration["ConnectionStrings:MilestoneReviewDb"] = $"Data Source={Path.Combine(dataDir, "milestone-review.db")}";
var accessDatabasePath = Path.Combine(dataDir, "access.db");
var flowDesignerDatabasePath = Path.Combine(dataDir, "flowdesigner.db");
var flowLibraryDatabasePath = Path.Combine(dataDir, "flow-library.db");
var legacyTemplateDatabasePath = Path.Combine(dataDir, "templates.db");
var legacyPublishedDiagramDatabasePath = Path.Combine(dataDir, "published-diagrams.db");
var dataProtectionKeysPath = Path.Combine(dataDir, "protection-keys");
Directory.CreateDirectory(dataProtectionKeysPath);

// Add services to the container.
builder.Services.AddRazorPages()
    .AddApplicationPart(typeof(NotificationModuleMarker).Assembly);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ISafeMarkdownRenderer, SafeMarkdownRenderer>();
builder.Services.AddSingleton<TeamHub.Web.Confluence.IConfluenceContentRenderer, TeamHub.Web.Confluence.ConfluenceContentRenderer>();
builder.Services.Configure<ExternalProcessCatalogOptions>(
    builder.Configuration.GetSection(ExternalProcessCatalogOptions.SectionName));
var legacyWorklogSection = builder.Configuration.GetSection("WorklogAnalytics");
if (legacyWorklogSection.Exists())
{
    var legacyWorklog = legacyWorklogSection.Get<LegacyWorklogAnalyticsOptions>()!;
    builder.Services.PostConfigure<ExternalProcessCatalogOptions>(catalog =>
    {
        var process = catalog.Processes.FirstOrDefault(item =>
            item.Key.Equals(ExternalProcessKeys.WorklogAnalytics, StringComparison.OrdinalIgnoreCase)).Value;
        if (process is null)
        {
            process = new ExternalProcessOptions();
            catalog.Processes[ExternalProcessKeys.WorklogAnalytics] = process;
        }
        process.Enabled = legacyWorklog.Enabled;
        process.RootPath = legacyWorklog.RootPath;
        process.ExecutablePath = string.IsNullOrWhiteSpace(legacyWorklog.PythonExecutable)
            ? Path.Combine(legacyWorklog.RootPath, ".venv", "Scripts", "python.exe")
            : legacyWorklog.PythonExecutable;
        process.TimeoutSeconds = legacyWorklog.TimeoutSeconds;
    });
}
builder.Services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
builder.Services.AddScoped<IExternalProcessCredentialResolver, ExternalProcessCredentialResolver>();
builder.Services.AddScoped<IWorklogEffortService, PythonWorklogEffortService>();
builder.Services.AddScoped<IMilestoneReviewReportProcessService, MilestoneReviewReportProcessService>();
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
    .SetApplicationName("TeamHub");
if (OperatingSystem.IsWindows())
{
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}
builder.Services.Configure<ApplicationOptions>(builder.Configuration.GetSection("Application"));
builder.Services.Configure<List<NavigationTabOptions>>(builder.Configuration.GetSection("NavigationTabs"));
builder.Services.AddWorkflowAuthentication($"Data Source={accessDatabasePath}", options =>
{
    options.UserId = Environment.GetEnvironmentVariable("TEAMHUB_BOOTSTRAP_ADMIN_USER") ?? string.Empty;
    options.DisplayName = Environment.GetEnvironmentVariable("TEAMHUB_BOOTSTRAP_ADMIN_NAME") ?? string.Empty;
    options.Password = Environment.GetEnvironmentVariable("TEAMHUB_BOOTSTRAP_ADMIN_PASSWORD") ?? string.Empty;
});
builder.Services.AddAuthorization();
builder.Services.AddScoped<ICurrentAccessService, CurrentAccessService>();
builder.Services.AddSingleton<ITeamHubModuleCatalog, TeamHubModuleCatalog>();
builder.Services.AddWorkflowInfrastructure(builder.Configuration);
builder.Services.AddTeamHubNotifications(workflowConnectionString);
builder.Services.AddScoped<INotificationRecipientResolver, TeamHubNotificationRecipientResolver>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserProvider, TeamHubFlowCurrentUserProvider>();
builder.Services.AddScoped<IFlowReviewerDirectory, TeamHubFlowReviewerDirectory>();
builder.Services.AddScoped<IFlowPermissionService, TeamHubFlowPermissionService>();
builder.Services.AddScoped<IFlowPublicationService, WorkCenterFlowPublicationService>();
builder.Services.AddFlowDesigner(options =>
{
    options.ConnectionString = $"Data Source={flowDesignerDatabasePath}";
    options.LibraryConnectionString = $"Data Source={flowLibraryDatabasePath}";
    options.LegacyTemplateConnectionString = $"Data Source={legacyTemplateDatabasePath}";
    options.LegacyPublishedConnectionString = $"Data Source={legacyPublishedDiagramDatabasePath}";
});
builder.Services.AddMilestoneTracker(builder.Configuration);
builder.Services.AddTeamDirectory(builder.Configuration);
builder.Services.AddMilestoneReview(builder.Configuration);
builder.Services.AddScoped<IMilestoneReviewUserDirectory, TeamHubMilestoneReviewUserDirectory>();
builder.Services.AddScoped<IMilestoneReviewCommentPublisher, StudioMilestoneReviewCommentPublisher>();
builder.Services.AddScoped<IMilestoneReviewWorkflowStarter, TeamHubMilestoneReviewWorkflowStarter>();
builder.Services.AddStudioDirectory(builder.Configuration);
builder.Services.AddTeamHubAi(builder.Configuration);
builder.Services.AddAiUsageLedger(builder.Configuration);
builder.Services.AddScoped<TeamHub.AI.Contracts.IAiProviderCredentialAccessor, TeamHub.Web.AI.TeamHubAiProviderCredentialAccessor>();
builder.Services.AddSingleton<IAiChatInstructionProvider, TeamHub.Web.AI.TeamHubAiInstructionFileProvider>();
builder.Services.AddScoped<IAiMeetingNotesInstructionProvider, TeamHub.Web.AI.MeetingNotesInstructionFileProvider>();
builder.Services.AddScoped<TeamHub.Web.AI.IAiChatDocumentExtractor, TeamHub.Web.AI.AiChatDocumentExtractor>();
builder.Services.AddHttpClient<TeamHub.Web.AI.IMeetingNotesTeamsPublisher, TeamHub.Web.AI.MeetingNotesTeamsPublisher>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddAiChat(builder.Configuration);
builder.Services.AddScoped<IAiChatToolProvider, TeamHub.Web.AI.TeamHubAiDirectoryTools>();
builder.Services.AddScoped<IAiChatToolProvider, TeamHub.Web.AI.TeamHubAiStudioSupportTools>();
builder.Services.AddScoped<IAiChatKnowledgeSource, TeamHub.Web.AI.TeamHubChatKnowledgeSource>();
builder.Services.AddScoped<IAiChatKnowledgeSource, TeamHub.Web.AI.SupportSummaryChatKnowledgeSource>();
builder.Services.AddScoped<IAiChatKnowledgeSource, TeamHub.Web.AI.TeamHubPageChatKnowledgeSource>();
builder.Services.AddAiFlowDesigner(builder.Configuration);
builder.Services.AddReportGenerator(builder.Configuration);
builder.Services.AddScoped<IWeeklyWorkSummaryService, WeeklyWorkSummaryService>();
builder.Services.Configure<HomeConfigurationOptions>(builder.Configuration.GetSection(HomeConfigurationOptions.SectionName));
builder.Services.AddSingleton<IHomeContentService, HomeContentService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var userAccess = scope.ServiceProvider.GetRequiredService<IUserAccessService>();
    await userAccess.InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<IWorkflowDatabaseInitializer>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<INotificationDatabaseInitializer>().InitializeAsync();
    var studioDatabase = scope.ServiceProvider.GetRequiredService<IStudioDatabaseInitializer>();
    studioDatabase.InitializeAsync().GetAwaiter().GetResult();
    var teamDatabase = scope.ServiceProvider.GetRequiredService<ITeamDatabaseInitializer>();
    teamDatabase.InitializeAsync().GetAwaiter().GetResult();
    await scope.ServiceProvider.GetRequiredService<IReportGeneratorDatabaseInitializer>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<IAiUsageDatabaseInitializer>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<IMilestoneReviewDatabaseInitializer>().InitializeAsync();
    var customTeamTabs = scope.ServiceProvider.GetRequiredService<ICustomTeamTabService>();
    var customTeamModules = (await customTeamTabs.ListTabsAsync())
        .Select(tab => CustomTeamTabAccess.ModuleForSlug(tab.Slug))
        .ToList();
    await userAccess.EnsureModulesAsync(customTeamModules);
}
await app.Services.InitializeFlowDesignerAsync();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(studioLogosDirectory),
    RequestPath = "/studio-logos"
});

app.UseRouting();

app.UseAuthentication();
app.UseTeamHubAccessControl();
app.UseAuthorization();


app.MapGet("/api/work-center/assignees", async (
    IUserAccessService users,
    CancellationToken cancellationToken) =>
{
    var assignees = (await users.ListUsersAsync(cancellationToken))
        .Where(user => user.IsActive
            && user.IsRegistered
            && MailAddress.TryCreate(user.UserId, out _))
        .OrderBy(user => user.DisplayName)
        .ThenBy(user => user.UserId)
        .Select(user => new
        {
            email = user.UserId.Trim().ToLowerInvariant(),
            displayName = user.DisplayName
        })
        .ToList();
    return Results.Ok(assignees);
}).RequireAuthorization();
app.MapRazorPages();
app.MapFlowDesignerApi();

var moduleCatalog = app.Services.GetRequiredService<ITeamHubModuleCatalog>();
moduleCatalog.Discover(((IEndpointRouteBuilder)app).DataSources);
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<IUserAccessService>()
        .EnsureModulesAsync(moduleCatalog.Modules);
}

app.Run();
