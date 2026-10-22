using Microsoft.EntityFrameworkCore;

namespace TeamHub.FlowDesigner.Persistence;

public sealed class FlowDesignerDbContext(DbContextOptions<FlowDesignerDbContext> options) : DbContext(options)
{
    internal DbSet<FlowDefinitionEntity> Flows => Set<FlowDefinitionEntity>();
    internal DbSet<FlowPublicationRequestEntity> PublicationRequests => Set<FlowPublicationRequestEntity>();
    internal DbSet<FlowPublicationReviewerEntity> PublicationReviewers => Set<FlowPublicationReviewerEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var flow = modelBuilder.Entity<FlowDefinitionEntity>();
        flow.ToTable("FlowDefinitions");
        flow.HasKey(item => item.Id);
        flow.Property(item => item.Name).HasMaxLength(200).IsRequired();
        flow.Property(item => item.Description).HasMaxLength(2000);
        flow.Property(item => item.DiagramType).HasColumnName("Mode").HasMaxLength(50).IsRequired();
        flow.Property(item => item.GraphJson).IsRequired();
        flow.Property(item => item.CreatedBy).HasMaxLength(200);
        flow.HasIndex(item => item.UpdatedAt);

        var request = modelBuilder.Entity<FlowPublicationRequestEntity>();
        request.ToTable("FlowPublicationRequests");
        request.HasKey(item => item.Id);
        request.Property(item => item.FlowName).HasMaxLength(200).IsRequired();
        request.Property(item => item.DiagramType).HasMaxLength(50).IsRequired();
        request.Property(item => item.SnapshotJson).IsRequired();
        request.Property(item => item.SnapshotHash).HasMaxLength(64).IsRequired();
        request.Property(item => item.RequestedBy).HasMaxLength(200).IsRequired();
        request.Property(item => item.Status).HasMaxLength(30).IsRequired();
        request.Property(item => item.ReviewedBy).HasMaxLength(200);
        request.Property(item => item.ReviewNote).HasMaxLength(2000);
        request.Property(item => item.DeletedBy).HasMaxLength(200);
        request.HasIndex(item => new { item.Status, item.RequestedAt });
        request.HasIndex(item => new { item.FlowId, item.RequestedAt });

        var reviewer = modelBuilder.Entity<FlowPublicationReviewerEntity>();
        reviewer.ToTable("FlowPublicationReviewers");
        reviewer.HasKey(item => new { item.RequestId, item.UserId });
        reviewer.Property(item => item.UserId).HasMaxLength(320);
        reviewer.Property(item => item.DisplayName).HasMaxLength(200).IsRequired();
        reviewer.Property(item => item.Status).HasMaxLength(30).IsRequired();
        reviewer.Property(item => item.ReviewNote).HasMaxLength(2000);
        reviewer.HasIndex(item => new { item.UserId, item.Status });
        reviewer.HasOne<FlowPublicationRequestEntity>().WithMany()
            .HasForeignKey(item => item.RequestId).OnDelete(DeleteBehavior.Cascade);
    }
}
