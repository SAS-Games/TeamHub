namespace TeamHub.Team;

public sealed class TeamMemberDto
{
    public string Id { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Gid { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ContactNumber { get; set; } = string.Empty;
}

public sealed class SpecializationDto
{
    public string Id { get; set; } = string.Empty;
    public string Pod { get; set; } = string.Empty;
    public string FocusAreas { get; set; } = string.Empty;
    public string Members { get; set; } = string.Empty;
}

public sealed class TeamDirectoryDto
{
    public IReadOnlyList<TeamMemberDto> Members { get; set; } = [];
    public IReadOnlyList<SpecializationDto> Specializations { get; set; } = [];
}

public sealed class TeamAchievementDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AchievedBy { get; set; } = string.Empty;
    public DateTime AchievedOn { get; set; }
}
