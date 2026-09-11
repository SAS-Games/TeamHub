using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Studio;

namespace TeamHub.Web.Pages.Configuration;

[Authorize(Roles = "Admin")]
public sealed class AtlassianModel(
    IAtlassianConfigurationService configurationService,
    IStudioDirectoryService studioDirectoryService) : PageModel
{
    [BindProperty]
    public SettingsInput Settings { get; set; } = new();

    [BindProperty]
    public List<MappingInput> Mappings { get; set; } = [];

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
                JiraBaseUrl = Settings.JiraBaseUrl,
                JiraSearchApiPath = Settings.JiraSearchApiPath,
                JiraMaxResults = Settings.JiraMaxResults,
                JiraDefaultSupportComponent = Settings.JiraDefaultSupportComponent,
                ConfluenceEnabled = Settings.ConfluenceEnabled,
                ConfluenceBaseUrl = Settings.ConfluenceBaseUrl,
                ConfluenceContentApiPath = Settings.ConfluenceContentApiPath
            }, cancellationToken);

            await configurationService.SaveStudioMappingsAsync(Mappings.Select(item => new StudioAtlassianMapping
            {
                StudioId = item.StudioId,
                JiraProjectKeys = ParseProjectKeys(item.JiraProjectKeys),
                JiraStudioComponent = item.JiraStudioComponent,
                JiraSupportComponent = item.JiraSupportComponent,
                ConfluenceSpaceKey = item.ConfluenceSpaceKey,
                ConfluenceParentPageId = item.ConfluenceParentPageId,
                ConfluenceWeeklyTitlePattern = item.ConfluenceWeeklyTitlePattern
            }).ToList(), cancellationToken);
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
        Settings = new SettingsInput
        {
            JiraEnabled = settings.JiraEnabled,
            JiraBaseUrl = settings.JiraBaseUrl,
            JiraSearchApiPath = settings.JiraSearchApiPath,
            JiraMaxResults = settings.JiraMaxResults,
            JiraDefaultSupportComponent = settings.JiraDefaultSupportComponent,
            ConfluenceEnabled = settings.ConfluenceEnabled,
            ConfluenceBaseUrl = settings.ConfluenceBaseUrl,
            ConfluenceContentApiPath = settings.ConfluenceContentApiPath
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
                ConfluenceSpaceKey = mapping?.ConfluenceSpaceKey ?? string.Empty,
                ConfluenceParentPageId = mapping?.ConfluenceParentPageId ?? string.Empty,
                ConfluenceWeeklyTitlePattern = mapping?.ConfluenceWeeklyTitlePattern ?? "{StudioName} Weekly Update - {WeekStart:yyyy-MM-dd}"
            };
        }).ToList();
    }

    private async Task EnsureStudioNamesAsync(CancellationToken cancellationToken)
    {
        var studios = (await studioDirectoryService.GetStudiosAsync(cancellationToken))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in Mappings)
        {
            if (studios.TryGetValue(mapping.StudioId, out var studio))
            {
                mapping.StudioName = studio.StudioName;
                mapping.ProjectName = studio.ProjectName;
            }
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
        [StringLength(2048)] public string JiraBaseUrl { get; set; } = string.Empty;
        [StringLength(512)] public string JiraSearchApiPath { get; set; } = "/rest/api/2/search";
        [Range(1, 1000)] public int JiraMaxResults { get; set; } = 100;
        [StringLength(256)] public string JiraDefaultSupportComponent { get; set; } = "studio_Support";
        public bool ConfluenceEnabled { get; set; }
        [StringLength(2048)] public string ConfluenceBaseUrl { get; set; } = string.Empty;
        [StringLength(512)] public string ConfluenceContentApiPath { get; set; } = "/rest/api/content";
    }

    public sealed class MappingInput
    {
        public string StudioId { get; set; } = string.Empty;
        public string StudioName { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        [StringLength(2048)] public string JiraProjectKeys { get; set; } = string.Empty;
        [StringLength(256)] public string JiraStudioComponent { get; set; } = string.Empty;
        [StringLength(256)] public string JiraSupportComponent { get; set; } = string.Empty;
        [StringLength(256)] public string ConfluenceSpaceKey { get; set; } = string.Empty;
        [StringLength(256)] public string ConfluenceParentPageId { get; set; } = string.Empty;
        [StringLength(512)] public string ConfluenceWeeklyTitlePattern { get; set; } = string.Empty;
    }
}
