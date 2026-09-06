using Microsoft.EntityFrameworkCore;

namespace TeamHub.Team;

internal sealed class SqliteTeamDirectoryService(TeamDbContext dbContext)
    : ITeamDirectoryService, ITeamConfigurationService
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
                ContactNumber = member.ContactNumber
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
        ContactNumber = member.ContactNumber
    };

    private static SpecializationDto ToDto(SupportSpecializationRecord specialization) => new()
    {
        Id = specialization.Id.ToString(),
        Pod = specialization.Pod,
        FocusAreas = specialization.FocusAreas,
        Members = specialization.Members
    };
}
