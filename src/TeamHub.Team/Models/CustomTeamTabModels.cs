namespace TeamHub.Team;

public static class CustomTeamFieldTypes
{
    public const string Text = "Text";
    public const string LongText = "LongText";
    public const string Number = "Number";
    public const string Date = "Date";
    public const string Boolean = "Boolean";
    public const string Choice = "Choice";
    public const string Person = "Person";
    public const string Url = "Url";

    public static IReadOnlyList<string> All { get; } = [Text, LongText, Number, Date, Boolean, Choice, Person, Url];

    public static string Normalize(string? value) => All.FirstOrDefault(
        item => string.Equals(item, value?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Select a supported column type.", nameof(value));
}

public static class CustomTeamTableSourceTypes
{
    public const string Manual = "Manual";
    public const string ExcelUrl = "ExcelUrl";
    public const string MicrosoftGraphExcel = "MicrosoftGraphExcel";
    public const string UploadedExcel = "UploadedExcel";
    public static IReadOnlyList<string> All { get; } = [Manual, ExcelUrl, MicrosoftGraphExcel, UploadedExcel];

    public static bool IsExcelBacked(string? value) =>
        !string.Equals(value, Manual, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? value) => All.FirstOrDefault(
        item => string.Equals(item, value?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Select a supported table source.", nameof(value));
}

public static class CustomTeamRowSourceStatuses
{
    public const string Manual = "Manual";
    public const string Active = "Active";
    public const string Missing = "Missing";
}

public sealed class CustomTeamTabDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public IReadOnlyList<CustomTeamTableDto> Tables { get; set; } = [];
}

public sealed class CustomTeamTableDto
{
    public string Id { get; set; } = string.Empty;
    public string TabId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public string SourceType { get; set; } = CustomTeamTableSourceTypes.Manual;
    public string? SourceUrl { get; set; }
    public string? SourceDriveId { get; set; }
    public string? SourceItemId { get; set; }
    public string? SourceDisplayName { get; set; }
    public string? SourceWorksheet { get; set; }
    public int SourceHeaderRow { get; set; } = 1;
    public string? PrimaryKeySourceHeader { get; set; }
    public DateTime? LastSyncedAtUtc { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastSyncMessage { get; set; }
    public bool IsExcelBacked => CustomTeamTableSourceTypes.IsExcelBacked(SourceType);
    public bool IsDirectExcelUrl => SourceType == CustomTeamTableSourceTypes.ExcelUrl;
    public bool IsMicrosoftGraphExcel => SourceType == CustomTeamTableSourceTypes.MicrosoftGraphExcel;
    public bool IsUploadedExcel => SourceType == CustomTeamTableSourceTypes.UploadedExcel;
    public IReadOnlyList<CustomTeamColumnDto> Columns { get; set; } = [];
    public IReadOnlyList<CustomTeamRowDto> Rows { get; set; } = [];
}

public sealed class CustomTeamColumnDto
{
    public string Id { get; set; } = string.Empty;
    public string TableId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string FieldType { get; set; } = CustomTeamFieldTypes.Text;
    public bool IsRequired { get; set; }
    public IReadOnlyList<string> Options { get; set; } = [];
    public int DisplayOrder { get; set; }
    public bool IsSourceColumn { get; set; }
    public string? SourceHeader { get; set; }
    public bool IsPrimaryKey { get; set; }
}

public sealed class CustomTeamRowDto
{
    public string Id { get; set; } = string.Empty;
    public string TableId { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> Values { get; set; } = new Dictionary<string, string>();
    public int Version { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? SourceKey { get; set; }
    public string SourceStatus { get; set; } = CustomTeamRowSourceStatuses.Manual;
    public DateTime? LastSeenAtUtc { get; set; }
    public bool IsMissingFromSource => SourceStatus == CustomTeamRowSourceStatuses.Missing;
}

public sealed record SaveCustomTeamTabRequest(string? Id, string Name, int DisplayOrder = 0);
public sealed record SaveCustomTeamTableRequest(
    string TabId,
    string? Id,
    string Name,
    int DisplayOrder = 0,
    string SourceType = CustomTeamTableSourceTypes.Manual,
    string? SourceUrl = null,
    string? SourceWorksheet = null,
    int SourceHeaderRow = 1,
    string? PrimaryKeySourceHeader = null,
    string? SourceDriveId = null,
    string? SourceItemId = null,
    string? SourceDisplayName = null);
public sealed record SaveCustomTeamColumnRequest(
    string TableId,
    string? Id,
    string Label,
    string FieldType,
    bool IsRequired,
    IReadOnlyList<string> Options,
    int DisplayOrder = 0);
public sealed record SaveCustomTeamRowRequest(
    string TableId,
    string? Id,
    int Version,
    IReadOnlyDictionary<string, string?> Values,
    string Actor);
public sealed record ExcelTableSyncResult(int Added, int Updated, int Missing, int Restored, DateTime SyncedAtUtc);
public sealed record ExcelSchemaImportResult(
    int Added,
    int Existing,
    int MissingFromWorkbook,
    string Worksheet,
    int HeaderCount);
