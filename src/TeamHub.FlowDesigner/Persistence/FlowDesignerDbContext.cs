using Microsoft.EntityFrameworkCore;

namespace TeamHub.FlowDesigner.Persistence;

public sealed class FlowDesignerDbContext(DbContextOptions<FlowDesignerDbContext> options) : DbContext(options)
{
    internal DbSet<FlowDefinitionEntity> Flows => Set<FlowDefinitionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var flow = modelBuilder.Entity<FlowDefinitionEntity>();
        flow.ToTable("FlowDefinitions");
        flow.HasKey(item => item.Id);
        flow.Property(item => item.Name).HasMaxLength(200).IsRequired();
        flow.Property(item => item.Description).HasMaxLength(2000);
        flow.Property(item => item.Mode).HasMaxLength(50).IsRequired();
        flow.Property(item => item.GraphJson).IsRequired();
        flow.Property(item => item.CreatedBy).HasMaxLength(200);
        flow.HasIndex(item => item.UpdatedAt);
    }
}
