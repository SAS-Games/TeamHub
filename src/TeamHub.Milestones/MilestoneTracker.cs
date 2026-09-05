using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
}

public sealed class MilestoneConfigurationOptions
{
    public const string SectionName = "MilestoneConfiguration";
    public string ExcelPath { get; set; } = string.Empty;
}

internal sealed class ExcelMilestoneTrackerService(IOptions<MilestoneConfigurationOptions> options) : IMilestoneTrackerService
{
    public Task<IReadOnlyList<MilestoneDto>> GetMilestonesAsync(CancellationToken cancellationToken = default)
    {
        var filePath = options.Value.ExcelPath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("MilestoneConfiguration:ExcelPath is not configured.");
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Milestone tracker Excel file not found: {filePath}");
        }

        using var workbook = new XLWorkbook(filePath);
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

        return Task.FromResult<IReadOnlyList<MilestoneDto>>(milestones);
    }

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
        services.Configure<MilestoneConfigurationOptions>(configuration.GetSection(MilestoneConfigurationOptions.SectionName));
        services.AddScoped<IMilestoneTrackerService, ExcelMilestoneTrackerService>();
        return services;
    }
}
