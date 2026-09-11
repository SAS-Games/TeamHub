using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Authentication;
using TeamHub.Studio;

namespace TeamHub.Web.Pages.Configuration;

[Authorize(Roles = "Admin")]
public sealed class AtlassianModel(
    IAtlassianConfigurationService configurationService,
    IStudioDirectoryService studioDirectoryService,
    IUserAccessService userAccessService) : PageModel
{
    [BindProperty]
    public SettingsInput Settings { get; set; } = new();

    [BindProperty]
    public List<MappingInput> Mappings { get; set; } = [];

    [BindProperty]
    public DefaultCredentialsInput DefaultCredentials { get; set; } = new();

    [BindProperty]
    public List<PrivilegedAccessInput> PrivilegedUsers { get; set; } = [];

    public AtlassianDefaultCredentialStatus DefaultCredentialStatus { get; private set; } = new(false, false);

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await EnsureStudioNamesAsync(cancellationToken);
            return Page();
        }

        try
        {
            await configurationService.SaveSettingsAsync(new AtlassianIntegrationSettings
            {
                JiraEnabled = Settings.JiraEnabled,
                JiraBaseUrl = Settings.JiraBaseUrl ?? string.Empty,
                JiraSearchApiPath = Settings.JiraSearchApiPath ?? string.Empty,
                JiraMaxResults = Settings.JiraMaxResults,
                JiraDefaultSupportComponent = Settings.JiraDefaultSupportComponent ?? string.Empty,
                ConfluenceEnabled = Settings.ConfluenceEnabled,
                ConfluenceBaseUrl = Settings.ConfluenceBaseUrl ?? string.Empty,
                ConfluenceContentApiPath = Settings.ConfluenceContentApiPath ?? string.Empty,
                ConfluenceSpaceKey = Settings.ConfluenceSpaceKey ?? string.Empty,
                ConfluenceParentPageId = Settings.ConfluenceParentPageId ?? string.Empty,
                ConfluenceYearTitlePattern = Settings.ConfluenceYearTitlePattern ?? string.Empty,
                ConfluenceMonthTitlePattern = Settings.ConfluenceMonthTitlePattern ?? string.Empty,
                ConfluenceWeeklyTitlePattern = Settings.ConfluenceWeeklyTitlePattern ?? string.Empty
            }, cancellationToken);

            await configurationService.SaveStudioMappingsAsync(Mappings.Select(item => new StudioAtlassianMapping
            {
                StudioId = item.StudioId ?? string.Empty,
                JiraProjectKeys = ParseProjectKeys(item.JiraProjectKeys),
                JiraStudioComponent = item.JiraStudioComponent ?? string.Empty,
                JiraSupportComponent = item.JiraSupportComponent ?? string.Empty,
                ConfluenceStudioIdentifier = item.ConfluenceStudioIdentifier ?? string.Empty
            }).ToList(), cancellationToken);

            await configurationService.SaveDefaultTokensAsync(
                DefaultCredentials.JiraToken,
                DefaultCredentials.ConfluenceToken,
                DefaultCredentials.RemoveJiraToken,
                DefaultCredentials.RemoveConfluenceToken,
                cancellationToken);

            var eligiblePrivilegedUsers = (await userAccessService.ListUsersAsync(cancellationToken))
                .Where(item => item.IsActive && item.UserType == TeamHubUserTypes.Privileged)
                .ToDictionary(item => item.UserId, StringComparer.OrdinalIgnoreCase);
            await configurationService.SavePrivilegedAccessAsync(PrivilegedUsers
                .Where(item => !string.IsNullOrWhiteSpace(item.UserId) && eligiblePrivilegedUsers.ContainsKey(item.UserId))
                .Select(item => new AtlassianPrivilegedAccess(
                    item.UserId!,
                    item.JiraReadOnlyAccess,
                    item.ConfluenceReadOnlyAccess))
                .ToList(), cancellationToken);
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await EnsureStudioNamesAsync(cancellationToken);
            return Page();
        }

        TempData["StatusMessage"] = "Jira and Confluence configuration saved.";
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var settings = await configurationService.GetSettingsAsync(cancellationToken);
        DefaultCredentialStatus = await configurationService.GetDefaultCredentialStatusAsync(cancellationToken);
        Settings = new SettingsInput
        {
            JiraEnabled = settings.JiraEnabled,
            JiraBaseUrl = settings.JiraBaseUrl,
            JiraSearchApiPath = settings.JiraSearchApiPath,
            JiraMaxResults = settings.JiraMaxResults,
            JiraDefaultSupportComponent = settings.JiraDefaultSupportComponent,
            ConfluenceEnabled = settings.ConfluenceEnabled,
            ConfluenceBaseUrl = settings.ConfluenceBaseUrl,
            ConfluenceContentApiPath = settings.ConfluenceContentApiPath,
            ConfluenceSpaceKey = settings.ConfluenceSpaceKey,
            ConfluenceParentPageId = settings.ConfluenceParentPageId,
            ConfluenceYearTitlePattern = settings.ConfluenceYearTitlePattern,
            ConfluenceMonthTitlePattern = settings.ConfluenceMonthTitlePattern,
            ConfluenceWeeklyTitlePattern = settings.ConfluenceWeeklyTitlePattern
        };

        var studios = await studioDirectoryService.GetStudiosAsync(cancellationToken);
        var mappings = (await configurationService.ListStudioMappingsAsync(cancellationToken))
            .ToDictionary(item => item.StudioId, StringComparer.OrdinalIgnoreCase);
        Mappings = studios.Select(studio =>
        {
            mappings.TryGetValue(studio.Id, out var mapping);
            return new MappingInput
            {
                StudioId = studio.Id,
                StudioName = studio.StudioName,
                ProjectName = studio.ProjectName,
                JiraProjectKeys = mapping is null ? string.Empty : string.Join(", ", mapping.JiraProjectKeys),
                JiraStudioComponent = mapping?.JiraStudioComponent ?? string.Empty,
                JiraSupportComponent = mapping?.JiraSupportComponent ?? string.Empty,
                ConfluenceStudioIdentifier = mapping?.ConfluenceStudioIdentifier ?? string.Empty
            };
        }).ToList();

        var assignedAccess = (await configurationService.ListPrivilegedAccessAsync(cancellationToken))
            .ToDictionary(item => item.UserId, StringComparer.OrdinalIgnoreCase);
        PrivilegedUsers = (await userAccessService.ListUsersAsync(cancellationToken))
            .Where(item => item.IsActive && item.UserType == TeamHubUserTypes.Privileged)
            .Select(user =>
            {
                assignedAccess.TryGetValue(user.UserId, out var access);
                return new PrivilegedAccessInput
                {
                    UserId = user.UserId,
                    DisplayName = user.DisplayName,
                    JiraReadOnlyAccess = access?.JiraReadOnlyAccess ?? false,
                    ConfluenceReadOnlyAccess = access?.ConfluenceReadOnlyAccess ?? false
                };
            })
            .ToList();
    }

    private async Task EnsureStudioNamesAsync(CancellationToken cancellationToken)
    {
        var studios = (await studioDirectoryService.GetStudiosAsync(cancellationToken))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in Mappings)
        {
            if (!string.IsNullOrWhiteSpace(mapping.StudioId) && studios.TryGetValue(mapping.StudioId, out var studio))
            {
                mapping.StudioName = studio.StudioName;
                mapping.ProjectName = studio.ProjectName;
            }
        }
        DefaultCredentialStatus = await configurationService.GetDefaultCredentialStatusAsync(cancellationToken);
        var users = (await userAccessService.ListUsersAsync(cancellationToken))
            .ToDictionary(item => item.UserId, StringComparer.OrdinalIgnoreCase);
        foreach (var privilegedUser in PrivilegedUsers)
        {
            if (!string.IsNullOrWhiteSpace(privilegedUser.UserId) && users.TryGetValue(privilegedUser.UserId, out var user)) privilegedUser.DisplayName = user.DisplayName;
        }
    }

    private static IReadOnlyList<string> ParseProjectKeys(string? value) =>
        (value ?? string.Empty)
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public sealed class SettingsInput
    {
        public bool JiraEnabled { get; set; }
        [StringLength(2048)] public string? JiraBaseUrl { get; set; }
        [StringLength(512)] public string? JiraSearchApiPath { get; set; } = "/rest/api/2/search";
        [Range(1, 1000)] public int JiraMaxResults { get; set; } = 100;
        [StringLength(256)] public string? JiraDefaultSupportComponent { get; set; } = "StudioSupport";
        public bool ConfluenceEnabled { get; set; }
        [StringLength(2048)] public string? ConfluenceBaseUrl { get; set; }
        [StringLength(512)] public string? ConfluenceContentApiPath { get; set; } = "/rest/api/content";
        [StringLength(256)] public string? ConfluenceSpaceKey { get; set; }
        [StringLength(256)] public string? ConfluenceParentPageId { get; set; }
        [StringLength(256)] public string? ConfluenceYearTitlePattern { get; set; } = "{Year}";
        [StringLength(256)] public string? ConfluenceMonthTitlePattern { get; set; } = "{Month}/{Year}";
        [StringLength(512)] public string? ConfluenceWeeklyTitlePattern { get; set; } = "{WeekStart:dd/MM}-{WeekEnd:dd/MM}";
    }

    public sealed class MappingInput
    {
        [Required] public string? StudioId { get; set; }
        public string? StudioName { get; set; }
        public string? ProjectName { get; set; }
        [StringLength(2048)] public string? JiraProjectKeys { get; set; }
        [StringLength(256)] public string? JiraStudioComponent { get; set; }
        [StringLength(256)] public string? JiraSupportComponent { get; set; }
        [StringLength(256)] public string? ConfluenceStudioIdentifier { get; set; }
    }

    public sealed class DefaultCredentialsInput
    {
        [DataType(DataType.Password)]
        [StringLength(4096)]
        public string? JiraToken { get; set; }
        [DataType(DataType.Password)]
        [StringLength(4096)]
        public string? ConfluenceToken { get; set; }
        public bool RemoveJiraToken { get; set; }
        public bool RemoveConfluenceToken { get; set; }
    }

    public sealed class PrivilegedAccessInput
    {
        [Required] public string? UserId { get; set; }
        public string? DisplayName { get; set; }
        public bool JiraReadOnlyAccess { get; set; }
        public bool ConfluenceReadOnlyAccess { get; set; }
    }
}
