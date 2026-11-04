using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using TeamHub.AI.Usage.Contracts;

namespace TeamHub.Web.AI;

public sealed record AiUsageReportExportContext(
    DateOnly FromDate,
    DateOnly ToDate,
    string UsageGrouping,
    string UsagePrimaryHeading,
    string? UsageSecondaryHeading,
    string? UsageTertiaryHeading,
    AiUsageReport UsageReport,
    string ToolGrouping,
    string ToolPrimaryHeading,
    string? ToolSecondaryHeading,
    AiToolActivityReport ToolReport);

public interface IAiUsageReportExporter
{
    byte[] ExportUsageCsv(AiUsageReportExportContext context);
    byte[] ExportToolActivityCsv(AiUsageReportExportContext context);
    byte[] ExportExcel(AiUsageReportExportContext context);
}

public sealed class AiUsageReportExporter : IAiUsageReportExporter
{
    private static readonly XLColor TitleColor = XLColor.FromHtml("#16324F");
    private static readonly XLColor HeaderColor = XLColor.FromHtml("#DCE6F1");

    public byte[] ExportUsageCsv(AiUsageReportExportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var rows = new List<IReadOnlyList<string?>>();
        var headers = new List<string?> { context.UsagePrimaryHeading };
        if (context.UsageSecondaryHeading is not null) headers.Add(context.UsageSecondaryHeading);
        if (context.UsageTertiaryHeading is not null) headers.Add(context.UsageTertiaryHeading);
        headers.AddRange(["Calls", "Measured calls", "Input tokens", "Output tokens", "Total tokens"]);
        rows.Add(headers);
        foreach (var row in context.UsageReport.Rows)
        {
            var values = new List<string?> { row.Key };
            if (context.UsageSecondaryHeading is not null) values.Add(row.SecondaryKey);
            if (context.UsageTertiaryHeading is not null) values.Add(row.TertiaryKey);
            values.AddRange(
            [
                Invariant(row.RequestCount),
                Invariant(row.MeasuredRequestCount),
                Invariant(row.InputTokens),
                Invariant(row.OutputTokens),
                Invariant(row.TotalTokens)
            ]);
            rows.Add(values);
        }
        return Csv(rows);
    }

    public byte[] ExportToolActivityCsv(AiUsageReportExportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var rows = new List<IReadOnlyList<string?>>();
        var headers = new List<string?> { context.ToolPrimaryHeading };
        if (context.ToolSecondaryHeading is not null) headers.Add(context.ToolSecondaryHeading);
        headers.AddRange(["Calls", "Completed", "Failed", "Cancelled", "Average duration (ms)"]);
        rows.Add(headers);
        foreach (var row in context.ToolReport.Rows)
        {
            var values = new List<string?> { row.Key };
            if (context.ToolSecondaryHeading is not null) values.Add(row.SecondaryKey);
            values.AddRange(
            [
                Invariant(row.CallCount),
                Invariant(row.CompletedCount),
                Invariant(row.FailedCount),
                Invariant(row.CancelledCount),
                Invariant(row.AverageDurationMilliseconds)
            ]);
            rows.Add(values);
        }
        return Csv(rows);
    }

