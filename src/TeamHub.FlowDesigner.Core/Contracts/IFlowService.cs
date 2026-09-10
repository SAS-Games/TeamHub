using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Core.Validation;

namespace TeamHub.FlowDesigner.Core.Contracts;

public interface IFlowService
{
    Task<IReadOnlyList<FlowSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<FlowDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<FlowDefinition> CreateAsync(string name, string? description = null, DiagramType diagramType = DiagramType.StandardFlowchart, FlowTemplate template = FlowTemplate.Blank, CancellationToken cancellationToken = default);
    Task<FlowValidationResult> SaveAsync(FlowDefinition flow, CancellationToken cancellationToken = default);
    Task<NodeComment> AddNodeCommentAsync(Guid flowId, string nodeId, string body, CancellationToken cancellationToken = default);
    Task<FlowDefinition> SetSharedAsync(Guid id, bool isShared, CancellationToken cancellationToken = default);
    Task RenameAsync(Guid id, string name, CancellationToken cancellationToken = default);
    Task<FlowDefinition> DuplicateAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
