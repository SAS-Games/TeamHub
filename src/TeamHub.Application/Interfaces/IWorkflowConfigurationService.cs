using TeamHub.Application.Models;

namespace TeamHub.Application.Interfaces;

public interface IWorkflowConfigurationService
{
    Task<ConfigurationSyncResult> SyncAsync(string actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowDraftSummaryDto>> GetDraftsAsync(CancellationToken cancellationToken = default);
    Task<WorkflowDraftDto?> GetDraftAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Guid> SaveDraftAsync(WorkflowDraftDto draft, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ConfigurationSyncResult> PublishDraftAsync(Guid id, string actor, CancellationToken cancellationToken = default);
}
