using Microsoft.EntityFrameworkCore;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Persistence;

public sealed class SqliteTemplateCatalogRepository(
    IDbContextFactory<FlowLibraryDbContext> contextFactory) : ITemplateCatalogRepository
{
    public async Task<IReadOnlyList<TemplateCatalogSummary>> ListAsync(
        string? templateKind = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.Templates.AsNoTracking().Where(template => template.IsActive);
        if (!string.IsNullOrWhiteSpace(templateKind))
        {
            query = query.Where(template => template.TemplateKind == templateKind);
        }

        var templates = await query
            .OrderBy(template => template.Category)
            .ThenBy(template => template.Name)
            .ToListAsync(cancellationToken);
        return templates.Select(template => new TemplateCatalogSummary(
            template.Id,
            template.TemplateKey,
            template.Name,
            template.Description,
            template.Category,
            template.TemplateKind,
            ParseDiagramType(template.DiagramType),
            template.Version,
            template.IsBuiltIn,
            template.AdminOnly,
            new DateTimeOffset(DateTime.SpecifyKind(template.UpdatedAt, DateTimeKind.Utc))))
            .ToList();
    }

    public async Task<TemplateCatalogDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Templates.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return entity is null ? null : ToDefinition(entity);
    }

    public async Task<TemplateCatalogDefinition?> GetByKeyAsync(string templateKey, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var normalizedKey = templateKey.Trim().ToUpperInvariant();
        var entity = await context.Templates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TemplateKey == normalizedKey, cancellationToken);
        return entity is null ? null : ToDefinition(entity);
    }

    public async Task<bool> IsDeletedAsync(string templateKey, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var normalizedKey = templateKey.Trim().ToUpperInvariant();
        return await context.DeletedTemplates.AsNoTracking()
            .AnyAsync(item => item.TemplateKey == normalizedKey, cancellationToken);
    }

    public async Task SaveAsync(TemplateCatalogDefinition template, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Templates.SingleOrDefaultAsync(item => item.Id == template.Id, cancellationToken);
        if (entity is null)
        {
            entity = new TemplateCatalogDefinitionEntity { Id = template.Id };
            context.Templates.Add(entity);
        }

        entity.TemplateKey = template.TemplateKey;
        entity.Name = template.Name;
        entity.Description = template.Description;
        entity.Category = template.Category;
        entity.TemplateKind = template.TemplateKind;
        entity.DiagramType = template.DiagramType?.ToString();
        entity.PayloadJson = template.PayloadJson;
        entity.Version = template.Version;
        entity.IsActive = template.IsActive;
        entity.IsBuiltIn = template.IsBuiltIn;
        entity.AdminOnly = template.AdminOnly;
        entity.CreatedBy = template.CreatedBy;
        entity.CreatedAt = template.CreatedAt.UtcDateTime;
        entity.UpdatedAt = template.UpdatedAt.UtcDateTime;

        var deletion = await context.DeletedTemplates
            .SingleOrDefaultAsync(item => item.TemplateKey == template.TemplateKey, cancellationToken);
        if (deletion is not null) context.DeletedTemplates.Remove(deletion);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Templates.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (entity is null) return;

        context.Templates.Remove(entity);
        if (!await context.DeletedTemplates.AnyAsync(item => item.TemplateKey == entity.TemplateKey, cancellationToken))
        {
            context.DeletedTemplates.Add(new DeletedTemplateDefinitionEntity
            {
                TemplateKey = entity.TemplateKey,
                DeletedAt = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    private static TemplateCatalogDefinition ToDefinition(TemplateCatalogDefinitionEntity entity) => new()
    {
        Id = entity.Id,
        TemplateKey = entity.TemplateKey,
        Name = entity.Name,
        Description = entity.Description,
        Category = entity.Category,
        TemplateKind = entity.TemplateKind,
        DiagramType = ParseDiagramType(entity.DiagramType),
        PayloadJson = entity.PayloadJson,
        Version = entity.Version,
        IsActive = entity.IsActive,
        IsBuiltIn = entity.IsBuiltIn,
        AdminOnly = entity.AdminOnly,
        CreatedBy = entity.CreatedBy,
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(entity.CreatedAt, DateTimeKind.Utc)),
        UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(entity.UpdatedAt, DateTimeKind.Utc))
    };

    private static DiagramType? ParseDiagramType(string? value) =>
        Enum.TryParse<DiagramType>(value, out var diagramType) ? diagramType : null;
}
