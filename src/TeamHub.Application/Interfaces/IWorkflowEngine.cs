using TeamHub.Application.Models;

namespace TeamHub.Application.Interfaces;

public interface IWorkflowEngine
{
    Task<Guid> StartWorkflowAsync(StartWorkflowRequest request, CancellationToken cancellationToken = default);
    Task<bool> CompleteStepAsync(StepCompletionRequest request, CancellationToken cancellationToken = default);
    Task<int> RunReminderCycleAsync(CancellationToken cancellationToken = default);
    Task CancelWorkflowAsync(Guid workflowInstanceId, string actor, CancellationToken cancellationToken = default);
}
