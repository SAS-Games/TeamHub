using System.Text.Json.Serialization;

namespace TeamHub.FlowDesigner.Core.Validation;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationSeverity
{
    Warning,
    Error
}

public sealed record FlowValidationIssue(
    string Code,
    string Message,
    ValidationSeverity Severity,
    string? ElementId = null);

public sealed class FlowValidationResult
{
    public IReadOnlyList<FlowValidationIssue> Issues { get; init; } = [];
    public bool IsValid => Issues.All(issue => issue.Severity != ValidationSeverity.Error);
}
