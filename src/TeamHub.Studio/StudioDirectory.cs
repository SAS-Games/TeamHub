using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TeamHub.Studio;

public sealed class StudioDetails
{
    public string Id { get; set; } = string.Empty;
    public string StudioName { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public List<StudioTeamMember> TeamMembers { get; set; } = [];
    public List<StudioDevelopmentTool> DevelopmentTools { get; set; } = [];
    public List<StudioImportantLink> ImportantLinks { get; set; } = [];
}

public sealed class StudioTeamMember
{
    public string Name { get; set; } = string.Empty;
    public string RolesAndResponsibilities { get; set; } = string.Empty;
    public string? EmailId { get; set; }
}

public sealed class StudioDevelopmentTool
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class StudioImportantLink
{
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public interface IStudioDirectoryService
{
    Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default);
    Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default);
    Task<StudioDetails> SaveStudioAsync(StudioDetails studio, CancellationToken cancellationToken = default);
    Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default);
}

public interface IStudioDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

internal sealed class StudioRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StudioName { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<StudioTeamMemberRecord> TeamMembers { get; set; } = new List<StudioTeamMemberRecord>();
    public ICollection<StudioDevelopmentToolsRecord> DevelopmentTools { get; set; } = new List<StudioDevelopmentToolsRecord>();
    public ICollection<StudioImportantLinkRecord> ImportantLinks { get; set; } = new List<StudioImportantLinkRecord>();
}

internal sealed class StudioTeamMemberRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudioRecordId { get; set; }
    public int DisplayOrder { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RolesAndResponsibilities { get; set; } = string.Empty;
    public string? EmailId { get; set; }
    public StudioRecord Studio { get; set; } = null!;
}

internal sealed class StudioDevelopmentToolsRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudioRecordId { get; set; }
    public int DisplayOrder { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public StudioRecord Studio { get; set; } = null!;
}

internal sealed class StudioImportantLinkRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudioRecordId { get; set; }
    public int DisplayOrder { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public StudioRecord Studio { get; set; } = null!;
}

internal sealed class StudioDbContext(DbContextOptions<StudioDbContext> options) : DbContext(options)
{
    public DbSet<StudioRecord> Studios => Set<StudioRecord>();
    public DbSet<StudioTeamMemberRecord> StudioTeamMembers => Set<StudioTeamMemberRecord>();
    public DbSet<StudioDevelopmentToolsRecord> StudioDevelopmentTools => Set<StudioDevelopmentToolsRecord>();
    public DbSet<StudioImportantLinkRecord> StudioImportantLinks => Set<StudioImportantLinkRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StudioRecord>(entity =>
        {
            entity.ToTable("Studios");
            entity.Property(x => x.StudioName).HasMaxLength(256);
            entity.Property(x => x.ProjectName).HasMaxLength(256);
            entity.Property(x => x.Location).HasMaxLength(256);
            entity.HasIndex(x => new { x.StudioName, x.ProjectName }).IsUnique();
        });

