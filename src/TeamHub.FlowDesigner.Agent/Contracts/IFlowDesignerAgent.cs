using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Agent.Contracts;

public interface IFlowDesignerAgent
{
    Task<FlowDesignerAgentDraft> CreateDraftAsync(
        CreateFlowDiagramDraftRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CreateFlowDiagramDraftRequest(
    string Prompt,
    IReadOnlyList<FlowDesignerSourceDocument> SourceDocuments);

public sealed record FlowDesignerSourceDocument(
    string Id,
    string Title,
    string MediaType,
    string Content,
    string? SourceReference = null);

public sealed record FlowDesignerAgentDraft(
    FlowDefinition Diagram,
    IReadOnlyList<FlowDesignerEvidenceReference> Evidence,
    IReadOnlyList<string> Warnings);

public sealed record FlowDesignerEvidenceReference(
    string SourceDocumentId,
    string? Location,
    string Explanation);