    public byte[] ExportExcel(AiUsageReportExportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var workbook = new XLWorkbook();
        workbook.Properties.Title = "TeamHub AI Usage Ledger";
        WriteUsageWorksheet(workbook.Worksheets.Add("Usage"), context);
        WriteToolWorksheet(workbook.Worksheets.Add("Tool activity"), context);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteUsageWorksheet(
        IXLWorksheet worksheet,
        AiUsageReportExportContext context)
    {
        var dimensionColumns = 1
            + (context.UsageSecondaryHeading is null ? 0 : 1)
            + (context.UsageTertiaryHeading is null ? 0 : 1);
        var columnCount = dimensionColumns + 5;
        WriteTitleAndPeriod(worksheet, "AI usage", context.UsageGrouping, context, columnCount);
        var totals = context.UsageReport.Totals;
        WriteSummary(
            worksheet,
            ["Provider calls", "Measured calls", "Input tokens", "Output tokens", "Total tokens"],
            [totals.RequestCount, totals.MeasuredRequestCount, totals.InputTokens, totals.OutputTokens, totals.TotalTokens]);

        var headers = new List<string> { context.UsagePrimaryHeading };
        if (context.UsageSecondaryHeading is not null) headers.Add(context.UsageSecondaryHeading);
        if (context.UsageTertiaryHeading is not null) headers.Add(context.UsageTertiaryHeading);
        headers.AddRange(["Calls", "Measured calls", "Input tokens", "Output tokens", "Total tokens"]);
        WriteHeaders(worksheet, 8, headers);

        var rowNumber = 9;
        foreach (var row in context.UsageReport.Rows)
        {
            var column = 1;
            worksheet.Cell(rowNumber, column++).SetValue(row.Key);
            if (context.UsageSecondaryHeading is not null)
                worksheet.Cell(rowNumber, column++).SetValue(row.SecondaryKey ?? string.Empty);
            if (context.UsageTertiaryHeading is not null)
                worksheet.Cell(rowNumber, column++).SetValue(row.TertiaryKey ?? string.Empty);
            WriteNumbers(worksheet, rowNumber, column,
                row.RequestCount, row.MeasuredRequestCount, row.InputTokens, row.OutputTokens, row.TotalTokens);
            rowNumber++;
        }
        FinishWorksheet(worksheet, 8, rowNumber, columnCount, dimensionColumns + 1);
    }

    private static void WriteToolWorksheet(
        IXLWorksheet worksheet,
        AiUsageReportExportContext context)
    {
        var dimensionColumns = 1 + (context.ToolSecondaryHeading is null ? 0 : 1);
        var columnCount = dimensionColumns + 5;
        WriteTitleAndPeriod(worksheet, "AI tool activity", context.ToolGrouping, context, columnCount);
        var totals = context.ToolReport.Totals;
        WriteSummary(
            worksheet,
            ["Tool calls", "Completed", "Failed", "Cancelled", "External calls", "Total duration (ms)"],
            [totals.CallCount, totals.CompletedCount, totals.FailedCount, totals.CancelledCount,
                totals.ExternalCallCount, totals.TotalDurationMilliseconds]);

        var headers = new List<string> { context.ToolPrimaryHeading };
        if (context.ToolSecondaryHeading is not null) headers.Add(context.ToolSecondaryHeading);
        headers.AddRange(["Calls", "Completed", "Failed", "Cancelled", "Average duration (ms)"]);
        WriteHeaders(worksheet, 8, headers);

        var rowNumber = 9;
        foreach (var row in context.ToolReport.Rows)
        {
            var column = 1;
            worksheet.Cell(rowNumber, column++).SetValue(row.Key);
            if (context.ToolSecondaryHeading is not null)
                worksheet.Cell(rowNumber, column++).SetValue(row.SecondaryKey ?? string.Empty);
            WriteNumbers(worksheet, rowNumber, column,
                row.CallCount, row.CompletedCount, row.FailedCount, row.CancelledCount,
                row.AverageDurationMilliseconds);
            rowNumber++;
        }
        FinishWorksheet(worksheet, 8, rowNumber, columnCount, dimensionColumns + 1);
    }

    private static void WriteTitleAndPeriod(
        IXLWorksheet worksheet,
        string title,
        string grouping,
        AiUsageReportExportContext context,
        int columnCount)
    {
        var titleRange = worksheet.Range(1, 1, 1, columnCount).Merge();
        titleRange.Value = title;
        titleRange.Style.Fill.BackgroundColor = TitleColor;
        titleRange.Style.Font.FontColor = XLColor.White;
        titleRange.Style.Font.Bold = true;
        titleRange.Style.Font.FontSize = 16;
        worksheet.Cell(2, 1).SetValue("Period (UTC)");
        worksheet.Cell(2, 2).SetValue($"{context.FromDate:yyyy-MM-dd} to {context.ToDate:yyyy-MM-dd}");
        worksheet.Cell(3, 1).SetValue("Grouped by");
        worksheet.Cell(3, 2).SetValue(grouping);
        worksheet.Range(2, 1, 3, 1).Style.Font.Bold = true;
    }

    private static void WriteSummary(
        IXLWorksheet worksheet,
        IReadOnlyList<string> labels,
        IReadOnlyList<long> values)
    {
        for (var index = 0; index < labels.Count; index++)
        {
            worksheet.Cell(5, index + 1).SetValue(labels[index]);
            worksheet.Cell(6, index + 1).SetValue(values[index]);
        }
        var range = worksheet.Range(5, 1, 5, labels.Count);
        range.Style.Fill.BackgroundColor = HeaderColor;
        range.Style.Font.Bold = true;
        worksheet.Range(6, 1, 6, values.Count).Style.NumberFormat.Format = "#,##0";
    }

    private static void WriteHeaders(IXLWorksheet worksheet, int row, IReadOnlyList<string> headers)
    {
        for (var index = 0; index < headers.Count; index++)
            worksheet.Cell(row, index + 1).SetValue(headers[index]);
        var range = worksheet.Range(row, 1, row, headers.Count);
        range.Style.Fill.BackgroundColor = HeaderColor;
        range.Style.Font.Bold = true;
        range.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
    }

    private static void WriteNumbers(
        IXLWorksheet worksheet,
        int row,
        int firstColumn,
        params long[] values)
    {
        for (var index = 0; index < values.Length; index++)
            worksheet.Cell(row, firstColumn + index).SetValue(values[index]);
    }

    private static void FinishWorksheet(
        IXLWorksheet worksheet,
        int headerRow,
        int nextRow,
        int columnCount,
        int firstNumericColumn)
    {
        var finalRow = Math.Max(headerRow, nextRow - 1);
        worksheet.Range(headerRow, 1, finalRow, columnCount).SetAutoFilter();
        if (nextRow > headerRow + 1)
            worksheet.Range(headerRow + 1, firstNumericColumn, finalRow, columnCount)
                .Style.NumberFormat.Format = "#,##0";
        worksheet.SheetView.FreezeRows(headerRow);
        worksheet.Columns(1, columnCount).AdjustToContents();
        foreach (var column in worksheet.Columns(1, columnCount))
            if (column.Width > 45) column.Width = 45;
    }

    private static byte[] Csv(IEnumerable<IReadOnlyList<string?>> rows)
    {
        var builder = new StringBuilder("\uFEFF");
        foreach (var row in rows)
        {
            builder.AppendJoin(',', row.Select(CsvCell));
            builder.Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string CsvCell(string? value)
    {
        var safe = value ?? string.Empty;
        if (safe.Length > 0 && safe[0] is '=' or '+' or '-' or '@') safe = $"'{safe}";
        return safe.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : safe;
    }

    private static string Invariant(long value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
