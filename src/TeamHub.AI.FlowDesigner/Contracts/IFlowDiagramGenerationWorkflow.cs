using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.AI.FlowDesigner.Contracts;

public interface IFlowDiagramGenerationWorkflow
{
    Task<FlowDiagramGenerationDraft> CreateDraftAsync(
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

public sealed record FlowDiagramGenerationDraft(
    FlowDefinition Diagram,
    IReadOnlyList<FlowDesignerEvidenceReference> Evidence,
    IReadOnlyList<string> Warnings,
    string Provider,
    string Model);

public sealed record FlowDesignerEvidenceReference(
    string ElementId,
    string SourceDocumentId,
    string? Location,
    string Explanation);
