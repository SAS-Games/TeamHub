using Microsoft.EntityFrameworkCore;

namespace TeamHub.Notifications.Persistence;

internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<NotificationInboxSource> NotificationOutbox => Set<NotificationInboxSource>();
    public DbSet<NotificationReadReceipt> ReadReceipts => Set<NotificationReadReceipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationInboxSource>(entity =>
        {
            entity.ToTable("NotificationOutbox");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Recipient).HasMaxLength(320).UseCollation("NOCASE");
            entity.Property(item => item.Subject).HasMaxLength(1000);
        });

        modelBuilder.Entity<NotificationReadReceipt>(entity =>
        {
            entity.ToTable("NotificationReadReceipts");
            entity.HasKey(item => item.NotificationId);
            entity.Property(item => item.Recipient).HasMaxLength(320).UseCollation("NOCASE");
            entity.HasIndex(item => new { item.Recipient, item.ReadAtUtc });
        });
    }
}
