using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Core.Validation;

namespace TeamHub.FlowDesigner.Services;

public sealed class FlowService(
    IFlowRepository repository,
    IFlowValidator validator,
    IFlowSerializer serializer,
    ICurrentUserProvider currentUser,
    IFlowPermissionService permissions) : IFlowService
{
    public async Task<IReadOnlyList<FlowSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var flows = await repository.ListAsync(cancellationToken);
        return flows.Where(flow => permissions.CanView(flow.CreatedBy)).ToList();
    }

    public async Task<FlowDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var flow = await repository.GetAsync(id, cancellationToken);
        return flow is not null && permissions.CanView(flow.CreatedBy) ? flow : null;
    }

    public async Task<FlowDefinition> CreateAsync(string name, string? description = null, CancellationToken cancellationToken = default)
    {
        if (!permissions.CanCreate())
        {
            throw new UnauthorizedAccessException("The current user cannot create flow diagrams.");
        }

        var now = DateTimeOffset.UtcNow;
        var flow = new FlowDefinition
        {
            Name = NormalizeName(name),
            Description = description?.Trim() ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = currentUser.GetCurrentUserId()
        };
        await repository.SaveAsync(flow, cancellationToken);
        return flow;
    }

    public async Task<FlowValidationResult> SaveAsync(FlowDefinition flow, CancellationToken cancellationToken = default)
    {
        flow.Name = NormalizeName(flow.Name);
        flow.Description = flow.Description?.Trim() ?? string.Empty;
        var existing = await repository.GetAsync(flow.Id, cancellationToken);
        if (existing is null)
        {
            throw new KeyNotFoundException($"Flow '{flow.Id}' was not found.");
        }

        if (!permissions.CanEdit(existing.CreatedBy))
        {
            throw new UnauthorizedAccessException("The current user cannot edit this flow diagram.");
        }

        var result = validator.Validate(flow);
        if (!result.IsValid)
        {
            return result;
        }

        flow.CreatedAt = existing.CreatedAt;
        flow.CreatedBy = existing.CreatedBy;
        flow.Version = Math.Max(existing.Version + 1, flow.Version);
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.SaveAsync(flow, cancellationToken);
        return result;
    }

    public async Task RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        var flow = await RequiredFlowAsync(id, requireEdit: true, cancellationToken);
        flow.Name = NormalizeName(name);
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        flow.Version++;
        await repository.SaveAsync(flow, cancellationToken);
    }

    public async Task<FlowDefinition> DuplicateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!permissions.CanCreate())
        {
            throw new UnauthorizedAccessException("The current user cannot create flow diagrams.");
        }

        var source = await RequiredFlowAsync(id, requireEdit: false, cancellationToken);
        var copy = serializer.Deserialize(serializer.Serialize(source));
        var now = DateTimeOffset.UtcNow;
        copy.Id = Guid.NewGuid();
        copy.Name = $"{source.Name} (copy)";
        copy.CreatedAt = now;
        copy.UpdatedAt = now;
        copy.CreatedBy = currentUser.GetCurrentUserId();
        copy.Version = 1;
        await repository.SaveAsync(copy, cancellationToken);
        return copy;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await RequiredFlowAsync(id, requireEdit: true, cancellationToken);
        await repository.DeleteAsync(id, cancellationToken);
    }

    private async Task<FlowDefinition> RequiredFlowAsync(Guid id, bool requireEdit, CancellationToken cancellationToken)
    {
        var flow = await repository.GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Flow '{id}' was not found.");
        var allowed = requireEdit ? permissions.CanEdit(flow.CreatedBy) : permissions.CanView(flow.CreatedBy);
        return allowed ? flow : throw new UnauthorizedAccessException("The current user cannot access this flow diagram.");
    }

    private static string NormalizeName(string? name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("A flow name is required.", nameof(name));
        }

        return normalized.Length <= 200 ? normalized : normalized[..200];
    }
}
