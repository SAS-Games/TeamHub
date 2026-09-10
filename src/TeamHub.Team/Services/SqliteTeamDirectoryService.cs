using Microsoft.EntityFrameworkCore;

namespace TeamHub.Team;

internal sealed class SqliteTeamDirectoryService(TeamDbContext dbContext)
    : ITeamDirectoryService, ITeamConfigurationService, ITeamAchievementService, IPageTextAppearanceService
{
    public async Task<TeamDirectoryDto> GetTeamDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var members = await dbContext.TeamMembers
            .AsNoTracking()
            .OrderBy(member => member.EmployeeName)
            .Select(member => new TeamMemberDto
            {
                Id = member.Id.ToString(),
                EmployeeName = member.EmployeeName,
                Role = member.Role,
                Gid = member.Gid,
                Email = member.Email,
                ContactNumber = member.ContactNumber,
                Section = member.Section == TeamMemberSections.Management
                    ? TeamMemberSections.Management
                    : TeamMemberSections.TeamMember
            })
            .ToListAsync(cancellationToken);

        var specializations = await dbContext.SupportSpecializations
            .AsNoTracking()
            .OrderBy(specialization => specialization.Pod)
            .Select(specialization => new SpecializationDto
            {
                Id = specialization.Id.ToString(),
                Pod = specialization.Pod,
                FocusAreas = specialization.FocusAreas,
                Members = specialization.Members
            })
            .ToListAsync(cancellationToken);

        return new TeamDirectoryDto
        {
            Members = members,
            Specializations = specializations
        };
    }

    public async Task<TeamMemberDto?> GetTeamMemberAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var memberId))
        {
            return null;
        }

        var member = await dbContext.TeamMembers.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == memberId, cancellationToken);
        return member is null ? null : ToDto(member);
    }

    public async Task<TeamMemberDto> SaveTeamMemberAsync(TeamMemberDto member, CancellationToken cancellationToken = default)
    {
        var record = await FindOrCreateMemberAsync(member.Id, cancellationToken);
        record.EmployeeName = member.EmployeeName.Trim();
        record.Role = member.Role.Trim();
        record.Gid = member.Gid.Trim();
        record.Email = member.Email.Trim();
        record.ContactNumber = member.ContactNumber.Trim();
        record.Section = TeamMemberSections.Normalize(member.Section);
        record.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(record);
    }

    public async Task DeleteTeamMemberAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var memberId))
        {
            return;
        }

        var record = await dbContext.TeamMembers.FindAsync([memberId], cancellationToken);
        if (record is null)
        {
            return;
        }

        dbContext.TeamMembers.Remove(record);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SpecializationDto?> GetSpecializationAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var specializationId))
        {
            return null;
        }

        var specialization = await dbContext.SupportSpecializations.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == specializationId, cancellationToken);
        return specialization is null ? null : ToDto(specialization);
    }

    public async Task<SpecializationDto> SaveSpecializationAsync(SpecializationDto specialization, CancellationToken cancellationToken = default)
    {
        var record = await FindOrCreateSpecializationAsync(specialization.Id, cancellationToken);
        record.Pod = specialization.Pod.Trim();
        record.FocusAreas = specialization.FocusAreas.Trim();
        record.Members = specialization.Members.Trim();
        record.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(record);
    }

    public async Task DeleteSpecializationAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var specializationId))
        {
            return;
        }

        var record = await dbContext.SupportSpecializations.FindAsync([specializationId], cancellationToken);
        if (record is null)
        {
            return;
        }

        dbContext.SupportSpecializations.Remove(record);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TeamAchievementDto>> GetAchievementsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.TeamAchievements
            .AsNoTracking()
            .OrderByDescending(achievement => achievement.AchievedOn)
            .ThenByDescending(achievement => achievement.CreatedAtUtc)
            .Select(achievement => new TeamAchievementDto
            {
                Id = achievement.Id.ToString(),
                Title = achievement.Title,
                Description = achievement.Description,
                AchievedBy = achievement.AchievedBy,
                AchievedOn = achievement.AchievedOn
            })
            .ToListAsync(cancellationToken);

    public async Task<TeamAchievementDto> AddAchievementAsync(
        TeamAchievementDto achievement,
        CancellationToken cancellationToken = default)
    {
        var record = new TeamAchievementRecord
        {
            Title = achievement.Title.Trim(),
            Description = achievement.Description.Trim(),
            AchievedBy = achievement.AchievedBy.Trim(),
            AchievedOn = achievement.AchievedOn.Date
        };
        dbContext.TeamAchievements.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(record);
    }

    public async Task<PageTextAppearanceDto> GetPageAppearanceAsync(
        string pageKey,
        CancellationToken cancellationToken = default)
    {
        var normalizedPageKey = pageKey.Trim().ToLowerInvariant();
        var columns = await dbContext.PageTextAppearances
            .AsNoTracking()
            .Where(appearance => appearance.PageKey == normalizedPageKey)
            .OrderBy(appearance => appearance.ColumnKey)
            .Select(appearance => new ColumnTextAppearanceDto
            {
                ColumnKey = appearance.ColumnKey,
                IsBold = appearance.IsBold,
                IsItalic = appearance.IsItalic
            })
            .ToListAsync(cancellationToken);

        return new PageTextAppearanceDto
        {
            PageKey = normalizedPageKey,
            Columns = columns
        };
    }

    public async Task SavePageAppearanceAsync(
        PageTextAppearanceDto appearance,
        CancellationToken cancellationToken = default)
    {
        var pageKey = appearance.PageKey.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(pageKey))
        {
            throw new ArgumentException("A page key is required.", nameof(appearance));
        }

        var existing = await dbContext.PageTextAppearances
            .Where(item => item.PageKey == pageKey)
            .ToListAsync(cancellationToken);
        dbContext.PageTextAppearances.RemoveRange(existing);

        foreach (var column in appearance.Columns
                     .Where(column => !string.IsNullOrWhiteSpace(column.ColumnKey))
                     .DistinctBy(column => column.ColumnKey, StringComparer.OrdinalIgnoreCase))
        {
            dbContext.PageTextAppearances.Add(new PageTextAppearanceRecord
            {
                PageKey = pageKey,
                ColumnKey = column.ColumnKey.Trim().ToLowerInvariant(),
                IsBold = column.IsBold,
                IsItalic = column.IsItalic,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<TeamMemberRecord> FindOrCreateMemberAsync(string id, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(id, out var memberId))
        {
            var existing = await dbContext.TeamMembers.FirstOrDefaultAsync(item => item.Id == memberId, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        var record = new TeamMemberRecord();
        dbContext.TeamMembers.Add(record);
        return record;
    }

    private async Task<SupportSpecializationRecord> FindOrCreateSpecializationAsync(string id, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(id, out var specializationId))
        {
            var existing = await dbContext.SupportSpecializations
                .FirstOrDefaultAsync(item => item.Id == specializationId, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        var record = new SupportSpecializationRecord();
        dbContext.SupportSpecializations.Add(record);
        return record;
    }

    private static TeamMemberDto ToDto(TeamMemberRecord member) => new()
    {
        Id = member.Id.ToString(),
        EmployeeName = member.EmployeeName,
        Role = member.Role,
        Gid = member.Gid,
        Email = member.Email,
        ContactNumber = member.ContactNumber,
        Section = member.Section
    };

    private static SpecializationDto ToDto(SupportSpecializationRecord specialization) => new()
    {
        Id = specialization.Id.ToString(),
        Pod = specialization.Pod,
        FocusAreas = specialization.FocusAreas,
        Members = specialization.Members
    };

    private static TeamAchievementDto ToDto(TeamAchievementRecord achievement) => new()
    {
        Id = achievement.Id.ToString(),
        Title = achievement.Title,
        Description = achievement.Description,
        AchievedBy = achievement.AchievedBy,
        AchievedOn = achievement.AchievedOn
    };
}
