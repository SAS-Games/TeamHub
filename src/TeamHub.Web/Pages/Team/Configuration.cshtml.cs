using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Team;

namespace TeamHub.Web.Pages.Team;

[Authorize(Roles = "Admin")]
public sealed class ConfigurationModel(
    ITeamDirectoryService teamDirectoryService,
    ITeamConfigurationService teamConfigurationService) : PageModel
{
    public TeamMemberInput MemberInput { get; set; } = new();

    public SpecializationInput SupportInput { get; set; } = new();

    public TeamDirectoryDto Directory { get; private set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(
        string? memberId = null,
        string? specializationId = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(memberId))
        {
            var member = await teamConfigurationService.GetTeamMemberAsync(memberId, cancellationToken);
            if (member is null)
            {
                return NotFound();
            }

            MemberInput = TeamMemberInput.FromDto(member);
        }

        if (!string.IsNullOrWhiteSpace(specializationId))
        {
            var specialization = await teamConfigurationService.GetSpecializationAsync(specializationId, cancellationToken);
            if (specialization is null)
            {
                return NotFound();
            }

            SupportInput = SpecializationInput.FromDto(specialization);
        }

        await LoadDirectoryAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveMemberAsync(
        [Bind(Prefix = nameof(MemberInput))] TeamMemberInput input,
        CancellationToken cancellationToken)
    {
        MemberInput = input;
        if (!TryValidateModel(MemberInput, nameof(MemberInput)))
        {
            await LoadDirectoryAsync(cancellationToken);
            return Page();
        }

        var saved = await teamConfigurationService.SaveTeamMemberAsync(MemberInput.ToDto(), cancellationToken);
        StatusMessage = $"Team member saved: {saved.EmployeeName}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteMemberAsync(string memberId, CancellationToken cancellationToken)
    {
        await teamConfigurationService.DeleteTeamMemberAsync(memberId, cancellationToken);
        StatusMessage = "Team member deleted.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveSpecializationAsync(
        [Bind(Prefix = nameof(SupportInput))] SpecializationInput input,
        CancellationToken cancellationToken)
    {
        SupportInput = input;
        if (!TryValidateModel(SupportInput, nameof(SupportInput)))
        {
            await LoadDirectoryAsync(cancellationToken);
            return Page();
        }

        var saved = await teamConfigurationService.SaveSpecializationAsync(SupportInput.ToDto(), cancellationToken);
        StatusMessage = $"Support specialization saved: {saved.Pod}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteSpecializationAsync(string specializationId, CancellationToken cancellationToken)
    {
        await teamConfigurationService.DeleteSpecializationAsync(specializationId, cancellationToken);
        StatusMessage = "Support specialization deleted.";
        return RedirectToPage();
    }

    private async Task LoadDirectoryAsync(CancellationToken cancellationToken) =>
        Directory = await teamDirectoryService.GetTeamDirectoryAsync(cancellationToken);

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
}
