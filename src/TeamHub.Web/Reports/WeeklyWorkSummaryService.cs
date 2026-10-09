using System.Security.Claims;
using Microsoft.Extensions.Options;
using TeamHub.AI.ReportGenerator.Contracts;
using TeamHub.Authentication;
using TeamHub.Studio;
using TeamHub.Web.AccessControl;

namespace TeamHub.Web.Reports;

public interface IWeeklyWorkSummaryService
{
    bool IsEnabled { get; }
    Task<bool> CanGenerateAsync(CancellationToken cancellationToken = default);
    Task<WeeklyWorkSummaryPreview> GenerateAsync(
        DateOnly weekStart,
        DateOnly weekEnd,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}

public sealed record WeeklyWorkSummaryPreview(
    WeeklyWorkSummaryDraft Draft,
    IReadOnlyList<string> Warnings);

public sealed class WeeklyWorkSummaryService(
    ICurrentAccessService access,
    IStudioDirectoryService studios,
    IStudioConfluenceUpdateService confluence,
    IWeeklyWorkSummaryWorkflow workflow,
    IOptions<ReportGeneratorOptions> options) : IWeeklyWorkSummaryService
{
    public bool IsEnabled => options.Value.Enabled;

    public async Task<bool> CanGenerateAsync(CancellationToken cancellationToken = default) =>
        await access.CanAsync(TeamHubModules.StudioSupport, AccessLevel.ReadOnly, cancellationToken)
        && await access.CanAsync(TeamHubModules.AiReports, AccessLevel.Create, cancellationToken);

    public async Task<WeeklyWorkSummaryPreview> GenerateAsync(
        DateOnly weekStart,
        DateOnly weekEnd,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (!await CanGenerateAsync(cancellationToken))
            throw new UnauthorizedAccessException("You do not have permission to generate AI reports.");
        if (!IsEnabled)
            throw new WeeklyWorkSummaryException("AI report generation is disabled. An administrator can enable AI:ReportGenerator:Enabled.");
        if (weekStart.DayOfWeek != DayOfWeek.Monday || weekEnd != weekStart.AddDays(4))
            throw new WeeklyWorkSummaryException("Select a Monday-Friday weekly period.");

        var activeStudios = (await studios.GetStudiosAsync(cancellationToken))
            .Where(studio => studio.IsActive)
            .OrderBy(studio => studio.GroupDisplayOrder ?? int.MaxValue)
            .ThenBy(studio => studio.StudioGroup, StringComparer.OrdinalIgnoreCase)
            .ThenBy(studio => studio.StudioDisplayOrder ?? int.MaxValue)
            .ThenBy(studio => studio.StudioName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(studio => studio.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (activeStudios.Count == 0)
            throw new WeeklyWorkSummaryException("No active studios have been configured.");
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? string.Empty;
        var result = await confluence.GetConsolidatedUpdatesAsync(new ConsolidatedStudioConfluenceUpdateQuery
        {
            StudioIds = activeStudios.Select(studio => studio.Id).ToList(),
            StartDate = weekStart,
            EndDate = weekEnd,
            RequestingUserId = userId,
            AllowPrivilegedDefaultCredential = user.IsInRole(TeamHubUserTypes.Privileged)
        }, cancellationToken);
        if (!result.IsConfigured || !string.IsNullOrWhiteSpace(result.Message))
            throw new WeeklyWorkSummaryException(result.Message ?? "Confluence is not configured.");

        var warnings = new List<string>();
        var sources = new List<WeeklyWorkSummarySource>();
        foreach (var studio in activeStudios)
        {
            var updates = result.Updates.Where(update =>
                string.Equals(update.StudioId, studio.Id, StringComparison.OrdinalIgnoreCase)
                && update.WeekStart == weekStart && update.WeekEnd == weekEnd).ToList();
            if (updates.Count > 1)
                throw new WeeklyWorkSummaryException($"Multiple Confluence rows were returned for {studio.StudioName} in the selected week.");
            var update = updates.SingleOrDefault();
            if (update is null || !update.PageFound || !update.StudioFound)
            {
                warnings.Add($"{studio.StudioName}: no Confluence source was available for the selected week.");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(update.Message))
                throw new WeeklyWorkSummaryException($"{studio.StudioName}: {update.Message}");
            if (string.IsNullOrWhiteSpace(update.HpgdsSupport) && string.IsNullOrWhiteSpace(update.Notes))
            {
                warnings.Add($"{studio.StudioName}: HPGDS Support and Notes are empty.");
                continue;
            }
            sources.Add(new WeeklyWorkSummarySource(
                studio.Id, studio.StudioName, update.HpgdsSupport, update.Notes,
                update.PageId, update.PageVersion));
        }
        var draft = await workflow.GenerateAsync(
            new WeeklyWorkSummaryRequest(weekStart, weekEnd, userId, sources), cancellationToken);
        return new WeeklyWorkSummaryPreview(draft, warnings);
    }
}
