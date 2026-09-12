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
        var visibleFlows = await ListVisibleAsync(cancellationToken);
        if (visibleFlows.Count == 0) return visibleFlows;

        var visibleIds = visibleFlows.Select(flow => flow.Id).ToHashSet();
        var linkedChildIds = (await repository.ListDefinitionsAsync(cancellationToken))
            .Where(flow => visibleIds.Contains(flow.Id))
            .SelectMany(flow => flow.Nodes)
            .Where(node => node.ChildFlowId.HasValue)
            .Select(node => node.ChildFlowId!.Value)
            .ToHashSet();

        return visibleFlows
            .Where(flow => !linkedChildIds.Contains(flow.Id))
            .ToList();
    }

    public async Task<IReadOnlyList<FlowSummary>> ListLinkTargetsAsync(
        Guid sourceFlowId,
        CancellationToken cancellationToken = default) =>
        (await ListVisibleAsync(cancellationToken))
            .Where(flow => flow.Id != sourceFlowId)
            .ToList();

    public async Task<FlowDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var flow = await repository.GetAsync(id, cancellationToken);
        return flow is not null && CanView(flow.CreatedBy, flow.IsShared) ? flow : null;
    }

    public async Task<FlowDefinition> CreateAsync(string name, string? description = null, DiagramType diagramType = DiagramType.StandardFlowchart, FlowTemplate template = FlowTemplate.Blank, CancellationToken cancellationToken = default)
    {
        if (!permissions.CanCreate())
        {
            throw new UnauthorizedAccessException("The current user cannot create flow diagrams.");
        }
        if (!permissions.CanUseTemplate(template))
        {
            throw new UnauthorizedAccessException("The current user cannot use this flow template.");
        }
        if (!permissions.CanUseDiagramType(diagramType))
        {
            throw new UnauthorizedAccessException("The current user cannot create this type of flow diagram.");
        }

        var now = DateTimeOffset.UtcNow;
        var flow = new FlowDefinition
        {
            Name = NormalizeName(name),
            Description = description?.Trim() ?? string.Empty,
            DiagramType = diagramType,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = currentUser.GetCurrentUserId(),
            Version = 0
        };
        FlowTemplateFactory.Apply(flow, template);
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
        if (result.IsValid)
        {
            var linkIssues = await ValidateChildLinksAsync(flow, cancellationToken);
            if (linkIssues.Count > 0)
            {
                result = new FlowValidationResult { Issues = [.. result.Issues, .. linkIssues] };
            }
        }
        if (result.IsValid && existing.IsShared)
        {
            try
            {
                await EnsureLinkedFlowsAreSharedAsync(flow, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                result = new FlowValidationResult
                {
                    Issues =
                    [
                        .. result.Issues,
                        new FlowValidationIssue(
                            "child-flow-private",
                            "Share every linked child diagram before linking it from this shared diagram.",
                            ValidationSeverity.Error)
                    ]
                };
            }
        }
        if (!result.IsValid)
        {
            return result;
        }

        flow.CreatedAt = existing.CreatedAt;
        flow.CreatedBy = existing.CreatedBy;
        flow.IsShared = existing.IsShared;
        flow.Version = Math.Max(existing.Version + 1, 1);
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.SaveAsync(flow, cancellationToken);
        return result;
    }

    public async Task<NodeComment> AddNodeCommentAsync(Guid flowId, string nodeId, string body, CancellationToken cancellationToken = default)
    {
        var flow = await RequiredFlowAsync(flowId, requireEdit: true, cancellationToken);
        var node = flow.Nodes.SingleOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Node '{nodeId}' was not found.");
        var normalizedBody = body?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedBody))
        {
            throw new ArgumentException("A comment is required.", nameof(body));
        }

        var comment = new NodeComment
        {
            Body = normalizedBody.Length <= 2000 ? normalizedBody : normalizedBody[..2000],
            Author = currentUser.GetCurrentUserId() ?? "Anonymous",
            CreatedAt = DateTimeOffset.UtcNow
        };
        node.Comments ??= [];
        node.Comments.Add(comment);
        flow.UpdatedAt = comment.CreatedAt;
        if (flow.Version > 0)
        {
            flow.Version++;
        }
        await repository.SaveAsync(flow, cancellationToken);
        return comment;
    }

    public async Task<FlowDefinition> SetSharedAsync(Guid id, bool isShared, CancellationToken cancellationToken = default)
    {
        var flow = await RequiredFlowAsync(id, requireEdit: true, cancellationToken);
        if (flow.Version <= 0)
        {
            throw new InvalidOperationException("Save the diagram before sharing it.");
        }

        if (flow.IsShared == isShared)
        {
            return flow;
        }
        if (isShared)
        {
            await EnsureLinkedFlowsAreSharedAsync(flow, cancellationToken);
        }

        flow.IsShared = isShared;
        flow.UpdatedAt = DateTimeOffset.UtcNow;
        flow.Version++;
        await repository.SaveAsync(flow, cancellationToken);
        return flow;
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
        copy.IsShared = false;
        copy.Version = 0;
        await repository.SaveAsync(copy, cancellationToken);
        return copy;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var flow = await repository.GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Flow '{id}' was not found.");
        if (!permissions.CanDelete(flow.CreatedBy))
        {
            throw new UnauthorizedAccessException("The current user cannot delete this flow diagram.");
        }
        var referenced = (await repository.ListDefinitionsAsync(cancellationToken))
            .Where(parent => parent.Id != id)
            .Any(parent => parent.Nodes.Any(node => node.ChildFlowId == id));
        if (referenced)
        {
            throw new InvalidOperationException("This diagram is linked from another diagram. Remove the link before deleting it.");
        }
        await repository.DeleteAsync(id, cancellationToken);
    }

    private async Task<IReadOnlyList<FlowValidationIssue>> ValidateChildLinksAsync(
        FlowDefinition flow,
        CancellationToken cancellationToken)
    {
        var linkedNodes = flow.Nodes.Where(node => node.ChildFlowId.HasValue).ToList();
        if (linkedNodes.Count == 0) return [];

        var definitions = await repository.ListDefinitionsAsync(cancellationToken);
        var byId = definitions.ToDictionary(item => item.Id);
        byId[flow.Id] = flow;
        var issues = new List<FlowValidationIssue>();

        foreach (var node in linkedNodes)
        {
            var childId = node.ChildFlowId!.Value;
            if (childId == flow.Id)
            {
                issues.Add(new("child-flow-self-link", $"'{node.Title}' cannot link to its own diagram.", ValidationSeverity.Error, node.Id));
                continue;
            }
            if (!byId.TryGetValue(childId, out var child))
            {
                issues.Add(new("child-flow-missing", $"The detailed diagram linked from '{node.Title}' no longer exists.", ValidationSeverity.Error, node.Id));
                continue;
            }
            if (!CanView(child.CreatedBy, child.IsShared))
            {
                issues.Add(new("child-flow-forbidden", $"You cannot access the detailed diagram linked from '{node.Title}'.", ValidationSeverity.Error, node.Id));
                continue;
            }
            if (Reaches(childId, flow.Id, byId, []))
            {
                issues.Add(new("child-flow-cycle", $"Linking '{node.Title}' would create a cycle in the diagram hierarchy.", ValidationSeverity.Error, node.Id));
            }
        }

        return issues;
    }

    private static bool Reaches(
        Guid currentId,
        Guid targetId,
        IReadOnlyDictionary<Guid, FlowDefinition> definitions,
        HashSet<Guid> visited)
    {
        if (currentId == targetId) return true;
        if (!visited.Add(currentId) || !definitions.TryGetValue(currentId, out var current)) return false;

        return current.Nodes
            .Where(node => node.ChildFlowId.HasValue)
            .Select(node => node.ChildFlowId!.Value)
            .Any(childId => Reaches(childId, targetId, definitions, visited));
    }

    private async Task<List<FlowSummary>> ListVisibleAsync(CancellationToken cancellationToken)
    {
        var flows = await repository.ListAsync(cancellationToken);
        return flows
            .Where(flow => flow.Version > 0 && CanView(flow.CreatedBy, flow.IsShared))
            .ToList();
    }

    private async Task EnsureLinkedFlowsAreSharedAsync(FlowDefinition flow, CancellationToken cancellationToken)
    {
        var definitions = await repository.ListDefinitionsAsync(cancellationToken);
        var byId = definitions.ToDictionary(item => item.Id);
        var pending = new Stack<Guid>(flow.Nodes
            .Where(node => node.ChildFlowId.HasValue)
            .Select(node => node.ChildFlowId!.Value));
        var visited = new HashSet<Guid>();

        while (pending.TryPop(out var childId))
        {
            if (!visited.Add(childId)) continue;
            if (!byId.TryGetValue(childId, out var child) || !child.IsShared)
            {
                throw new InvalidOperationException("Share every linked child diagram before sharing this diagram.");
            }

            foreach (var descendantId in child.Nodes
                         .Where(node => node.ChildFlowId.HasValue)
                         .Select(node => node.ChildFlowId!.Value))
            {
                pending.Push(descendantId);
            }
        }
    }

    private async Task<FlowDefinition> RequiredFlowAsync(Guid id, bool requireEdit, CancellationToken cancellationToken)
    {
        var flow = await repository.GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException($"Flow '{id}' was not found.");
        var allowed = requireEdit ? permissions.CanEdit(flow.CreatedBy) : CanView(flow.CreatedBy, flow.IsShared);
        return allowed ? flow : throw new UnauthorizedAccessException("The current user cannot access this flow diagram.");
    }

    private bool CanView(string? ownerId, bool isShared) =>
        permissions.CanView(ownerId) || isShared && permissions.CanViewShared();

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
