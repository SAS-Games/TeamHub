using TeamHub.Application.Models;

namespace TeamHub.Application.Interfaces;

public interface IWorkflowReadService
{
    Task<DashboardSummaryDto> GetDashboardSummaryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowSummaryDto>> GetWorkflowSummariesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItemDto>> GetMyTasksAsync(string owner, bool includeAllOwners = false, CancellationToken cancellationToken = default);
}
