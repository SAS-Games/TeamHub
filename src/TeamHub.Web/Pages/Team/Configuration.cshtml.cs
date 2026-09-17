using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.Team;
using TeamHub.Authentication;
using TeamHub.Web.AccessControl;

namespace TeamHub.Web.Pages.Team;

[Authorize(Roles = TeamHubUserTypes.Admin)]
public sealed class ConfigurationModel(
    ITeamDirectoryService teamDirectoryService,
    ITeamConfigurationService teamConfigurationService,
    ITeamAchievementService achievementService,
    IPageTextAppearanceService appearanceService,
    ICustomTeamTabService customTabs,
    IExcelTableSourceReader excelReader,
    IExcelSourceFileStore excelSourceFiles,
    IUserAccessService users) : PageModel
{
    private const string MembersSection = "members";
    private const string SpecializationsSection = "specializations";
    private const string AchievementsSection = "achievements";
    private const string AppearanceSection = "appearance";
    private const string CustomTabsSection = "custom-tabs";

    public TeamMemberInput MemberInput { get; set; } = new();

    public SpecializationInput SupportInput { get; set; } = new();

    public AchievementInput AchievementForm { get; set; } = new();

    public PageAppearanceInput AppearanceForm { get; set; } = new();

    public TeamDirectoryDto Directory { get; private set; } = new();

    public IReadOnlyList<TeamAchievementDto> Achievements { get; private set; } = [];

    public IReadOnlyList<TeamPageAppearanceDefinition> AppearancePages => TeamPageAppearanceCatalog.Pages;

    public IReadOnlyList<CustomTeamTabDto> CustomTabs { get; private set; } = [];
    public CustomTeamTabDto? SelectedCustomTab { get; private set; }
    public string? SelectedCustomTabId { get; private set; }
    public IReadOnlyList<string> CustomFieldTypes => CustomTeamFieldTypes.All;

    public string ActiveSection { get; private set; } = MembersSection;

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(
        string? section = null,
        string? memberId = null,
        string? specializationId = null,
        string? achievementId = null,
        string? appearancePage = null,
        string? customTabId = null,
        CancellationToken cancellationToken = default)
    {
        ActiveSection = NormalizeSection(section);
        AppearanceForm.PageKey = NormalizeAppearancePage(appearancePage);
        SelectedCustomTabId = customTabId;

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

        if (!string.IsNullOrWhiteSpace(achievementId))
        {
            var achievement = await achievementService.GetAchievementAsync(achievementId, cancellationToken);
            if (achievement is null)
            {
                return NotFound();
            }

            AchievementForm = AchievementInput.FromDto(achievement);
            ActiveSection = AchievementsSection;
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

        var isEditing = !string.IsNullOrWhiteSpace(AchievementForm.Id);
        TeamAchievementDto saved;
        try
        {
            saved = await achievementService.SaveAchievementAsync(AchievementForm.ToDto(), cancellationToken);
        }
        catch (KeyNotFoundException exception)
        {
            ErrorMessage = exception.Message;
            return RedirectToPage(new { section = AchievementsSection });
        }

        StatusMessage = isEditing
            ? $"Achievement updated: {saved.Title}."
            : $"Achievement added: {saved.Title}.";
        return RedirectToPage(new { section = AchievementsSection });
    }

    public async Task<IActionResult> OnPostDeleteAchievementAsync(
        string achievementId,
        CancellationToken cancellationToken)
    {
        await achievementService.DeleteAchievementAsync(achievementId, cancellationToken);
        StatusMessage = "Achievement deleted.";
        return RedirectToPage(new { section = AchievementsSection });
    }

    public async Task<IActionResult> OnPostSaveAppearanceAsync(
        [Bind(Prefix = nameof(AppearanceForm))] PageAppearanceInput input,
        CancellationToken cancellationToken)
    {
        ActiveSection = AppearanceSection;
        AppearanceForm = input;
        var definition = TeamPageAppearanceCatalog.Find(input.PageKey);
        if (definition is null)
        {
            ModelState.AddModelError("AppearanceForm.PageKey", "Select a supported page.");
            AppearanceForm.PageKey = TeamPageAppearanceCatalog.Pages[0].Key;
            await LoadConfigurationAsync(cancellationToken);
            return Page();
        }

        if (!ModelState.IsValid)
        {
            await LoadConfigurationAsync(cancellationToken);
            return Page();
        }

        var postedColumns = input.Columns
            .Where(column => !string.IsNullOrWhiteSpace(column.ColumnKey))
            .GroupBy(column => column.ColumnKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var appearance = new PageTextAppearanceDto
        {
            PageKey = definition.Key,
            Columns = definition.Columns.Select(column =>
            {
                postedColumns.TryGetValue(column.Key, out var posted);
                return new ColumnTextAppearanceDto
                {
                    ColumnKey = column.Key,
                    IsBold = posted?.UsesBold == true,
                    IsItalic = posted?.UsesItalic == true
                };
            }).ToList()
        };

        await appearanceService.SavePageAppearanceAsync(appearance, cancellationToken);
        StatusMessage = $"Text appearance saved for {definition.Label}.";
        return RedirectToPage(new { section = AppearanceSection, appearancePage = definition.Key });
    }

    public async Task<IActionResult> OnPostSaveCustomTabAsync(
        string? id,
        string name,
        int displayOrder,
        string navigationPlacement,
        string pageWidth,
        CancellationToken cancellationToken)
    {
        try
        {
            var saved = await customTabs.SaveTabAsync(
                new SaveCustomTeamTabRequest(id, name, displayOrder, navigationPlacement, pageWidth),
                cancellationToken);
            await users.EnsureModulesAsync([CustomTeamTabAccess.ModuleForSlug(saved.Slug)], cancellationToken);
            StatusMessage = $"Team tab saved: {saved.Name}.";
            return CustomRedirect(saved.Id);
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            ErrorMessage = exception.Message;
            return CustomRedirect(id);
        }
    }

    public async Task<IActionResult> OnPostArchiveCustomTabAsync(string id, CancellationToken cancellationToken)
    {
        await customTabs.ArchiveTabAsync(id, cancellationToken);
        StatusMessage = "Team tab archived. Its schema and data remain in the Team Hub database.";
        return CustomRedirect();
    }

    public async Task<IActionResult> OnPostDeleteCustomTabAsync(string id, CancellationToken cancellationToken)
    {
        var tab = await customTabs.GetTabByIdAsync(id, cancellationToken);
        if (tab is null)
        {
            ErrorMessage = "Team tab was not found.";
            return CustomRedirect();
        }

        await customTabs.DeleteTabAsync(id, cancellationToken);
        await users.RemoveModulesAsync([CustomTeamTabAccess.ModuleForSlug(tab.Slug)], cancellationToken);
        StatusMessage = $"Team tab permanently deleted: {tab.Name}.";
        return CustomRedirect();
    }

    public async Task<IActionResult> OnPostSaveCustomTableAsync(
        string tabId,
        string? tableId,
        string name,
        int displayOrder,
        string sourceType,
        string? sourceUrl,
        string? sourceDisplayName,
        IFormFile? sourceFile,
        string? sourceWorksheet,
        int sourceHeaderRow,
        string? primaryKeySourceHeader,
        CancellationToken cancellationToken)
    {
        var isNewTable = string.IsNullOrWhiteSpace(tableId);
        try
        {
            var saved = await SaveCustomTableConfigurationAsync(
                tabId,
                tableId,
                name,
                displayOrder,
                sourceType,
                sourceUrl,
                sourceDisplayName,
                sourceFile,
                sourceWorksheet,
                sourceHeaderRow,
                primaryKeySourceHeader,
                cancellationToken);
            StatusMessage = isNewTable
                ? $"Table created: {saved.Name}. Configure its data source below."
                : $"Table saved: {saved.Name}.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = exception.Message;
        }
        return CustomRedirect(tabId);
    }

    public async Task<IActionResult> OnPostSaveAndSyncCustomTableAsync(
        string tabId,
        string tableId,
        string name,
        int displayOrder,
        string sourceType,
        string? sourceUrl,
        string? sourceDisplayName,
        IFormFile? sourceFile,
        string? sourceWorksheet,
        int sourceHeaderRow,
        string? primaryKeySourceHeader,
        CancellationToken cancellationToken)
    {
        try
        {
            var saved = await SaveCustomTableConfigurationAsync(
                tabId,
                tableId,
                name,
                displayOrder,
                sourceType,
                sourceUrl,
                sourceDisplayName,
                sourceFile,
                sourceWorksheet,
                sourceHeaderRow,
                primaryKeySourceHeader,
                cancellationToken);
            var result = await customTabs.SyncExcelTableAsync(
                saved.Id,
                User.Identity?.Name ?? "Administrator",
                cancellationToken);
            StatusMessage = $"Table configuration saved and Excel synchronized: {result.Added} added, {result.Updated} updated, {result.Restored} restored, {result.Missing} missing from source.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = exception.Message;
        }
        return CustomRedirect(tabId);
    }

    public async Task<IActionResult> OnPostSaveAndImportCustomTableSchemaAsync(
        string tabId,
        string tableId,
        string name,
        int displayOrder,
        string sourceType,
        string? sourceUrl,
        string? sourceDisplayName,
        IFormFile? sourceFile,
        string? sourceWorksheet,
        int sourceHeaderRow,
        string? primaryKeySourceHeader,
        CancellationToken cancellationToken)
    {
        try
        {
            var saved = await SaveCustomTableConfigurationAsync(
                tabId,
                tableId,
                name,
                displayOrder,
                sourceType,
                sourceUrl,
                sourceDisplayName,
                sourceFile,
                sourceWorksheet,
                sourceHeaderRow,
                primaryKeySourceHeader,
                cancellationToken);
            var result = await customTabs.ImportExcelSchemaAsync(saved.Id, cancellationToken);
            StatusMessage = result.MissingFromWorkbook == 0
                ? $"Table configuration saved. Excel schema imported from '{result.Worksheet}': {result.Added} column(s) added, {result.Existing} already present."
                : $"Table configuration saved. Excel schema imported from '{result.Worksheet}': {result.Added} added, {result.Existing} already present, {result.MissingFromWorkbook} mapped column(s) no longer found in the workbook.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = exception.Message;
        }
        return CustomRedirect(tabId);
    }

    private async Task<CustomTeamTableDto> SaveCustomTableConfigurationAsync(
        string tabId,
        string? tableId,
        string name,
        int displayOrder,
        string sourceType,
        string? sourceUrl,
        string? sourceDisplayName,
        IFormFile? sourceFile,
        string? sourceWorksheet,
        int sourceHeaderRow,
        string? primaryKeySourceHeader,
        CancellationToken cancellationToken)
    {
        if (sourceType == CustomTeamTableSourceTypes.UploadedExcel && sourceFile is { Length: > 0 })
        {
            await using var upload = sourceFile.OpenReadStream();
            var stored = await excelSourceFiles.SaveAsync(
                upload,
                sourceFile.FileName,
                sourceFile.Length,
                cancellationToken);
            sourceUrl = stored.Reference;
            sourceDisplayName = stored.DisplayName;
        }

        return await customTabs.SaveTableAsync(new SaveCustomTeamTableRequest(
            tabId,
            tableId,
            name,
            displayOrder,
            sourceType,
            sourceUrl,
            sourceWorksheet,
            sourceHeaderRow,
            primaryKeySourceHeader,
            null,
            null,
            sourceDisplayName), cancellationToken);
    }

    public async Task<IActionResult> OnPostTestCustomTableSourceAsync(
        string sourceType,
        string? sourceUrl,
        string? sourceWorksheet,
        int sourceHeaderRow,
        CancellationToken cancellationToken)
    {
        try
        {
            if (sourceType is not (CustomTeamTableSourceTypes.ExcelUrl or CustomTeamTableSourceTypes.MicrosoftGraphExcel))
                throw new ArgumentException("Select Excel download link or SharePoint / OneDrive before testing.");

            var result = await excelReader.ReadAsync(new ExcelTableSourceRequest(
                sourceType,
                sourceUrl,
                null,
                null,
                sourceWorksheet,
                sourceHeaderRow), cancellationToken);
            return new JsonResult(new
            {
                success = true,
                message = $"Workbook test successful. Worksheet '{result.Worksheet}' contains {result.Headers.Count:N0} column(s) and {result.Rows.Count:N0} data row(s)."
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or InvalidDataException
            or HttpRequestException)
        {
            return new JsonResult(new
            {
                success = false,
                message = $"Workbook test failed: {exception.Message}"
            });
        }
    }

    public async Task<IActionResult> OnPostArchiveCustomTableAsync(
        string tabId, string tableId, CancellationToken cancellationToken)
    {
        await customTabs.ArchiveTableAsync(tableId, cancellationToken);
        StatusMessage = "Table archived. Existing rows remain recoverable in the Team Hub database.";
        return CustomRedirect(tabId);
    }

    public async Task<IActionResult> OnPostDeleteCustomTableAsync(
        string tabId, string tableId, string tableName, CancellationToken cancellationToken)
    {
        await customTabs.DeleteTableAsync(tableId, cancellationToken);
        StatusMessage = $"Table permanently deleted: {tableName}.";
        return CustomRedirect(tabId);
    }

    public async Task<IActionResult> OnPostSyncCustomTableAsync(
        string tabId,
        string tableId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await customTabs.SyncExcelTableAsync(
                tableId,
                User.Identity?.Name ?? "Administrator",
                cancellationToken);
            StatusMessage = $"Excel synchronized: {result.Added} added, {result.Updated} updated, {result.Restored} restored, {result.Missing} missing from source.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = exception.Message;
        }
        return CustomRedirect(tabId);
    }

    public async Task<IActionResult> OnPostImportCustomTableSchemaAsync(
        string tabId,
        string tableId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await customTabs.ImportExcelSchemaAsync(tableId, cancellationToken);
            StatusMessage = result.MissingFromWorkbook == 0
                ? $"Excel schema imported from '{result.Worksheet}': {result.Added} column(s) added, {result.Existing} already present."
                : $"Excel schema imported from '{result.Worksheet}': {result.Added} added, {result.Existing} already present, {result.MissingFromWorkbook} mapped column(s) no longer found in the workbook.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            ErrorMessage = exception.Message;
        }
        return CustomRedirect(tabId);
    }

    public async Task<IActionResult> OnPostSaveCustomColumnAsync(
        string tabId,
        string tableId,
        string? columnId,
        string label,
        string fieldType,
        bool isRequired,
        string? options,
        int displayOrder,
        CancellationToken cancellationToken)
    {
        try
        {
            var parsedOptions = options?.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            var saved = await customTabs.SaveColumnAsync(new SaveCustomTeamColumnRequest(
                tableId, columnId, label, fieldType, isRequired, parsedOptions, displayOrder), cancellationToken);
            StatusMessage = $"Column saved: {saved.Label}.";
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            ErrorMessage = exception.Message;
        }
        return CustomRedirect(tabId);
    }

    public async Task<IActionResult> OnPostArchiveCustomColumnAsync(
        string tabId, string columnId, CancellationToken cancellationToken)
    {
        await customTabs.ArchiveColumnAsync(columnId, cancellationToken);
        StatusMessage = "Column archived. Existing values remain stored for recovery.";
        return CustomRedirect(tabId);
    }

    private RedirectToPageResult CustomRedirect(string? customTabId = null) =>
        RedirectToPage(new { section = CustomTabsSection, customTabId });

    private async Task LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        Directory = await teamDirectoryService.GetTeamDirectoryAsync(cancellationToken);
        Achievements = await achievementService.GetAchievementsAsync(cancellationToken);
        CustomTabs = await customTabs.ListTabsAsync(cancellationToken);
        if (ActiveSection == CustomTabsSection)
        {
            SelectedCustomTabId ??= CustomTabs.FirstOrDefault()?.Id;
            SelectedCustomTab = string.IsNullOrWhiteSpace(SelectedCustomTabId)
                ? null
                : await customTabs.GetTabByIdAsync(SelectedCustomTabId, cancellationToken);
        }
        if (ActiveSection == AppearanceSection)
        {
            var definition = TeamPageAppearanceCatalog.Find(AppearanceForm.PageKey)
                ?? TeamPageAppearanceCatalog.Pages[0];
            var appearance = await appearanceService.GetPageAppearanceAsync(definition.Key, cancellationToken);
            AppearanceForm = PageAppearanceInput.From(definition, appearance);
        }
    }

    private static string NormalizeSection(string? section) => section?.ToLowerInvariant() switch
    {
        SpecializationsSection => SpecializationsSection,
        AchievementsSection => AchievementsSection,
        AppearanceSection => AppearanceSection,
        CustomTabsSection => CustomTabsSection,
        _ => MembersSection
    };

    private static string NormalizeAppearancePage(string? pageKey) =>
        TeamPageAppearanceCatalog.Find(pageKey)?.Key ?? TeamPageAppearanceCatalog.Pages[0].Key;

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

        [Required, RegularExpression("^(TeamMember|Management)$")]
        public string Section { get; set; } = TeamMemberSections.TeamMember;

        public TeamMemberDto ToDto() => new()
        {
            Id = Id ?? string.Empty,
            EmployeeName = EmployeeName,
            Role = Role ?? string.Empty,
            Gid = Gid ?? string.Empty,
            Email = Email ?? string.Empty,
            ContactNumber = ContactNumber ?? string.Empty,
            Section = Section
        };

        public static TeamMemberInput FromDto(TeamMemberDto member) => new()
        {
            Id = member.Id,
            EmployeeName = member.EmployeeName,
            Role = member.Role,
            Gid = member.Gid,
            Email = member.Email,
            ContactNumber = member.ContactNumber,
            Section = TeamMemberSections.Normalize(member.Section)
        };
    }

    public sealed class PageAppearanceInput
    {
        public string PageKey { get; set; } = TeamPageAppearanceCatalog.Directory;
        public List<ColumnAppearanceInput> Columns { get; set; } = [];

        public static PageAppearanceInput From(
            TeamPageAppearanceDefinition definition,
            PageTextAppearanceDto appearance)
        {
            var savedColumns = appearance.Columns.ToDictionary(
                column => column.ColumnKey,
                StringComparer.OrdinalIgnoreCase);
            return new PageAppearanceInput
            {
                PageKey = definition.Key,
                Columns = definition.Columns.Select(column =>
                {
                    savedColumns.TryGetValue(column.Key, out var saved);
                    return new ColumnAppearanceInput
                    {
                        ColumnKey = column.Key,
                        Label = column.Label,
                        Style = (saved?.IsBold == true, saved?.IsItalic == true) switch
                        {
                            (true, true) => TextAppearanceStyle.BoldItalic,
                            (true, false) => TextAppearanceStyle.Bold,
                            (false, true) => TextAppearanceStyle.Italic,
                            _ => TextAppearanceStyle.Normal
                        }
                    };
                }).ToList()
            };
        }
    }

    public sealed class ColumnAppearanceInput
    {
        public string ColumnKey { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public TextAppearanceStyle Style { get; set; }
        public bool UsesBold => Style is TextAppearanceStyle.Bold or TextAppearanceStyle.BoldItalic;
        public bool UsesItalic => Style is TextAppearanceStyle.Italic or TextAppearanceStyle.BoldItalic;
    }

    public enum TextAppearanceStyle
    {
        Normal,
        Bold,
        Italic,
        BoldItalic
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
        public string? Id { get; set; }

        [Required, StringLength(256)]
        public string Title { get; set; } = string.Empty;

        [Required, StringLength(2000)]
        public string Description { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? Impact { get; set; }

        [Required, StringLength(512)]
        public string AchievedBy { get; set; } = string.Empty;

        [Required, DataType(DataType.Date)]
        public DateTime AchievedOn { get; set; } = DateTime.Today;

        public TeamAchievementDto ToDto() => new()
        {
            Id = Id ?? string.Empty,
            Title = Title,
            Description = Description,
            Impact = Impact ?? string.Empty,
            AchievedBy = AchievedBy,
            AchievedOn = AchievedOn
        };

        public static AchievementInput FromDto(TeamAchievementDto achievement) => new()
        {
            Id = achievement.Id,
            Title = achievement.Title,
            Description = achievement.Description,
            Impact = achievement.Impact,
            AchievedBy = achievement.AchievedBy,
            AchievedOn = achievement.AchievedOn
        };
    }
}
