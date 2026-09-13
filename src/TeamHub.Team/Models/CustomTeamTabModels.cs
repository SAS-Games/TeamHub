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
}

public sealed record SaveCustomTeamTabRequest(string? Id, string Name, int DisplayOrder = 0);
public sealed record SaveCustomTeamTableRequest(string TabId, string? Id, string Name, int DisplayOrder = 0);
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
