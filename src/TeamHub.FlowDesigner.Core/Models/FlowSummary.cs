namespace TeamHub.FlowDesigner.Core.Models;

public sealed record FlowSummary(
    Guid Id,
    string Name,
    string Description,
    DiagramType DiagramType,
    int NodeCount,
    DateTimeOffset UpdatedAt,
    int Version,
    string? CreatedBy);
