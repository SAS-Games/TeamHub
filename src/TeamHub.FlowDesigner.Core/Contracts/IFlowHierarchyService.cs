using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Core.Validation;

namespace TeamHub.FlowDesigner.Core.Contracts;

public interface IFlowHierarchyService
{
    Task<FlowDiagramTemplateBundle> CreateDraftAsync(
        FlowDiagramTemplateBundle proposal,
        CancellationToken cancellationToken = default);

    Task DeleteDraftAsync(
        Guid rootFlowId,
        CancellationToken cancellationToken = default);
}

public interface IFlowHierarchyValidator
{
    FlowValidationResult Validate(
        FlowDiagramTemplateBundle hierarchy,
        int maximumDiagrams = 25,
        int maximumDepth = 6);
}