using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Team;

namespace TeamHub.Web.Pages.Team;

[Authorize(Roles = "Admin")]
public sealed class ConfigurationModel(
    ITeamDirectoryService teamDirectoryService,
    ITeamConfigurationService teamConfigurationService,
    ITeamAchievementService achievementService) : PageModel
{
    private const string MembersSection = "members";
    private const string SpecializationsSection = "specializations";
    private const string AchievementsSection = "achievements";

    public TeamMemberInput MemberInput { get; set; } = new();

    public SpecializationInput SupportInput { get; set; } = new();

    public AchievementInput AchievementForm { get; set; } = new();

    public TeamDirectoryDto Directory { get; private set; } = new();

    public IReadOnlyList<TeamAchievementDto> Achievements { get; private set; } = [];

    public string ActiveSection { get; private set; } = MembersSection;

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(
        string? section = null,
        string? memberId = null,
        string? specializationId = null,
        CancellationToken cancellationToken = default)
    {
        ActiveSection = NormalizeSection(section);

        if (!string.IsNullOrWhiteSpace(memberId))
        {
            var member = await teamConfigurationService.GetTeamMemberAsync(memberId, cancellationToken);
            if (member is null)
            {
                return NotFound();
            }

            MemberInput = TeamMemberInput.FromDto(member);
            ActiveSection = MembersSection;
        }

        if (!string.IsNullOrWhiteSpace(specializationId))
        {
            var specialization = await teamConfigurationService.GetSpecializationAsync(specializationId, cancellationToken);
            if (specialization is null)
            {
                return NotFound();
            }

            SupportInput = SpecializationInput.FromDto(specialization);
            ActiveSection = SpecializationsSection;
        }

        await LoadConfigurationAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveMemberAsync(
        [Bind(Prefix = nameof(MemberInput))] TeamMemberInput input,
        CancellationToken cancellationToken)
    {
        ActiveSection = MembersSection;
        MemberInput = input;
        if (!TryValidateModel(MemberInput, nameof(MemberInput)))
        {
            await LoadConfigurationAsync(cancellationToken);
            return Page();
        }

        var saved = await teamConfigurationService.SaveTeamMemberAsync(MemberInput.ToDto(), cancellationToken);
        StatusMessage = $"Team member saved: {saved.EmployeeName}.";
        return RedirectToPage(new { section = MembersSection });
    }

    public async Task<IActionResult> OnPostDeleteMemberAsync(string memberId, CancellationToken cancellationToken)
    {
        await teamConfigurationService.DeleteTeamMemberAsync(memberId, cancellationToken);
        StatusMessage = "Team member deleted.";
        return RedirectToPage(new { section = MembersSection });
    }

    public async Task<IActionResult> OnPostSaveSpecializationAsync(
        [Bind(Prefix = nameof(SupportInput))] SpecializationInput input,
        CancellationToken cancellationToken)
    {
        ActiveSection = SpecializationsSection;
        SupportInput = input;
        if (!TryValidateModel(SupportInput, nameof(SupportInput)))
        {
            await LoadConfigurationAsync(cancellationToken);
            return Page();
        }

        var saved = await teamConfigurationService.SaveSpecializationAsync(SupportInput.ToDto(), cancellationToken);
        StatusMessage = $"Support specialization saved: {saved.Pod}.";
        return RedirectToPage(new { section = SpecializationsSection });
    }

    public async Task<IActionResult> OnPostDeleteSpecializationAsync(string specializationId, CancellationToken cancellationToken)
    {
        await teamConfigurationService.DeleteSpecializationAsync(specializationId, cancellationToken);
        StatusMessage = "Support specialization deleted.";
        return RedirectToPage(new { section = SpecializationsSection });
    }

    public async Task<IActionResult> OnPostSaveAchievementAsync(
        [Bind(Prefix = nameof(AchievementForm))] AchievementInput input,
        CancellationToken cancellationToken)
    {
        ActiveSection = AchievementsSection;
        AchievementForm = input;
        if (AchievementForm.AchievedOn.Date > DateTime.Today)
        {
            ModelState.AddModelError("AchievementForm.AchievedOn", "Achievement date cannot be in the future.");
        }

        if (!TryValidateModel(AchievementForm, nameof(AchievementForm)))
        {
            await LoadConfigurationAsync(cancellationToken);
            return Page();
        }

        var saved = await achievementService.AddAchievementAsync(AchievementForm.ToDto(), cancellationToken);
        StatusMessage = $"Achievement added: {saved.Title}.";
        return RedirectToPage(new { section = AchievementsSection });
    }

    private async Task LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        Directory = await teamDirectoryService.GetTeamDirectoryAsync(cancellationToken);
        Achievements = await achievementService.GetAchievementsAsync(cancellationToken);
    }

    private static string NormalizeSection(string? section) => section?.ToLowerInvariant() switch
    {
        SpecializationsSection => SpecializationsSection,
        AchievementsSection => AchievementsSection,
        _ => MembersSection
    };

    public sealed class TeamMemberInput
    {
        public string? Id { get; set; }

        [Required, StringLength(256)]
        public string EmployeeName { get; set; } = string.Empty;

        [StringLength(256)]
        public string? Role { get; set; }

        [StringLength(128)]
        public string? Gid { get; set; }

        [EmailAddress, StringLength(256)]
        public string? Email { get; set; }

        [StringLength(128)]
        public string? ContactNumber { get; set; }

        public TeamMemberDto ToDto() => new()
        {
            Id = Id ?? string.Empty,
            EmployeeName = EmployeeName,
            Role = Role ?? string.Empty,
            Gid = Gid ?? string.Empty,
            Email = Email ?? string.Empty,
            ContactNumber = ContactNumber ?? string.Empty
        };

        public static TeamMemberInput FromDto(TeamMemberDto member) => new()
        {
            Id = member.Id,
            EmployeeName = member.EmployeeName,
            Role = member.Role,
            Gid = member.Gid,
            Email = member.Email,
            ContactNumber = member.ContactNumber
        };
    }

    public sealed class SpecializationInput
    {
        public string? Id { get; set; }

        [Required, StringLength(256)]
        public string Pod { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? FocusAreas { get; set; }

        [StringLength(2000)]
        public string? Members { get; set; }

        public SpecializationDto ToDto() => new()
        {
            Id = Id ?? string.Empty,
            Pod = Pod,
            FocusAreas = FocusAreas ?? string.Empty,
            Members = Members ?? string.Empty
        };

        public static SpecializationInput FromDto(SpecializationDto specialization) => new()
        {
            Id = specialization.Id,
            Pod = specialization.Pod,
            FocusAreas = specialization.FocusAreas,
            Members = specialization.Members
        };
    }

    public sealed class AchievementInput
    {
        [Required, StringLength(256)]
        public string Title { get; set; } = string.Empty;

        [Required, StringLength(2000)]
        public string Description { get; set; } = string.Empty;

        [Required, StringLength(512)]
        public string AchievedBy { get; set; } = string.Empty;

        [Required, DataType(DataType.Date)]
        public DateTime AchievedOn { get; set; } = DateTime.Today;

        public TeamAchievementDto ToDto() => new()
        {
            Title = Title,
            Description = Description,
            AchievedBy = AchievedBy,
            AchievedOn = AchievedOn
        };
    }
}
