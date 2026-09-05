using TeamHub.Application.Models;

namespace TeamHub.Application.Interfaces;

public interface IWorkflowDefinitionProvider
{
    Task<IReadOnlyList<WorkflowDefinitionDto>> LoadDefinitionsAsync(CancellationToken cancellationToken = default);
}