        modelBuilder.Entity<StudioTeamMemberRecord>(entity =>
        {
            entity.ToTable("StudioTeamMembers");
            entity.Property(x => x.Name).HasMaxLength(256);
            entity.Property(x => x.EmailId).HasMaxLength(256);
            entity.HasIndex(x => new { x.StudioRecordId, x.DisplayOrder });
            entity.HasOne(x => x.Studio)
                .WithMany(x => x.TeamMembers)
                .HasForeignKey(x => x.StudioRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StudioDevelopmentToolsRecord>(entity =>
        {
            entity.ToTable("StudioDevelopmentTools");
            entity.Property(x => x.Name).HasMaxLength(256);
            entity.HasIndex(x => new { x.StudioRecordId, x.DisplayOrder });
            entity.HasOne(x => x.Studio)
                .WithMany(x => x.DevelopmentTools)
                .HasForeignKey(x => x.StudioRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StudioImportantLinkRecord>(entity =>
        {
            entity.ToTable("StudioImportantLinks");
            entity.Property(x => x.Label).HasMaxLength(256);
            entity.Property(x => x.Url).HasMaxLength(1024);
            entity.HasIndex(x => new { x.StudioRecordId, x.DisplayOrder });
            entity.HasOne(x => x.Studio)
                .WithMany(x => x.ImportantLinks)
                .HasForeignKey(x => x.StudioRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

internal sealed class SqliteStudioDatabaseInitializer(StudioDbContext dbContext) : IStudioDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS Studios (
                    Id TEXT NOT NULL CONSTRAINT PK_Studios PRIMARY KEY,
                    StudioName TEXT NOT NULL,
                    ProjectName TEXT NOT NULL,
                    Location TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS IX_Studios_StudioName_ProjectName
                ON Studios (StudioName, ProjectName);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS StudioTeamMembers (
                    Id TEXT NOT NULL CONSTRAINT PK_StudioTeamMembers PRIMARY KEY,
                    StudioRecordId TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    RolesAndResponsibilities TEXT NOT NULL,
                    EmailId TEXT NULL,
                    CONSTRAINT FK_StudioTeamMembers_Studios_StudioRecordId
                        FOREIGN KEY (StudioRecordId) REFERENCES Studios (Id) ON DELETE CASCADE
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_StudioTeamMembers_StudioRecordId_DisplayOrder
                ON StudioTeamMembers (StudioRecordId, DisplayOrder);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS StudioDevelopmentTools (
                    Id TEXT NOT NULL CONSTRAINT PK_StudioDevelopmentTools PRIMARY KEY,
                    StudioRecordId TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    Name TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    CONSTRAINT FK_StudioDevelopmentTools_Studios_StudioRecordId
                        FOREIGN KEY (StudioRecordId) REFERENCES Studios (Id) ON DELETE CASCADE
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_StudioDevelopmentTools_StudioRecordId_DisplayOrder
                ON StudioDevelopmentTools (StudioRecordId, DisplayOrder);
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS StudioImportantLinks (
                    Id TEXT NOT NULL CONSTRAINT PK_StudioImportantLinks PRIMARY KEY,
                    StudioRecordId TEXT NOT NULL,
                    DisplayOrder INTEGER NOT NULL,
                    Label TEXT NOT NULL,
                    Url TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    CONSTRAINT FK_StudioImportantLinks_Studios_StudioRecordId
                        FOREIGN KEY (StudioRecordId) REFERENCES Studios (Id) ON DELETE CASCADE
                );
                """, cancellationToken);

            await dbContext.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS IX_StudioImportantLinks_StudioRecordId_DisplayOrder
                ON StudioImportantLinks (StudioRecordId, DisplayOrder);
                """, cancellationToken);

        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }
}

internal sealed class SqliteStudioDirectoryService(StudioDbContext dbContext) : IStudioDirectoryService
{
    public async Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default)
    {
        var studios = await dbContext.Studios
            .AsNoTracking()
            .Include(studio => studio.TeamMembers)
            .Include(studio => studio.DevelopmentTools)
            .Include(studio => studio.ImportantLinks)
            .OrderBy(studio => studio.StudioName)
            .ToListAsync(cancellationToken);

        return studios.Select(ToDetails).ToList();
    }

    public async Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var studioId))
        {
            return null;
        }

        var studio = await dbContext.Studios
            .AsNoTracking()
            .Include(item => item.TeamMembers)
            .Include(item => item.DevelopmentTools)
            .Include(item => item.ImportantLinks)
            .FirstOrDefaultAsync(item => item.Id == studioId, cancellationToken);

        return studio is null ? null : ToDetails(studio);
    }

    public async Task<StudioDetails> SaveStudioAsync(StudioDetails studio, CancellationToken cancellationToken = default)
    {
        StudioRecord record;
        if (Guid.TryParse(studio.Id, out var studioId))
        {
            record = await dbContext.Studios
                .FirstOrDefaultAsync(item => item.Id == studioId, cancellationToken)
                ?? new StudioRecord { Id = studioId, CreatedAtUtc = DateTime.UtcNow };
        }
        else
        {
            record = new StudioRecord();
            dbContext.Studios.Add(record);
        }

        record.StudioName = studio.StudioName.Trim();
        record.ProjectName = studio.ProjectName.Trim();
        record.Location = studio.Location.Trim();
        record.UpdatedAtUtc = DateTime.UtcNow;

        if (dbContext.Entry(record).State == EntityState.Detached)
        {
            dbContext.Studios.Add(record);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await dbContext.StudioTeamMembers
            .Where(member => member.StudioRecordId == record.Id)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.StudioDevelopmentTools
            .Where(tool => tool.StudioRecordId == record.Id)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.StudioImportantLinks
            .Where(link => link.StudioRecordId == record.Id)
            .ExecuteDeleteAsync(cancellationToken);

        dbContext.StudioTeamMembers.AddRange(CreateTeamMemberRecords(record.Id, studio.TeamMembers));
        dbContext.StudioDevelopmentTools.AddRange(CreateDevelopmentToolsRecords(record.Id, studio.DevelopmentTools));
        dbContext.StudioImportantLinks.AddRange(CreateImportantLinkRecords(record.Id, studio.ImportantLinks));
        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetStudioAsync(record.Id.ToString(), cancellationToken) ?? ToDetails(record);
    }

    public async Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var studioId))
        {
            return;
        }

        var studio = await dbContext.Studios.FindAsync([studioId], cancellationToken);
        if (studio is null)
        {
            return;
        }

        dbContext.Studios.Remove(studio);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static StudioDetails ToDetails(StudioRecord studio)
    {
        return new StudioDetails
        {
            Id = studio.Id.ToString(),
            StudioName = studio.StudioName,
            ProjectName = studio.ProjectName,
            Location = studio.Location,
            TeamMembers = studio.TeamMembers
                .OrderBy(member => member.DisplayOrder)
                .Select(member => new StudioTeamMember
                {
                    Name = member.Name,
                    RolesAndResponsibilities = member.RolesAndResponsibilities,
                    EmailId = member.EmailId
                })
                .ToList(),
            DevelopmentTools = studio.DevelopmentTools
                .OrderBy(tool => tool.DisplayOrder)
                .Select(tool => new StudioDevelopmentTool
                {
                    Name = tool.Name,
                    Description = tool.Description
                })
                .ToList(),
            ImportantLinks = studio.ImportantLinks
                .OrderBy(link => link.DisplayOrder)
                .Select(link => new StudioImportantLink
                {
                    Label = link.Label,
                    Url = link.Url,
                    Description = link.Description
                })
                .ToList()
        };
    }

    private static IEnumerable<StudioTeamMemberRecord> CreateTeamMemberRecords(Guid studioRecordId, IEnumerable<StudioTeamMember> teamMembers)
    {
        var displayOrder = 0;
        foreach (var member in teamMembers.Where(HasTeamMemberValue))
        {
            yield return new StudioTeamMemberRecord
            {
                StudioRecordId = studioRecordId,
                DisplayOrder = displayOrder,
                Name = member.Name.Trim(),
                RolesAndResponsibilities = member.RolesAndResponsibilities.Trim(),
                EmailId = string.IsNullOrWhiteSpace(member.EmailId) ? null : member.EmailId.Trim()
            };
            displayOrder++;
        }
    }

    private static IEnumerable<StudioDevelopmentToolsRecord> CreateDevelopmentToolsRecords(Guid studioRecordId, IEnumerable<StudioDevelopmentTool> developmentTools)
    {
        var displayOrder = 0;
        foreach (var tool in developmentTools.Where(HasDevelopmentToolValue))
        {
            yield return new StudioDevelopmentToolsRecord
            {
                StudioRecordId = studioRecordId,
                DisplayOrder = displayOrder,
                Name = tool.Name.Trim(),
                Description = tool.Description.Trim()
            };
            displayOrder++;
        }
    }

    private static IEnumerable<StudioImportantLinkRecord> CreateImportantLinkRecords(Guid studioRecordId, IEnumerable<StudioImportantLink> importantLinks)
    {
        var displayOrder = 0;
        foreach (var link in importantLinks.Where(HasImportantLinkValue))
        {
            yield return new StudioImportantLinkRecord
            {
                StudioRecordId = studioRecordId,
                DisplayOrder = displayOrder,
                Label = link.Label.Trim(),
                Url = link.Url.Trim(),
                Description = link.Description.Trim()
            };
            displayOrder++;
        }
    }

    private static bool HasTeamMemberValue(StudioTeamMember member)
        => !string.IsNullOrWhiteSpace(member.Name)
            || !string.IsNullOrWhiteSpace(member.RolesAndResponsibilities)
            || !string.IsNullOrWhiteSpace(member.EmailId);

    private static bool HasImportantLinkValue(StudioImportantLink link)
        => !string.IsNullOrWhiteSpace(link.Label)
            || !string.IsNullOrWhiteSpace(link.Url)
            || !string.IsNullOrWhiteSpace(link.Description);
    private static bool HasDevelopmentToolValue(StudioDevelopmentTool tool)
        => !string.IsNullOrWhiteSpace(tool.Name)
            || !string.IsNullOrWhiteSpace(tool.Description);
}

public static class StudioDirectoryServiceCollectionExtensions
{
    public static IServiceCollection AddStudioDirectory(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration);
        services.AddDbContext<StudioDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("WorkflowDb") ?? "Data Source=data/workflow.db";
            options.UseSqlite(connectionString);
        });
        services.AddScoped<IStudioDatabaseInitializer, SqliteStudioDatabaseInitializer>();
        services.AddScoped<IStudioDirectoryService, SqliteStudioDirectoryService>();
        services.AddHttpClient<IStudioJiraTicketService, JiraStudioTicketService>();
        return services;
    }
}