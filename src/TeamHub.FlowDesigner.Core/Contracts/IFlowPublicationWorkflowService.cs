using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Core.Contracts;

public interface IFlowPublicationWorkflowService
{
    bool CanReview { get; }
    Task<FlowPublicationRequestSummary> RequestAsync(Guid flowId, CancellationToken cancellationToken = default);
    Task<FlowPublicationRequestSummary?> GetLatestRequestAsync(Guid flowId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowPublicationRequestSummary>> ListPendingAsync(CancellationToken cancellationToken = default);
    Task<FlowPublicationRequest?> GetRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<PublishedFlowSummary> ApproveAsync(Guid requestId, string? reviewNote, CancellationToken cancellationToken = default);
    Task RejectAsync(Guid requestId, string reviewNote, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublishedFlowSummary>> ListPublishedAsync(CancellationToken cancellationToken = default);
    Task<PublishedFlow?> GetPublishedBySourceAsync(Guid sourceFlowId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowSummary>> ListPublishedBundleAsync(Guid sourceFlowId, CancellationToken cancellationToken = default);
    Task<FlowDefinition?> GetRequestDiagramAsync(Guid requestId, Guid flowId, CancellationToken cancellationToken = default);
    Task<FlowDefinition> UpdatePublishedAsync(Guid sourceFlowId, FlowDefinition flow, CancellationToken cancellationToken = default);
}
