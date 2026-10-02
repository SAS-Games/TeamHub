using TeamHub.Application.Models;
using TeamHub.Domain.Enums;

namespace TeamHub.Application.Interfaces;

public interface IWorkflowReadService
{
    Task<DashboardSummaryDto> GetDashboardSummaryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowSummaryDto>> GetWorkflowSummariesAsync(
        WorkflowInstanceStatus? status = null,
        int? limit = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskItemDto>> GetMyTasksAsync(string owner, bool includeAllOwners = false, CancellationToken cancellationToken = default);
}
