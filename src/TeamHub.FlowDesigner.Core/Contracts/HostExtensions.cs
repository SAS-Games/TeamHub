using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Core.Contracts;

public interface ICurrentUserProvider
{
    string? GetCurrentUserId();
}

public interface IFlowPermissionService
{
    bool CanView(string? ownerId);
    bool CanViewShared();
    bool CanEdit(string? ownerId);
    bool CanDelete(string? ownerId);
    bool CanCreate();
    bool CanUseTemplate(FlowTemplate template);
    bool CanUseDiagramType(DiagramType diagramType);
    bool CanManageTemplates();
    bool CanReviewPublications();
}

public interface IFlowPublicationService
{
    bool CanPublish(FlowDefinition flow);
    Task<FlowPublicationResult> PublishAsync(FlowDefinition flow, CancellationToken cancellationToken = default);
}

public sealed record FlowPublicationResult(bool Success, string Message, IReadOnlyList<string> Errors)
{
    public static FlowPublicationResult Published(string message) => new(true, message, []);
    public static FlowPublicationResult Invalid(IReadOnlyList<string> errors) =>
        new(false, "The workflow could not be published.", errors);
}

public interface IFlowThemeProvider
{
    string? AccentColor { get; }
    string? CssClass { get; }
}

public interface IFlowNavigationProvider
{
    string FlowsPath { get; }
}
