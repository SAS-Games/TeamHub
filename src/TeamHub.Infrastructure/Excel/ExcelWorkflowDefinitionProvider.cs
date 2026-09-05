using ClosedXML.Excel;
using Microsoft.Extensions.Options;
using TeamHub.Application.Interfaces;
using TeamHub.Application.Models;
using TeamHub.Infrastructure.Options;

namespace TeamHub.Infrastructure.Excel;

public sealed class ExcelWorkflowDefinitionProvider(IOptions<WorkflowConfigurationOptions> options): IWorkflowDefinitionProvider
{
    public Task<IReadOnlyList<WorkflowDefinitionDto>> LoadDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        var filePath = options.Value.ExcelPath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("WorkflowConfiguration:ExcelPath is not configured.");
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Workflow configuration Excel file not found: {filePath}");
        }

        using var workbook = new XLWorkbook(filePath);

        var workflows = LoadSheetPerWorkflowDefinitions(workbook);
        if (workflows.Count == 0)
        {
            throw new InvalidDataException("No valid workflow definitions were found in the Excel workbook. Expected one sheet per workflow with step columns.");
        }

        return Task.FromResult<IReadOnlyList<WorkflowDefinitionDto>>(workflows.Values.ToList());
    }

    private static Dictionary<string, WorkflowDefinitionDto> LoadSheetPerWorkflowDefinitions(XLWorkbook workbook)
    {
        var workflows = new Dictionary<string, WorkflowDefinitionDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var worksheet in workbook.Worksheets)
        {
            if (string.Equals(worksheet.Name, "Workflows", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(worksheet.Name, "WorkflowSteps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var headerMap = GetHeaderMap(worksheet);
            if (headerMap.Count == 0 || !headerMap.ContainsKey("StepKey") && !headerMap.ContainsKey("StepName"))
            {
                continue;
            }

            var workflowKey = worksheet.Name.Trim();
            if (string.IsNullOrWhiteSpace(workflowKey))
            {
                continue;
            }

            var workflow = new WorkflowDefinitionDto
            {
                WorkflowKey = workflowKey,
                WorkflowName = worksheet.Name.Trim(),
                Description = null,
                Enabled = true
            };

            foreach (var row in worksheet.RowsUsed().Skip(1))
            {
                if (IsBlankRow(row))
                {
                    continue;
                }

                var stepKey = GetCellValue(row, headerMap, "StepKey");
                if (string.IsNullOrWhiteSpace(stepKey))
                {
                    continue;
                }

                workflow.Steps.Add(new WorkflowStepDefinitionDto
                {
                    StepKey = stepKey,
                    StepName = NullIfBlank(GetCellValue(row, headerMap, "StepName")) ?? stepKey,
                    Description = NullIfBlank(GetCellValue(row, headerMap, "Description")),
                    OwnerType = NullIfBlank(GetCellValue(row, headerMap, "OwnerType")) ?? "Email",
                    Owner = NullIfBlank(GetCellValue(row, headerMap, "Owner")) ?? string.Empty,
                    ExpectedDurationHours = ParseDouble(GetCellValue(row, headerMap, "ExpectedDurationHours"), "ExpectedDurationHours", workflowKey, stepKey),
                    DependsOn = ParseDependsOn(GetCellValue(row, headerMap, "DependsOn")),
                    ReminderAfterHours = ParseNullableDouble(GetCellValue(row, headerMap, "ReminderAfterHours")),
                    ReminderRepeatHours = ParseNullableDouble(GetCellValue(row, headerMap, "ReminderRepeatHours")),
                    EscalationAfterHours = ParseNullableDouble(GetCellValue(row, headerMap, "EscalationAfterHours")),
                    EscalationOwner = NullIfBlank(GetCellValue(row, headerMap, "EscalationOwner")),
                    Required = ParseBoolean(GetCellValue(row, headerMap, "Required"), "Required", workflowKey, stepKey),
                    Enabled = ParseBoolean(GetCellValue(row, headerMap, "Enabled"), "Enabled", workflowKey, stepKey),
                    SortOrder = ParseInt(GetCellValue(row, headerMap, "SortOrder"), "SortOrder", workflowKey, stepKey)
                });
            }

            if (workflow.Steps.Count > 0)
            {
                workflows[workflowKey] = workflow;
            }
        }

        return workflows;
    }

    private static Dictionary<string, int> GetHeaderMap(IXLWorksheet worksheet)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var firstRow = worksheet.FirstRow();
        if (firstRow is null)
        {
            return result;
        }

        var maxColumn = worksheet.LastColumnUsed() is IXLColumn lastColumn
            ? lastColumn.ColumnNumber()
            : firstRow.LastCell().Address.ColumnNumber;

        for (var col = 1; col <= maxColumn; col++)
        {
            var cellValue = firstRow.Cell(col).GetString().Trim();
            if (!string.IsNullOrWhiteSpace(cellValue))
            {
                result[cellValue] = col;
            }
        }

        return result;
    }

    private static string GetCellValue(IXLRow row, IReadOnlyDictionary<string, int> headerMap, string headerName)
    {
        if (!headerMap.TryGetValue(headerName, out var columnIndex))
        {
            return string.Empty;
        }

        return row.Cell(columnIndex).GetString().Trim();
    }

    private static bool IsBlankRow(IXLRow row)
    {
        return row.Cells().All(cell => string.IsNullOrWhiteSpace(cell.GetString().Trim()));
    }

    private static List<string> ParseDependsOn(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-")
        {
            return new List<string>();
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ParseBoolean(string value, string field, string workflowKey, string? stepKey)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        if (value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new InvalidDataException($"Invalid boolean in {field} for workflow={workflowKey}, step={stepKey ?? "-"}.");
    }

    private static double ParseDouble(string value, string field, string workflowKey, string? stepKey)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Missing number in {field} for workflow={workflowKey}, step={stepKey ?? "-"}.");
        }

        if (double.TryParse(value, out var result))
        {
            return result;
        }

        throw new InvalidDataException($"Invalid number in {field} for workflow={workflowKey}, step={stepKey ?? "-"}.");
    }

    private static double? ParseNullableDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value, out var result) ? result : null;
    }

    private static int ParseInt(string value, string field, string workflowKey, string? stepKey)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (int.TryParse(value, out var result))
        {
            return result;
        }

        throw new InvalidDataException($"Invalid integer in {field} for workflow={workflowKey}, step={stepKey ?? "-"}.");
    }
}
