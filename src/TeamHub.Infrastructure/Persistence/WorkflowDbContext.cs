using Microsoft.EntityFrameworkCore;
using TeamHub.Domain.Entities;

namespace TeamHub.Infrastructure.Persistence;

public sealed class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options) : DbContext(options)
{
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<WorkflowStepDefinition> WorkflowStepDefinitions => Set<WorkflowStepDefinition>();
    public DbSet<WorkflowStepDependency> WorkflowStepDependencies => Set<WorkflowStepDependency>();
    public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
    public DbSet<WorkflowStepInstance> WorkflowStepInstances => Set<WorkflowStepInstance>();
    public DbSet<WorkflowStepInstanceDependency> WorkflowStepInstanceDependencies => Set<WorkflowStepInstanceDependency>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<WorkflowDraftDefinition> WorkflowDraftDefinitions => Set<WorkflowDraftDefinition>();
    public DbSet<WorkflowDraftStepDefinition> WorkflowDraftStepDefinitions => Set<WorkflowDraftStepDefinition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkflowDefinition>(e =>
        {
            e.HasIndex(x => new { x.WorkflowKey, x.Version }).IsUnique();
            e.HasIndex(x => new { x.WorkflowKey, x.ConfigHash }).IsUnique();
            e.Property(x => x.WorkflowKey).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.ConfigHash).HasMaxLength(128);
        });

        modelBuilder.Entity<WorkflowStepDefinition>(e =>
        {
            e.HasIndex(x => new { x.WorkflowDefinitionId, x.StepKey }).IsUnique();
            e.Property(x => x.StepKey).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Owner).HasMaxLength(256);
            e.Property(x => x.OwnerType).HasMaxLength(64);
            e.HasOne(x => x.WorkflowDefinition)
                .WithMany(x => x.Steps)
                .HasForeignKey(x => x.WorkflowDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowStepDependency>(e =>
        {
            e.HasIndex(x => new { x.WorkflowStepDefinitionId, x.DependsOnStepDefinitionId }).IsUnique();
            e.HasOne(x => x.WorkflowStepDefinition)
                .WithMany(x => x.Dependencies)
                .HasForeignKey(x => x.WorkflowStepDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowInstance>(e =>
        {
            e.Property(x => x.InstanceName).HasMaxLength(256);
            e.Property(x => x.StartedBy).HasMaxLength(256);
            e.HasIndex(x => new { x.WorkflowDefinitionId, x.InstanceName })
                .IsUnique()
                .HasFilter("Status = 1");
            e.HasOne(x => x.WorkflowDefinition)
                .WithMany()
                .HasForeignKey(x => x.WorkflowDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkflowStepInstance>(e =>
        {
            e.HasIndex(x => new { x.WorkflowInstanceId, x.StepKey });
            e.Property(x => x.StepKey).HasMaxLength(128);
            e.Property(x => x.StepName).HasMaxLength(256);
            e.Property(x => x.Owner).HasMaxLength(256);
            e.Property(x => x.OwnerType).HasMaxLength(64);
            e.HasOne(x => x.WorkflowInstance)
                .WithMany(x => x.Steps)
                .HasForeignKey(x => x.WorkflowInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowStepInstanceDependency>(e =>
        {
            e.HasKey(x => new { x.WorkflowStepInstanceId, x.DependsOnWorkflowStepInstanceId });
            e.HasOne(x => x.WorkflowStepInstance)
                .WithMany(x => x.Dependencies)
                .HasForeignKey(x => x.WorkflowStepInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationLog>(e =>
        {
            e.Property(x => x.Recipient).HasMaxLength(256);
            e.Property(x => x.ErrorMessage).HasMaxLength(2048);
            e.HasIndex(x => new { x.WorkflowStepInstanceId, x.Type, x.Recipient, x.SentAtUtc });
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.Property(x => x.EventType).HasMaxLength(64);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.HasIndex(x => new { x.WorkflowInstanceId, x.TimestampUtc });
        });

        modelBuilder.Entity<WorkflowDraftDefinition>(e =>
        {
            e.Property(x => x.WorkflowKey).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(256);
            e.HasIndex(x => x.PublishedAtUtc);
        });

        modelBuilder.Entity<WorkflowDraftStepDefinition>(e =>
        {
            e.Property(x => x.StepKey).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Owner).HasMaxLength(256);
            e.Property(x => x.OwnerType).HasMaxLength(64);
            e.HasIndex(x => new { x.WorkflowDraftDefinitionId, x.SortOrder });
            e.HasOne(x => x.WorkflowDraftDefinition)
                .WithMany(x => x.Steps)
                .HasForeignKey(x => x.WorkflowDraftDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
