using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Core.Contracts;

public interface IFlowRepository
{
    Task<IReadOnlyList<FlowSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<FlowDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task SaveAsync(FlowDefinition flow, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
