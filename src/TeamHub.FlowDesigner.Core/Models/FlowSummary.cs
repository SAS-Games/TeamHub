namespace TeamHub.FlowDesigner.Core.Models;

public sealed record FlowSummary(
    Guid Id,
    string Name,
    string Description,
    FlowMode Mode,
    int NodeCount,
    DateTimeOffset UpdatedAt,
    int Version,
    string? CreatedBy);
