using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TeamHub.Excel;

namespace TeamHub.Team;

public sealed record ExcelTableSourceData(
    string Worksheet,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);

public sealed record ExcelTableSourceRequest(
    string SourceType,
    string? SourceUrl,
    string? SourceDriveId,
    string? SourceItemId,
    string? Worksheet,
    int HeaderRow);

public interface IExcelTableSourceReader
{
    Task<ExcelTableSourceData> ReadAsync(
        ExcelTableSourceRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StoredExcelSource(string Reference, string DisplayName);

public interface IExcelSourceFileStore
{
    Task<StoredExcelSource> SaveAsync(
        Stream source,
        string fileName,
        long length,
        CancellationToken cancellationToken = default);
}

internal sealed partial class DirectDownloadExcelTableSourceReader(IExcelWorkbookSource workbookSource) : IExcelTableSourceReader
{
    internal const int MaximumWorkbookBytes = ExcelWorkbookLimits.MaximumWorkbookBytes;
    private const int MaximumRows = 50_000;
    private const int MaximumColumns = 200;

    public async Task<ExcelTableSourceData> ReadAsync(
        ExcelTableSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.HeaderRow is < 1 or > 1000)
            throw new ArgumentException("Header row must be between 1 and 1,000.", nameof(request));

        var sourceRequest = request.SourceType switch
        {
            CustomTeamTableSourceTypes.ExcelUrl => new ExcelWorkbookSourceRequest(
                ExcelWorkbookSourceTypes.ExcelUrl,
                SourceUrl: request.SourceUrl),
            CustomTeamTableSourceTypes.UploadedExcel => new ExcelWorkbookSourceRequest(
                ExcelWorkbookSourceTypes.UploadedExcel,
                ManagedReference: request.SourceUrl),
            CustomTeamTableSourceTypes.MicrosoftGraphExcel => new ExcelWorkbookSourceRequest(
                ExcelWorkbookSourceTypes.MicrosoftGraphExcel,
                DriveId: request.SourceDriveId,
                ItemId: request.SourceItemId,
                SharingUrl: request.SourceUrl),
            _ => throw new InvalidOperationException("The configured Excel source is not supported.")
        };

        await using var workbook = await workbookSource.OpenAsync(sourceRequest, cancellationToken);
        return ReadWorkbook(workbook, request.Worksheet, request.HeaderRow);
    }

    internal static ExcelTableSourceData ReadWorkbook(Stream stream, string? requestedWorksheet, int headerRow)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace officeRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace packageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

            var workbook = LoadXml(RequiredEntry(archive, "xl/workbook.xml"));
            var sheets = workbook.Descendants(spreadsheet + "sheet").ToList();
            if (sheets.Count == 0) throw new InvalidOperationException("The Excel workbook does not contain a worksheet.");
            var sheet = string.IsNullOrWhiteSpace(requestedWorksheet)
                ? sheets[0]
                : sheets.FirstOrDefault(item => string.Equals((string?)item.Attribute("name"), requestedWorksheet.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Worksheet '{requestedWorksheet.Trim()}' was not found.");
            var worksheetName = (string?)sheet.Attribute("name") ?? "Sheet1";
            var relationshipId = (string?)sheet.Attribute(officeRelationships + "id")
                ?? throw new InvalidOperationException("The selected worksheet does not have a workbook relationship.");
            var relationships = LoadXml(RequiredEntry(archive, "xl/_rels/workbook.xml.rels"));
            var target = relationships.Descendants(packageRelationships + "Relationship")
                .FirstOrDefault(item => string.Equals((string?)item.Attribute("Id"), relationshipId, StringComparison.Ordinal))?
                .Attribute("Target")?.Value
                ?? throw new InvalidOperationException("The selected worksheet file could not be resolved.");
            var worksheetPath = ResolveWorkbookTarget(target);
            var worksheet = LoadXml(RequiredEntry(archive, worksheetPath));
            var sharedStrings = ReadSharedStrings(archive, spreadsheet);
            var styles = ReadStyles(archive, spreadsheet);

            var rows = worksheet.Descendants(spreadsheet + "row").ToList();
            var headerElement = rows.FirstOrDefault(item => ParseRowNumber(item) == headerRow)
                ?? throw new InvalidOperationException($"Header row {headerRow} was not found in worksheet '{worksheetName}'.");
            var headerCells = ReadCells(headerElement, sharedStrings, styles, spreadsheet);
            var headersByIndex = headerCells
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .ToDictionary(item => item.Key, item => item.Value.Trim());
            if (headersByIndex.Count == 0) throw new InvalidOperationException("The configured header row is empty.");
            if (headersByIndex.Count > MaximumColumns) throw new InvalidOperationException($"Excel tables are limited to {MaximumColumns} columns.");
            var duplicateHeader = headersByIndex.Values.GroupBy(item => item, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateHeader is not null) throw new InvalidOperationException($"The Excel header '{duplicateHeader.Key}' appears more than once.");

            var importedRows = new List<IReadOnlyDictionary<string, string>>();
            foreach (var row in rows.Where(item => ParseRowNumber(item) > headerRow))
            {
                var cells = ReadCells(row, sharedStrings, styles, spreadsheet);
                var values = headersByIndex.ToDictionary(
                    item => item.Value,
                    item => cells.GetValueOrDefault(item.Key) ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
                if (values.Values.All(string.IsNullOrWhiteSpace)) continue;
                importedRows.Add(values);
                if (importedRows.Count > MaximumRows)
                    throw new InvalidOperationException($"Excel tables are limited to {MaximumRows:N0} data rows.");
            }

            return new ExcelTableSourceData(worksheetName, headersByIndex.OrderBy(item => item.Key).Select(item => item.Value).ToList(), importedRows);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException("The downloaded file is not a valid .xlsx workbook.", exception);
        }
    }

    private static Dictionary<int, string> ReadCells(
        XElement row,
        IReadOnlyList<string> sharedStrings,
        IReadOnlyList<string?> styleFormats,
        XNamespace spreadsheet)
    {
        var result = new Dictionary<int, string>();
        var fallbackIndex = 0;
        foreach (var cell in row.Elements(spreadsheet + "c"))
        {
            var reference = (string?)cell.Attribute("r");
            var index = string.IsNullOrWhiteSpace(reference) ? fallbackIndex : ColumnIndex(reference);
            fallbackIndex = index + 1;
            var type = (string?)cell.Attribute("t");
            var raw = cell.Element(spreadsheet + "v")?.Value ?? string.Empty;
            string value;
            if (type == "s" && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedIndex))
                value = sharedIndex >= 0 && sharedIndex < sharedStrings.Count ? sharedStrings[sharedIndex] : string.Empty;
            else if (type == "inlineStr")
                value = string.Concat(cell.Descendants(spreadsheet + "t").Select(item => item.Value));
            else if (type == "b")
                value = raw == "1" ? "true" : "false";
            else
                value = FormatNumericOrText(raw, cell, styleFormats);
            result[index] = value;
        }
        return result;
    }

    private static string FormatNumericOrText(string raw, XElement cell, IReadOnlyList<string?> styleFormats)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return raw;
        if (!int.TryParse((string?)cell.Attribute("s"), out var styleIndex) || styleIndex < 0 || styleIndex >= styleFormats.Count)
            return raw;
        var format = styleFormats[styleIndex];
        if (string.IsNullOrWhiteSpace(format)) return raw;
        if (DateFormat().IsMatch(RemoveQuotedFormatText().Replace(format, string.Empty)))
        {
            try { return DateTime.FromOADate(number).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
            catch (ArgumentException) { return raw; }
        }
        var identifierFormat = Regex.Match(format, "^0+$");
        return identifierFormat.Success && number == Math.Truncate(number)
            ? number.ToString(identifierFormat.Value, CultureInfo.InvariantCulture)
            : raw;
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive, XNamespace spreadsheet)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        return LoadXml(entry).Descendants(spreadsheet + "si")
            .Select(item => string.Concat(item.Descendants(spreadsheet + "t").Select(text => text.Value)))
            .ToList();
    }

