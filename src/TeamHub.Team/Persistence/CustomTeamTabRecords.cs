namespace TeamHub.Team;

internal sealed class CustomTeamTabRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public bool IsArchived { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class CustomTeamTableRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TabId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public bool IsArchived { get; set; }
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
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class CustomTeamColumnRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TableId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string FieldType { get; set; } = CustomTeamFieldTypes.Text;
    public bool IsRequired { get; set; }
    public string OptionsJson { get; set; } = "[]";
    public int DisplayOrder { get; set; }
    public bool IsArchived { get; set; }
    public bool IsSourceColumn { get; set; }
    public string? SourceHeader { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class CustomTeamRowRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TableId { get; set; }
    public string ValuesJson { get; set; } = "{}";
    public int Version { get; set; } = 1;
    public bool IsDeleted { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string UpdatedBy { get; set; } = string.Empty;
    public string? DeletedBy { get; set; }
    public string? SourceKey { get; set; }
    public string SourceStatus { get; set; } = CustomTeamRowSourceStatuses.Manual;
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAtUtc { get; set; }
}

internal sealed class CustomTeamRowAuditRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RowId { get; set; }
    public Guid TableId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ValuesJson { get; set; } = "{}";
    public int Version { get; set; }
    public string Actor { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
