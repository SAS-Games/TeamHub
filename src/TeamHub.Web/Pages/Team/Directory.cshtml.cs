using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Team;

namespace TeamHub.Web.Pages.Team;

public class DirectoryModel(
    ITeamDirectoryService teamDirectoryService,
    IPageTextAppearanceService appearanceService) : PageModel
{
    public TeamDirectoryDto Directory { get; private set; } = new();
    public PageTextAppearanceDto Appearance { get; private set; } = new();
    public IReadOnlyList<MemberSection> MemberSections { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Directory = await teamDirectoryService.GetTeamDirectoryAsync(cancellationToken);
        Appearance = await appearanceService.GetPageAppearanceAsync(
            TeamPageAppearanceCatalog.Directory,
            cancellationToken);
        MemberSections =
        [
            new("Team Members", Directory.Members
                .Where(member => member.Section == TeamMemberSections.TeamMember).ToList()),
            new("Management", Directory.Members
                .Where(member => member.Section == TeamMemberSections.Management).ToList())
        ];
    }

    public sealed record MemberSection(string Title, IReadOnlyList<TeamMemberDto> Members);
}
