using Microsoft.EntityFrameworkCore;

namespace TeamHub.FlowDesigner.Persistence;

public sealed class PublishedFlowDbContext(DbContextOptions<PublishedFlowDbContext> options) : DbContext(options)
{
    internal DbSet<PublishedFlowEntity> PublishedFlows => Set<PublishedFlowEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var flow = modelBuilder.Entity<PublishedFlowEntity>();
        flow.ToTable("PublishedDiagrams");
        flow.HasKey(item => item.Id);
        flow.Property(item => item.Name).HasMaxLength(200).IsRequired();
        flow.Property(item => item.Description).HasMaxLength(2000);
        flow.Property(item => item.DiagramType).HasMaxLength(50).IsRequired();
        flow.Property(item => item.GraphJson).IsRequired();
        flow.Property(item => item.SnapshotHash).HasMaxLength(64).IsRequired();
        flow.Property(item => item.PublishedBy).HasMaxLength(200).IsRequired();
        flow.HasIndex(item => item.PublicationRequestId).IsUnique();
        flow.HasIndex(item => new { item.SourceFlowId, item.IsCurrent });
        flow.HasIndex(item => item.PublishedAt);
    }
}
