using TeamHub.Application.Models;

namespace TeamHub.Application.Interfaces;

public interface IWorkflowConfigurationService
{
    Task<ConfigurationSyncResult> SyncAsync(string actor, CancellationToken cancellationToken = default);
}
