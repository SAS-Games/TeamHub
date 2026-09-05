using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TeamHub.Team;

public sealed class TeamMemberDto
{
    public string EmployeeName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Gid { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ContactNumber { get; set; } = string.Empty;
}

public sealed class SpecializationDto
{
    public string Pod { get; set; } = string.Empty;
    public string FocusAreas { get; set; } = string.Empty;
    public string Members { get; set; } = string.Empty;
}

public sealed class TeamDirectoryDto
{
    public IReadOnlyList<TeamMemberDto> Members { get; set; } = [];
    public IReadOnlyList<SpecializationDto> Specializations { get; set; } = [];
}

public interface ITeamDirectoryService
{
    Task<TeamDirectoryDto> GetTeamDirectoryAsync(CancellationToken cancellationToken = default);
}

public sealed class TeamConfigurationOptions
{
    public const string SectionName = "TeamConfiguration";
    public string ExcelPath { get; set; } = string.Empty;
}

internal sealed class ExcelTeamDirectoryService(IOptions<TeamConfigurationOptions> options) : ITeamDirectoryService
{
    public Task<TeamDirectoryDto> GetTeamDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var filePath = options.Value.ExcelPath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("TeamConfiguration:ExcelPath is not configured.");
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Team info Excel file not found: {filePath}");
        }

        using var workbook = new XLWorkbook(filePath);

        var members = LoadMembers(workbook, cancellationToken);
        var specializations = LoadSpecializations(workbook, cancellationToken);

        return Task.FromResult(new TeamDirectoryDto
        {
            Members = members,
            Specializations = specializations
        });
    }

    private static List<TeamMemberDto> LoadMembers(XLWorkbook workbook, CancellationToken cancellationToken)
    {
        var members = new List<TeamMemberDto>();
        var worksheet = FindWorksheet(workbook, "Team Info");
        if (worksheet is null)
        {
            return members;
        }

        var headers = GetHeaderMap(worksheet);
        foreach (var row in worksheet.RowsUsed().Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = GetCellValue(row, headers, "Employee Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            members.Add(new TeamMemberDto
            {
                EmployeeName = name,
                Role = GetCellValue(row, headers, "Role"),
                Gid = GetCellValue(row, headers, "GID"),
                Email = GetCellValue(row, headers, "Email id"),
                ContactNumber = GetCellValue(row, headers, "Contact number")
            });
        }

        return members;
    }

    private static List<SpecializationDto> LoadSpecializations(XLWorkbook workbook, CancellationToken cancellationToken)
    {
        var specializations = new List<SpecializationDto>();
        var worksheet = FindWorksheet(workbook, "Support Specializations");
        if (worksheet is null)
        {
            return specializations;
        }

        var headers = GetHeaderMap(worksheet);
        foreach (var row in worksheet.RowsUsed().Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pod = GetCellValue(row, headers, "Pods");
            if (string.IsNullOrWhiteSpace(pod))
            {
                continue;
            }

            specializations.Add(new SpecializationDto
            {
                Pod = pod,
                FocusAreas = GetCellValue(row, headers, "Sub-Categories / Focus Areas"),
                Members = GetCellValue(row, headers, "Engineers / Members")
            });
        }

        return specializations;
    }

    private static IXLWorksheet? FindWorksheet(XLWorkbook workbook, string name)
        => workbook.Worksheets.FirstOrDefault(w => string.Equals(w.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, int> GetHeaderMap(IXLWorksheet worksheet)
    {
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var headerRow = worksheet.RowsUsed().FirstOrDefault();
        if (headerRow is null)
        {
            return headers;
        }

        foreach (var cell in headerRow.CellsUsed())
        {
            headers[cell.GetString().Trim()] = cell.Address.ColumnNumber;
        }

        return headers;
    }

    private static string GetCellValue(IXLRow row, Dictionary<string, int> headers, string columnName)
        => headers.TryGetValue(columnName, out var column) ? row.Cell(column).GetString().Trim() : string.Empty;
}

public static class TeamDirectoryServiceCollectionExtensions
{
    public static IServiceCollection AddTeamDirectory(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TeamConfigurationOptions>(configuration.GetSection(TeamConfigurationOptions.SectionName));
        services.AddScoped<ITeamDirectoryService, ExcelTeamDirectoryService>();
        return services;
    }
}
