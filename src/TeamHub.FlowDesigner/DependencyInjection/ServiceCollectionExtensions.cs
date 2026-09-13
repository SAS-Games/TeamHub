using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Persistence;
using TeamHub.FlowDesigner.Serialization;
using TeamHub.FlowDesigner.Services;
using TeamHub.FlowDesigner.Validation;

namespace TeamHub.FlowDesigner.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFlowDesigner(
        this IServiceCollection services,
        Action<FlowDesignerOptions>? configure = null)
    {
        var options = new FlowDesignerOptions();
        configure?.Invoke(options);

        var publishedConnectionString = options.PublishedConnectionString ?? DerivePublishedConnectionString(options.ConnectionString);
        services.AddDbContextFactory<FlowDesignerDbContext>(builder => builder.UseSqlite(options.ConnectionString));
        services.AddDbContextFactory<TemplateCatalogDbContext>(builder => builder.UseSqlite(options.TemplateConnectionString));
        services.AddDbContextFactory<PublishedFlowDbContext>(builder => builder.UseSqlite(publishedConnectionString));
        services.AddSingleton<IFlowSerializer, SystemTextJsonFlowSerializer>();
        services.AddSingleton<IFlowValidator, FlowValidator>();
        services.AddScoped<IFlowRepository, SqliteFlowRepository>();
        services.AddScoped<ITemplateCatalogRepository, SqliteTemplateCatalogRepository>();
        services.AddScoped<IFlowTemplateCatalogService, FlowTemplateCatalogService>();
        services.AddScoped<TemplateCatalogSeeder>();
        services.AddScoped<IFlowService, FlowService>();
        services.AddScoped<IFlowPublicationWorkflowService, FlowPublicationWorkflowService>();
        services.AddScoped<PublishedFlowRecoveryService>();
        services.TryAddSingleton<ICurrentUserProvider, AnonymousCurrentUserProvider>();
        services.TryAddSingleton<IFlowPermissionService, AllowAllFlowPermissionService>();
        services.TryAddSingleton<IFlowThemeProvider, DefaultFlowThemeProvider>();
        services.TryAddSingleton<IFlowNavigationProvider, DefaultFlowNavigationProvider>();
        services.TryAddSingleton<IFlowPublicationService, NoFlowPublicationService>();
        return services;
    }

    public static async Task InitializeFlowDesignerAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FlowDesignerDbContext>>();
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await EnsurePublicationRequestTableAsync(context, cancellationToken);
        await EnsurePublicationRequestDeletionColumnsAsync(context, cancellationToken);
        var publishedFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PublishedFlowDbContext>>();
        await using var publishedContext = await publishedFactory.CreateDbContextAsync(cancellationToken);
        await publishedContext.Database.EnsureCreatedAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<PublishedFlowRecoveryService>().RecoverIfEmptyAsync(cancellationToken);
        var templateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TemplateCatalogDbContext>>();
        await using var templateContext = await templateFactory.CreateDbContextAsync(cancellationToken);
        await templateContext.Database.EnsureCreatedAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<TemplateCatalogSeeder>().SeedAsync(cancellationToken);
    }

    private static string DerivePublishedConnectionString(string authoringConnectionString)
    {
        var builder = new SqliteConnectionStringBuilder(authoringConnectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:")
        {
            return authoringConnectionString;
        }

        var fullAuthoringPath = Path.GetFullPath(builder.DataSource);
        builder.DataSource = Path.Combine(Path.GetDirectoryName(fullAuthoringPath)!, "published-diagrams.db");
        return builder.ToString();
    }

    private static Task EnsurePublicationRequestTableAsync(
        FlowDesignerDbContext context,
        CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS FlowPublicationRequests (
                Id TEXT NOT NULL CONSTRAINT PK_FlowPublicationRequests PRIMARY KEY,
                FlowId TEXT NOT NULL,
                FlowName TEXT NOT NULL,
                DiagramType TEXT NOT NULL,
                SourceVersion INTEGER NOT NULL,
                NodeCount INTEGER NOT NULL,
                SnapshotJson TEXT NOT NULL,
                SnapshotHash TEXT NOT NULL,
                RequestedBy TEXT NOT NULL,
                RequestedAt TEXT NOT NULL,
                Status TEXT NOT NULL,
                ReviewedBy TEXT NULL,
                ReviewedAt TEXT NULL,
                ReviewNote TEXT NULL,
                DeletedBy TEXT NULL,
                DeletedAt TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_FlowPublicationRequests_Status_RequestedAt
                ON FlowPublicationRequests (Status, RequestedAt);
            CREATE INDEX IF NOT EXISTS IX_FlowPublicationRequests_FlowId_RequestedAt
                ON FlowPublicationRequests (FlowId, RequestedAt);
            """, cancellationToken);

    private static async Task EnsurePublicationRequestDeletionColumnsAsync(
        FlowDesignerDbContext context,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await ColumnExistsAsync(context, "FlowPublicationRequests", "DeletedBy", cancellationToken))
            {
                await context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE FlowPublicationRequests ADD COLUMN DeletedBy TEXT NULL;",
                    cancellationToken);
            }
            if (!await ColumnExistsAsync(context, "FlowPublicationRequests", "DeletedAt", cancellationToken))
            {
                await context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE FlowPublicationRequests ADD COLUMN DeletedAt TEXT NULL;",
                    cancellationToken);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        FlowDesignerDbContext context,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private sealed class AnonymousCurrentUserProvider : ICurrentUserProvider
    {
        public string? GetCurrentUserId() => null;
    }

    private sealed class AllowAllFlowPermissionService : IFlowPermissionService
    {
        public bool CanView(string? ownerId) => true;
        public bool CanViewShared() => true;
        public bool CanEdit(string? ownerId) => true;
        public bool CanDelete(string? ownerId) => true;
        public bool CanCreate() => true;
        public bool CanUseTemplate(FlowTemplate template) => true;
        public bool CanUseDiagramType(DiagramType diagramType) => true;
        public bool CanManageTemplates() => true;
        public bool CanReviewPublications() => true;
    }

    private sealed class NoFlowPublicationService : IFlowPublicationService
    {
        public bool CanPublish(FlowDefinition flow) => false;

        public Task<FlowPublicationResult> PublishAsync(FlowDefinition flow, CancellationToken cancellationToken = default) =>
            Task.FromResult(FlowPublicationResult.Invalid(["Publishing is not configured for this host."]));
    }

    private sealed class DefaultFlowThemeProvider : IFlowThemeProvider
    {
        public string? AccentColor => "#087f6b";
        public string? CssClass => null;
    }

    private sealed class DefaultFlowNavigationProvider : IFlowNavigationProvider
    {
        public string FlowsPath => "/flows";
    }
}
