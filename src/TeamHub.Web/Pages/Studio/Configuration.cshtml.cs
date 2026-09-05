using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Studio;

namespace TeamHub.Web.Pages.Studio;

[Authorize(Roles = "Admin")]
public class ConfigurationModel(IStudioDirectoryService studioDirectoryService) : PageModel
{
    [BindProperty]
    public StudioInput Input { get; set; } = new();

    public IReadOnlyList<StudioDetails> Studios { get; private set; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    public bool ShowForm { get; private set; }

    public async Task OnGetAsync(string? studioId = null, bool create = false, CancellationToken cancellationToken = default)
    {
        await LoadStudiosAsync(cancellationToken);
        ShowForm = create || !string.IsNullOrWhiteSpace(studioId);

        if (!string.IsNullOrWhiteSpace(studioId))
        {
            var studio = await studioDirectoryService.GetStudioAsync(studioId, cancellationToken);
            if (studio is not null)
            {
                Input = StudioInput.FromStudio(studio);
            }
        }

        EnsureEditableRows();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        ShowForm = true;
        NormalizeInputRows();
        if (!ModelState.IsValid)
        {
            await LoadStudiosAsync(cancellationToken);
            EnsureEditableRows();
            return Page();
        }

        try
        {
            var saved = await studioDirectoryService.SaveStudioAsync(Input.ToStudio(), cancellationToken);
            StatusMessage = $"Studio saved: {saved.StudioName}.";
            return RedirectToPage();
        }
        catch (DbUpdateException ex)
        {
            ModelState.AddModelError(string.Empty, $"Studio could not be saved: {ex.GetBaseException().Message}");
        }

        await LoadStudiosAsync(cancellationToken);
        EnsureEditableRows();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string studioId, CancellationToken cancellationToken)
    {
        await studioDirectoryService.DeleteStudioAsync(studioId, cancellationToken);
        StatusMessage = "Studio deleted.";
        Input = new StudioInput();
        await LoadStudiosAsync(cancellationToken);
        EnsureEditableRows();
        return Page();
    }

    private async Task LoadStudiosAsync(CancellationToken cancellationToken)
    {
        Studios = await studioDirectoryService.GetStudiosAsync(cancellationToken);
    }

    private void NormalizeInputRows()
    {
        Input.TeamMembers = (Input.TeamMembers ?? [])
            .Where(member => !member.Remove)
            .Where(member => !string.IsNullOrWhiteSpace(member.Name) || !string.IsNullOrWhiteSpace(member.RolesAndResponsibilities) || !string.IsNullOrWhiteSpace(member.EmailId))
            .ToList();
        Input.DevelopmentTools = (Input.DevelopmentTools ?? [])
            .Where(tool => !tool.Remove)
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name) || !string.IsNullOrWhiteSpace(tool.Description))
            .ToList();
        Input.ImportantLinks = (Input.ImportantLinks ?? [])
            .Where(link => !link.Remove)
            .Where(link => !string.IsNullOrWhiteSpace(link.Label) || !string.IsNullOrWhiteSpace(link.Url) || !string.IsNullOrWhiteSpace(link.Description))
            .ToList();
    }

    private void EnsureEditableRows()
    {
        Input.TeamMembers.Add(new StudioTeamMemberInput());
        Input.DevelopmentTools.Add(new StudioDevelopmentToolInput());
        Input.ImportantLinks.Add(new StudioImportantLinkInput());
    }

    public sealed class StudioInput
    {
        public string? Id { get; set; }

        [Required]
        public string StudioName { get; set; } = string.Empty;

        [Required]
        public string ProjectName { get; set; } = string.Empty;

        [Required]
        public string Location { get; set; } = string.Empty;

        public List<StudioTeamMemberInput> TeamMembers { get; set; } = [];
        public List<StudioDevelopmentToolInput> DevelopmentTools { get; set; } = [];
        public List<StudioImportantLinkInput> ImportantLinks { get; set; } = [];

        public static StudioInput FromStudio(StudioDetails studio)
        {
            return new StudioInput
            {
                Id = studio.Id,
                StudioName = studio.StudioName,
                ProjectName = studio.ProjectName,
                Location = studio.Location,
                TeamMembers = studio.TeamMembers.Select(member => new StudioTeamMemberInput
                {
                    Name = member.Name,
                    RolesAndResponsibilities = member.RolesAndResponsibilities,
                    EmailId = member.EmailId ?? string.Empty
                }).ToList(),
                DevelopmentTools = studio.DevelopmentTools.Select(tool => new StudioDevelopmentToolInput
                {
                    Name = tool.Name,
                    Description = tool.Description
                }).ToList(),
                ImportantLinks = studio.ImportantLinks.Select(link => new StudioImportantLinkInput
                {
                    Label = link.Label,
                    Url = link.Url,
                    Description = link.Description
                }).ToList()
            };
        }

        public StudioDetails ToStudio()
        {
            return new StudioDetails
            {
                Id = Id ?? string.Empty,
                StudioName = StudioName,
                ProjectName = ProjectName,
                Location = Location,
                TeamMembers = TeamMembers.Select(member => new StudioTeamMember
                {
                    Name = member.Name ?? string.Empty,
                    RolesAndResponsibilities = member.RolesAndResponsibilities ?? string.Empty,
                    EmailId = member.EmailId
                }).ToList(),
                DevelopmentTools = DevelopmentTools.Select(tool => new StudioDevelopmentTool
                {
                    Name = tool.Name ?? string.Empty,
                    Description = tool.Description ?? string.Empty
                }).ToList(),
                ImportantLinks = ImportantLinks.Select(link => new StudioImportantLink
                {
                    Label = link.Label ?? string.Empty,
                    Url = link.Url ?? string.Empty,
                    Description = link.Description ?? string.Empty
                }).ToList()
            };
        }
    }

    public sealed class StudioTeamMemberInput
    {
        public string? Name { get; set; }
        public string? RolesAndResponsibilities { get; set; }
        public string? EmailId { get; set; }
        public bool Remove { get; set; }
    }

    public sealed class StudioDevelopmentToolInput
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public bool Remove { get; set; }
    }

    public sealed class StudioImportantLinkInput
    {
        public string? Label { get; set; }
        public string? Url { get; set; }
        public string? Description { get; set; }
        public bool Remove { get; set; }
    }
}