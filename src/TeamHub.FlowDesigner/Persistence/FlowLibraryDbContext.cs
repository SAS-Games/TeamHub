using Microsoft.EntityFrameworkCore;

namespace TeamHub.FlowDesigner.Persistence;

public sealed class FlowLibraryDbContext(DbContextOptions<FlowLibraryDbContext> options) : DbContext(options)
{
    internal DbSet<TemplateCatalogDefinitionEntity> Templates => Set<TemplateCatalogDefinitionEntity>();
    internal DbSet<PublishedFlowEntity> PublishedFlows => Set<PublishedFlowEntity>();
    internal DbSet<DeletedTemplateDefinitionEntity> DeletedTemplates => Set<DeletedTemplateDefinitionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var template = modelBuilder.Entity<TemplateCatalogDefinitionEntity>();
        template.ToTable("TemplateDefinitions");
        template.HasKey(item => item.Id);
        template.Property(item => item.TemplateKey).HasMaxLength(100).IsRequired();
        template.Property(item => item.Name).HasMaxLength(200).IsRequired();
        template.Property(item => item.Description).HasMaxLength(2000);
        template.Property(item => item.Category).HasMaxLength(100).IsRequired();
        template.Property(item => item.TemplateKind).HasMaxLength(50).IsRequired();
        template.Property(item => item.DiagramType).HasMaxLength(50);
        template.Property(item => item.PayloadJson).IsRequired();
        template.Property(item => item.CreatedBy).HasMaxLength(200);
        template.HasIndex(item => item.TemplateKey).IsUnique();
        template.HasIndex(item => new { item.TemplateKind, item.IsActive });

        var publishedFlow = modelBuilder.Entity<PublishedFlowEntity>();
        publishedFlow.ToTable("PublishedDiagrams");
        publishedFlow.HasKey(item => item.Id);
        publishedFlow.Property(item => item.Name).HasMaxLength(200).IsRequired();
        publishedFlow.Property(item => item.Description).HasMaxLength(2000);
        publishedFlow.Property(item => item.DiagramType).HasMaxLength(50).IsRequired();
        publishedFlow.Property(item => item.GraphJson).IsRequired();
        publishedFlow.Property(item => item.SnapshotHash).HasMaxLength(64).IsRequired();
        publishedFlow.Property(item => item.PublishedBy).HasMaxLength(200).IsRequired();
        publishedFlow.HasIndex(item => item.PublicationRequestId).IsUnique();
        publishedFlow.HasIndex(item => new { item.SourceFlowId, item.IsCurrent });
        publishedFlow.HasIndex(item => item.PublishedAt);

        var deletedTemplate = modelBuilder.Entity<DeletedTemplateDefinitionEntity>();
        deletedTemplate.ToTable("DeletedTemplateDefinitions");
        deletedTemplate.HasKey(item => item.TemplateKey);
        deletedTemplate.Property(item => item.TemplateKey).HasMaxLength(100);
    }
}
