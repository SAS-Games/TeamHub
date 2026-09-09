using Microsoft.EntityFrameworkCore;
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

        services.AddDbContextFactory<FlowDesignerDbContext>(builder => builder.UseSqlite(options.ConnectionString));
        services.AddDbContextFactory<TemplateCatalogDbContext>(builder => builder.UseSqlite(options.TemplateConnectionString));
        services.AddSingleton<IFlowSerializer, SystemTextJsonFlowSerializer>();
        services.AddSingleton<IFlowValidator, FlowValidator>();
        services.AddScoped<IFlowRepository, SqliteFlowRepository>();
        services.AddScoped<ITemplateCatalogRepository, SqliteTemplateCatalogRepository>();
        services.AddScoped<IFlowTemplateCatalogService, FlowTemplateCatalogService>();
        services.AddScoped<TemplateCatalogSeeder>();
        services.AddScoped<IFlowService, FlowService>();
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
        var templateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TemplateCatalogDbContext>>();
        await using var templateContext = await templateFactory.CreateDbContextAsync(cancellationToken);
        await templateContext.Database.EnsureCreatedAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<TemplateCatalogSeeder>().SeedAsync(cancellationToken);
    }

    private sealed class AnonymousCurrentUserProvider : ICurrentUserProvider
    {
        public string? GetCurrentUserId() => null;
    }

    private sealed class AllowAllFlowPermissionService : IFlowPermissionService
    {
        public bool CanView(string? ownerId) => true;
        public bool CanEdit(string? ownerId) => true;
        public bool CanCreate() => true;
        public bool CanUseTemplate(FlowTemplate template) => true;
        public bool CanUseDiagramType(DiagramType diagramType) => true;
        public bool CanManageTemplates() => true;
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
