using Microsoft.EntityFrameworkCore;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Persistence;

namespace TeamHub.FlowDesigner.Services;

public sealed class FlowHierarchyService(
    IDbContextFactory<FlowDesignerDbContext> contextFactory,
    IFlowSerializer serializer,
    IFlowValidator flowValidator,
    IFlowHierarchyValidator hierarchyValidator,
    ICurrentUserProvider currentUser,
    IFlowPermissionService permissions) : IFlowHierarchyService
{
    private const int MaximumDiagrams = 25;
    private const int MaximumDepth = 6;

    public async Task<FlowDiagramTemplateBundle> CreateDraftAsync(
        FlowDiagramTemplateBundle proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!permissions.CanCreate())
        {
            throw new UnauthorizedAccessException("The current user cannot create flow diagrams.");
        }

        var structure = hierarchyValidator.Validate(proposal, MaximumDiagrams, MaximumDepth);
        if (!structure.IsValid)
        {
            throw new InvalidOperationException(
                "The diagram hierarchy is invalid: "
                + string.Join(" ", structure.Issues.Select(issue => issue.Message)));
        }

        foreach (var flow in proposal.Flows)
        {
            if (!permissions.CanUseDiagramType(flow.DiagramType))
            {
                throw new UnauthorizedAccessException(
                    $"The current user cannot create the '{flow.DiagramType}' diagram type.");
            }
            if (!flowValidator.Validate(flow).IsValid)
            {
                throw new InvalidOperationException($"Diagram '{flow.Name}' contains Flow Designer validation errors.");
            }
        }

        var idMap = proposal.Flows.ToDictionary(flow => flow.Id, _ => Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var owner = currentUser.GetCurrentUserId();
        var created = new List<FlowDefinition>(proposal.Flows.Count);
        foreach (var source in proposal.Flows)
        {
            var flow = serializer.Deserialize(serializer.Serialize(source));
            flow.Id = idMap[source.Id];
            flow.Name = NormalizeRequired(source.Name, "Diagram name", 200);
            flow.Description = NormalizeOptional(source.Description, 2_000);
            flow.CreatedAt = now;
            flow.UpdatedAt = now;
            flow.CreatedBy = owner;
            flow.IsShared = false;
            flow.Version = 1;
            foreach (var node in flow.Nodes)
            {
                node.Comments = [];
                if (node.ChildFlowId.HasValue)
                {
                    node.ChildFlowId = idMap[node.ChildFlowId.Value];
                }
            }
            created.Add(flow);
        }

        var createdBundle = new FlowDiagramTemplateBundle
        {
            RootFlowId = idMap[proposal.RootFlowId],
            Flows = created
        };
        var finalValidation = hierarchyValidator.Validate(createdBundle, MaximumDiagrams, MaximumDepth);
        if (!finalValidation.IsValid)
        {
            throw new InvalidOperationException("The remapped diagram hierarchy failed Flow Designer validation.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        foreach (var flow in created)
        {
            context.Flows.Add(new FlowDefinitionEntity
            {
                Id = flow.Id,
                Name = flow.Name,
                Description = flow.Description,
                DiagramType = flow.DiagramType.ToString(),
                GraphJson = serializer.Serialize(flow),
                CreatedAt = flow.CreatedAt.UtcDateTime,
                UpdatedAt = flow.UpdatedAt.UtcDateTime,
                CreatedBy = flow.CreatedBy,
                Version = flow.Version,
                NodeCount = flow.Nodes.Count
            });
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return createdBundle;
    }

public async Task DeleteDraftAsync(Guid rootFlowId, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entities = await context.Flows.AsNoTracking().ToListAsync(cancellationToken);
        var definitions = entities.Select(entity => serializer.Deserialize(entity.GraphJson)).ToList();
        var byId = definitions.ToDictionary(flow => flow.Id);
        if (!byId.ContainsKey(rootFlowId))
        {
            throw new KeyNotFoundException($"Flow '{rootFlowId}' was not found.");
        }

        var deleting = new HashSet<Guid>();
        var active = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (!byId.ContainsKey(id))
            {
                throw new InvalidOperationException($"The diagram hierarchy links to missing diagram '{id}'.");
            }
            if (!active.Add(id))
            {
                throw new InvalidOperationException("The diagram hierarchy contains a parent/child cycle.");
            }
            if (!deleting.Add(id))
            {
                active.Remove(id);
                return;
            }
            foreach (var childId in byId[id].Nodes
                         .Where(node => node.ChildFlowId.HasValue)
                         .Select(node => node.ChildFlowId!.Value))
            {
                Visit(childId);
            }
            active.Remove(id);
        }
        Visit(rootFlowId);

        var externalReference = definitions
            .Where(flow => !deleting.Contains(flow.Id))
            .SelectMany(flow => flow.Nodes)
            .FirstOrDefault(node => node.ChildFlowId.HasValue && deleting.Contains(node.ChildFlowId.Value));
        if (externalReference is not null)
        {
            throw new InvalidOperationException(
                "This diagram is linked from a parent. Delete the complete hierarchy from its root diagram.");
        }

        var currentUserId = currentUser.GetCurrentUserId();
        foreach (var flow in deleting.Select(id => byId[id]))
        {
            var canDiscardOwnDraft = flow.Version <= 0
                && permissions.CanCreate()
                && string.Equals(flow.CreatedBy, currentUserId, StringComparison.OrdinalIgnoreCase);
            if (!canDiscardOwnDraft && !permissions.CanDelete(flow.CreatedBy))
            {
                throw new UnauthorizedAccessException(
                    "The current user cannot delete every diagram in this hierarchy.");
            }
        }

        var ids = deleting.ToList();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Flows.Where(flow => ids.Contains(flow.Id)).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    private static string NormalizeRequired(string? value, string label, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{label} is required.");
        }
        if (normalized.Length > maximumLength)
        {
            throw new InvalidOperationException($"{label} cannot exceed {maximumLength:N0} characters.");
        }
        return normalized;
    }

    private static string NormalizeOptional(string? value, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximumLength)
        {
            throw new InvalidOperationException($"Diagram description cannot exceed {maximumLength:N0} characters.");
        }
        return normalized;
    }
}