    private static IReadOnlyList<string?> ReadStyles(ZipArchive archive, XNamespace spreadsheet)
    {
        var entry = archive.GetEntry("xl/styles.xml");
        if (entry is null) return [];
        var document = LoadXml(entry);
        var customFormats = document.Descendants(spreadsheet + "numFmt")
            .Where(item => item.Attribute("numFmtId") is not null)
            .ToDictionary(item => (int)item.Attribute("numFmtId")!, item => (string?)item.Attribute("formatCode"));
        return document.Descendants(spreadsheet + "cellXfs").Elements(spreadsheet + "xf")
            .Select(item =>
            {
                var id = (int?)item.Attribute("numFmtId") ?? 0;
                if (customFormats.TryGetValue(id, out var custom)) return custom;
                return id is >= 14 and <= 22 ? "yyyy-mm-dd" : null;
            })
            .ToList();
    }

    private static int ParseRowNumber(XElement row) =>
        int.TryParse((string?)row.Attribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var character in reference.TakeWhile(char.IsLetter))
            index = checked(index * 26 + char.ToUpperInvariant(character) - 'A' + 1);
        return Math.Max(0, index - 1);
    }

    private static string ResolveWorkbookTarget(string target)
    {
        var uri = new Uri(new Uri("https://teamhub.invalid/xl/workbook.xml"), target.Replace('\\', '/'));
        return uri.AbsolutePath.TrimStart('/');
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static ZipArchiveEntry RequiredEntry(ZipArchive archive, string path) =>
        archive.GetEntry(path) ?? throw new InvalidOperationException($"The Excel workbook is missing '{path}'.");

    [GeneratedRegex("[ymdhis]", RegexOptions.IgnoreCase)]
    private static partial Regex DateFormat();

    [GeneratedRegex("\"[^\"]*\"|\\\\.")]
    private static partial Regex RemoveQuotedFormatText();
}
