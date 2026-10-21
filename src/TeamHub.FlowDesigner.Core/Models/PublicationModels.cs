namespace TeamHub.FlowDesigner.Core.Models;

public enum FlowPublicationRequestStatus
{
    Pending,
    Approved,
    Rejected,
    Deleted
}

public enum FlowPublicationReviewerStatus
{
    Pending,
    Approved
}

public sealed record FlowPublicationReviewer(
    string UserId,
    string DisplayName,
    FlowPublicationReviewerStatus Status,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote);

public sealed record FlowReviewerOption(string UserId, string DisplayName);

public sealed record FlowPublicationRequestSummary(
    Guid Id,
    Guid FlowId,
    string FlowName,
    DiagramType DiagramType,
    int SourceVersion,
    int NodeCount,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    FlowPublicationRequestStatus Status,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote,
    string? DeletedBy,
    DateTimeOffset? DeletedAt);

public sealed record PublishedFlowSummary(
    Guid Id,
    Guid SourceFlowId,
    string Name,
    string Description,
    DiagramType DiagramType,
    int NodeCount,
    int SourceVersion,
    int PublicationVersion,
    DateTimeOffset PublishedAt,
    string PublishedBy);

public sealed record PublishedFlow(
    PublishedFlowSummary Summary,
    FlowDefinition Definition);

public sealed record FlowPublicationRequest(
    FlowPublicationRequestSummary Summary,
    FlowDefinition Snapshot);
