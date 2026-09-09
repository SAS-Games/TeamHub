using Microsoft.EntityFrameworkCore;

namespace TeamHub.FlowDesigner.Persistence;

public sealed class TemplateCatalogDbContext(DbContextOptions<TemplateCatalogDbContext> options) : DbContext(options)
{
    internal DbSet<TemplateCatalogDefinitionEntity> Templates => Set<TemplateCatalogDefinitionEntity>();

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
    }
}
