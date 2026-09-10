namespace TeamHub.Team;

public interface ITeamDirectoryService
{
    Task<TeamDirectoryDto> GetTeamDirectoryAsync(CancellationToken cancellationToken = default);
}

public interface ITeamConfigurationService
{
    Task<TeamMemberDto?> GetTeamMemberAsync(string id, CancellationToken cancellationToken = default);
    Task<TeamMemberDto> SaveTeamMemberAsync(TeamMemberDto member, CancellationToken cancellationToken = default);
    Task DeleteTeamMemberAsync(string id, CancellationToken cancellationToken = default);
    Task<SpecializationDto?> GetSpecializationAsync(string id, CancellationToken cancellationToken = default);
    Task<SpecializationDto> SaveSpecializationAsync(SpecializationDto specialization, CancellationToken cancellationToken = default);
    Task DeleteSpecializationAsync(string id, CancellationToken cancellationToken = default);
}

public interface ITeamAchievementService
{
    Task<IReadOnlyList<TeamAchievementDto>> GetAchievementsAsync(CancellationToken cancellationToken = default);
    Task<TeamAchievementDto> AddAchievementAsync(TeamAchievementDto achievement, CancellationToken cancellationToken = default);
}

public interface IPageTextAppearanceService
{
    Task<PageTextAppearanceDto> GetPageAppearanceAsync(string pageKey, CancellationToken cancellationToken = default);
    Task SavePageAppearanceAsync(PageTextAppearanceDto appearance, CancellationToken cancellationToken = default);
}

public interface ITeamDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
