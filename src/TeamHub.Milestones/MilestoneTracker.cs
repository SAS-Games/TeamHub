using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeamHub.Excel;

namespace TeamHub.Milestones;

public sealed class MilestoneDto
{
    public string SlNo { get; set; } = string.Empty;
    public string Program { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Developer { get; set; } = string.Empty;
    public string Milestone { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime? DeliveryDate { get; set; }
    public DateTime? MsApprovalDate { get; set; }
    public DateTime? ReleaseDate { get; set; }
}

public interface IMilestoneTrackerService
{
    Task<IReadOnlyList<MilestoneDto>> GetMilestonesAsync(CancellationToken cancellationToken = default);
    Task<MilestoneSourceTestResult> TestSourceAsync(
        MilestoneSourceSettings settings,
        CancellationToken cancellationToken = default);
}

public sealed record MilestoneSourceTestResult(int MilestoneCount);

public sealed class MilestoneConfigurationOptions
{
    public const string SectionName = "MilestoneConfiguration";
    public string SourceType { get; set; } = ExcelWorkbookSourceTypes.LocalFile;
    public string ExcelPath { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string SourceDriveId { get; set; } = string.Empty;
    public string SourceItemId { get; set; } = string.Empty;
    public string ManagedReference { get; set; } = string.Empty;
}

internal sealed class ExcelMilestoneTrackerService(
    IMilestoneConfigurationService configurationService,
    IExcelWorkbookSource workbookSource) : IMilestoneTrackerService
{
    public async Task<IReadOnlyList<MilestoneDto>> GetMilestonesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await configurationService.GetSettingsAsync(cancellationToken);
        return await ReadMilestonesAsync(settings, cancellationToken);
    }

    public async Task<MilestoneSourceTestResult> TestSourceAsync(
        MilestoneSourceSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var milestones = await ReadMilestonesAsync(settings, cancellationToken);
        return new MilestoneSourceTestResult(milestones.Count);
    }

    private async Task<IReadOnlyList<MilestoneDto>> ReadMilestonesAsync(
        MilestoneSourceSettings settings,
        CancellationToken cancellationToken)
    {
        await using var workbookStream = await workbookSource.OpenAsync(
            CreateSourceRequest(settings),
            cancellationToken);
        using var workbook = new XLWorkbook(workbookStream);
        var milestones = new List<MilestoneDto>();
        if (workbook.Worksheets.Count == 0)
        {
            throw new InvalidDataException("The milestone tracker Excel file has no worksheets.");
        }

        foreach (var worksheet in workbook.Worksheets)
        {
            var headerRow = FindHeaderRow(worksheet);
            if (headerRow is null)
            {
                continue;
            }
            var headers = GetHeaderMap(headerRow);

            var currentSlNo = string.Empty;
            var currentProgram = string.Empty;
            var currentTitle = string.Empty;
            var currentDeveloper = string.Empty;
            foreach (var row in worksheet.RowsUsed().Where(row => row.RowNumber() > headerRow.RowNumber()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slNo = row.Cell(headers["Sl. No"]).GetString().Trim();
                var program = row.Cell(headers["Program"]).GetString().Trim();
                var title = row.Cell(headers["Title"]).GetString().Trim();
                var developer = row.Cell(headers["Developer"]).GetString().Trim();
                var milestone = row.Cell(headers["Milestone"]).GetString().Trim();
                if (string.IsNullOrWhiteSpace(program) && string.IsNullOrWhiteSpace(milestone))
                {
                    continue;
                }

                currentSlNo = string.IsNullOrWhiteSpace(slNo) ? currentSlNo : slNo;
                currentProgram = string.IsNullOrWhiteSpace(program) ? currentProgram : program;
                currentTitle = string.IsNullOrWhiteSpace(title) ? currentTitle : title;
                currentDeveloper = string.IsNullOrWhiteSpace(developer) ? currentDeveloper : developer;

                milestones.Add(new MilestoneDto
                {
                    SlNo = currentSlNo,
                    Program = currentProgram,
                    Title = currentTitle,
                    Developer = currentDeveloper,
                    Milestone = milestone,
                    Description = row.Cell(headers["Description"]).GetString().Trim(),
                    DeliveryDate = ParseDate(row.Cell(headers["Delivery Date"]), row.RowNumber(), "Delivery Date"),
                    MsApprovalDate = ParseDate(row.Cell(headers["MS Approval Date"]), row.RowNumber(), "MS Approval Date"),
                    ReleaseDate = ParseDate(row.Cell(headers["Release Date"]), row.RowNumber(), "Release Date")
                });
            }
        }

        return milestones;
    }

    private static ExcelWorkbookSourceRequest CreateSourceRequest(MilestoneSourceSettings settings)
    {
        var sourceType = string.IsNullOrWhiteSpace(settings.SourceType)
            ? ExcelWorkbookSourceTypes.LocalFile
            : settings.SourceType.Trim();
        return sourceType switch
        {
            ExcelWorkbookSourceTypes.LocalFile => new(sourceType, LocalPath: settings.ExcelPath),
            ExcelWorkbookSourceTypes.ExcelUrl => new(sourceType, SourceUrl: settings.SourceUrl),
            ExcelWorkbookSourceTypes.UploadedExcel => new(
                sourceType,
                ManagedReference: settings.SourceUrl),
            ExcelWorkbookSourceTypes.MicrosoftGraphExcel => new(
                sourceType,
                DriveId: Optional(settings.SourceDriveId),
                ItemId: Optional(settings.SourceItemId),
                SharingUrl: Optional(settings.SourceUrl)),
            _ => throw new InvalidOperationException($"MilestoneConfiguration:SourceType '{sourceType}' is not supported.")
        };
    }

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? ParseDate(IXLCell cell, int rowNumber, string field)
    {
        if (string.IsNullOrWhiteSpace(cell.GetString()))
        {
            return null;
        }

        if (cell.TryGetValue<DateTime>(out var date))
        {
            return date;
        }

        throw new InvalidDataException($"Invalid {field} on row {rowNumber}.");
    }

    private static IXLRow? FindHeaderRow(IXLWorksheet worksheet)
    {
        var requiredHeaders = new[] { "Sl. No", "Program", "Title", "Developer", "Milestone", "Description", "Delivery Date", "MS Approval Date", "Release Date" };
        return worksheet.RowsUsed().FirstOrDefault(row =>
        {
            var headers = row.CellsUsed()
                .Select(cell => NormalizeHeader(cell.GetString()))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return requiredHeaders.All(headers.Contains);
        });
    }

    private static Dictionary<string, int> GetHeaderMap(IXLRow headerRow)
    {
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
        {
            headers[NormalizeHeader(cell.GetString())] = cell.Address.ColumnNumber;
        }

        return headers;
    }

    private static string NormalizeHeader(string value)
        => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public static class MilestoneTrackerServiceCollectionExtensions
{
    public static IServiceCollection AddMilestoneTracker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddExcelWorkbookSources(configuration);
        services.Configure<MilestoneConfigurationOptions>(configuration.GetSection(MilestoneConfigurationOptions.SectionName));
        services.AddScoped<IMilestoneConfigurationService, SqliteMilestoneConfigurationService>();
        services.AddScoped<IMilestoneTrackerService, ExcelMilestoneTrackerService>();
        return services;
    }
}
