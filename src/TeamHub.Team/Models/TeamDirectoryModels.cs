namespace TeamHub.Team;

public sealed class TeamMemberDto
{
    public string Id { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Gid { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ContactNumber { get; set; } = string.Empty;
    public string Section { get; set; } = TeamMemberSections.TeamMember;
}

public static class TeamMemberSections
{
    public const string TeamMember = "TeamMember";
    public const string Management = "Management";

    public static string Normalize(string? section) =>
        string.Equals(section?.Trim(), Management, StringComparison.OrdinalIgnoreCase) ? Management : TeamMember;
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
    public string Impact { get; set; } = string.Empty;
    public string AchievedBy { get; set; } = string.Empty;
    public DateTime AchievedOn { get; set; }
}

public sealed class ColumnTextAppearanceDto
{
    public string ColumnKey { get; set; } = string.Empty;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
}

public sealed class PageTextAppearanceDto
{
    public string PageKey { get; set; } = string.Empty;
    public IReadOnlyList<ColumnTextAppearanceDto> Columns { get; set; } = [];

    public string CssClass(string columnKey)
    {
        var appearance = Columns.FirstOrDefault(column =>
            string.Equals(column.ColumnKey, columnKey, StringComparison.OrdinalIgnoreCase));
        if (appearance is null)
        {
            return string.Empty;
        }

        var classes = new List<string> { "configured-text" };
        if (appearance?.IsBold == true)
        {
            classes.Add("configured-text-bold");
        }

        if (appearance?.IsItalic == true)
        {
            classes.Add("configured-text-italic");
        }

        return string.Join(' ', classes);
    }
}

public sealed record TeamPageColumnDefinition(string Key, string Label);

public sealed record TeamPageAppearanceDefinition(
    string Key,
    string Label,
    IReadOnlyList<TeamPageColumnDefinition> Columns);

public static class TeamPageAppearanceCatalog
{
    public const string Directory = "team-directory";
    public const string Achievements = "team-achievements";
    public const string ResponsibilityMatrix = "responsibility-matrix";

    public static IReadOnlyList<TeamPageAppearanceDefinition> Pages { get; } = CreatePages();

    public static TeamPageAppearanceDefinition? Find(string? pageKey) => Pages.FirstOrDefault(page =>
        string.Equals(page.Key, pageKey, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<TeamPageAppearanceDefinition> CreatePages()
    {
        return new TeamPageAppearanceDefinition[]
        {
            CreateDirectoryPage(),
            CreateAchievementsPage(),
            CreateResponsibilityMatrixPage()
        };
    }

    private static TeamPageAppearanceDefinition CreateDirectoryPage() => new(
        Directory,
        "Team Directory",
        new List<TeamPageColumnDefinition>
        {
            new("employee-name", "Employee Name"),
            new("role", "Role"),
            new("gid", "GID"),
            new("email", "Email"),
            new("contact-number", "Contact Number")
        });

    private static TeamPageAppearanceDefinition CreateAchievementsPage() => new(
        Achievements,
        "Achievements",
        new List<TeamPageColumnDefinition>
        {
            new("achieved-on", "Achievement Date"),
            new("title", "Title"),
            new("description", "Description"),
            new("impact", "Impact"),
            new("achieved-by", "Achieved By")
        });

    private static TeamPageAppearanceDefinition CreateResponsibilityMatrixPage() => new(
        ResponsibilityMatrix,
        "Responsibility Matrix",
        new List<TeamPageColumnDefinition>
        {
            new("pod", "Pod"),
            new("focus-areas", "Sub-Categories / Focus Areas"),
            new("members", "Engineers / Members")
        });
}
