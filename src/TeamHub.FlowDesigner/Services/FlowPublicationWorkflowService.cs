using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Persistence;

namespace TeamHub.FlowDesigner.Services;

public sealed class FlowPublicationWorkflowService(
    IDbContextFactory<FlowDesignerDbContext> authoringContextFactory,
    IDbContextFactory<FlowLibraryDbContext> publishedContextFactory,
    IFlowRepository flows,
    IFlowSerializer serializer,
    IFlowValidator validator,
    ICurrentUserProvider currentUser,
    IFlowPermissionService permissions,
    IFlowPublicationService hostPublisher) : IFlowPublicationWorkflowService
{
    private const int MaxReviewNoteLength = 2000;

    public bool CanReview => permissions.CanReviewPublications();

    public async Task<FlowPublicationRequestSummary> RequestAsync(
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        var bundle = await LoadAuthoringBundleAsync(flowId, cancellationToken);
        var root = RequiredRoot(bundle);
        var snapshotJson = serializer.SerializeBundle(bundle);
        var snapshotHash = Hash(snapshotJson);

        await using var context = await authoringContextFactory.CreateDbContextAsync(cancellationToken);
        var pending = await context.PublicationRequests
            .AsNoTracking()
            .Where(item => item.FlowId == root.Id && item.Status == FlowPublicationRequestStatus.Pending.ToString())
            .OrderByDescending(item => item.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (pending is not null)
        {
            if (string.Equals(pending.SnapshotHash, snapshotHash, StringComparison.Ordinal)) return ToSummary(pending);
            throw new InvalidOperationException("This diagram hierarchy already has a version awaiting admin review.");
        }

        var requestedBy = RequiredCurrentUser();
        var entity = new FlowPublicationRequestEntity
        {
            Id = Guid.NewGuid(),
            FlowId = root.Id,
            FlowName = root.Name,
            DiagramType = root.DiagramType.ToString(),
            SourceVersion = root.Version,
            NodeCount = bundle.Flows.Sum(item => item.Nodes.Count),
            SnapshotJson = snapshotJson,
            SnapshotHash = snapshotHash,
            RequestedBy = requestedBy,
            RequestedAt = DateTime.UtcNow,
            Status = FlowPublicationRequestStatus.Pending.ToString()
        };
        context.PublicationRequests.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return ToSummary(entity);
    }

    public async Task<FlowPublicationRequestSummary?> GetLatestRequestAsync(
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        var rootFlowId = await ResolveRootFlowIdAsync(flowId, cancellationToken);
        await using var context = await authoringContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.PublicationRequests
            .AsNoTracking()
            .Where(item => item.FlowId == rootFlowId)
            .OrderByDescending(item => item.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (entity is null) return null;
        if (!CanReview && !string.Equals(entity.RequestedBy, RequiredCurrentUser(), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The publication request belongs to another user.");
        }
        return ToSummary(entity);
    }

    public async Task<IReadOnlyList<FlowPublicationRequestSummary>> ListPendingAsync(
        CancellationToken cancellationToken = default)
    {
        RequireReviewer();
        await using var context = await authoringContextFactory.CreateDbContextAsync(cancellationToken);
        return (await context.PublicationRequests
                .AsNoTracking()
                .Where(item => item.Status == FlowPublicationRequestStatus.Pending.ToString())
                .OrderBy(item => item.RequestedAt)
                .ToListAsync(cancellationToken))
            .Select(ToSummary)
            .ToList();
    }

    public async Task<FlowPublicationRequest?> GetRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        RequireReviewer();
        await using var context = await authoringContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.PublicationRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (entity is null) return null;
        var bundle = DeserializeBundle(entity.SnapshotJson, entity.FlowId);
        return new FlowPublicationRequest(ToSummary(entity), RequiredRoot(bundle));
    }

    public async Task<FlowDefinition?> GetRequestDiagramAsync(
        Guid requestId,
        Guid flowId,
        CancellationToken cancellationToken = default)
    {
        RequireReviewer();
        await using var context = await authoringContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.PublicationRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (entity is null) return null;
        VerifyHash(entity.SnapshotJson, entity.SnapshotHash, "The submitted snapshot failed its integrity check.");
        return DeserializeBundle(entity.SnapshotJson, entity.FlowId).Flows.SingleOrDefault(item => item.Id == flowId);
    }

    public async Task<PublishedFlowSummary> ApproveAsync(
        Guid requestId,
        string? reviewNote,
        CancellationToken cancellationToken = default)
    {
        RequireReviewer();
        await using var authoringContext = await authoringContextFactory.CreateDbContextAsync(cancellationToken);
        var request = await authoringContext.PublicationRequests
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken)
            ?? throw new KeyNotFoundException($"Publication request '{requestId}' was not found.");
        if (ParseStatus(request.Status) != FlowPublicationRequestStatus.Pending)
        {
            throw new InvalidOperationException("This publication request has already been reviewed.");
        }
        VerifyHash(request.SnapshotJson, request.SnapshotHash, "The submitted snapshot failed its integrity check.");

        await using var publishedContext = await publishedContextFactory.CreateDbContextAsync(cancellationToken);
        var existingPublication = await publishedContext.PublishedFlows
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.PublicationRequestId == requestId, cancellationToken);
        var bundle = DeserializeBundle(request.SnapshotJson, request.FlowId);
        ValidateBundle(bundle);
        var root = RequiredRoot(bundle);
        if (existingPublication is null && root.DiagramType == DiagramType.WorkCenterWorkflow)
        {
            var hostResult = await hostPublisher.PublishAsync(root, cancellationToken);
            if (!hostResult.Success)
            {
                throw new InvalidOperationException(string.Join(" ", hostResult.Errors));
            }
        }

        var publishedBy = existingPublication?.PublishedBy ?? RequiredCurrentUser();
        var publishedBundle = SanitizeBundle(bundle);
        var publication = existingPublication
            ?? await PublishBundleAsync(
                publishedContext,
                request.Id,
                publishedBundle,
                request.SourceVersion,
                publishedBy,
                DateTime.UtcNow,
                cancellationToken);

        request.Status = FlowPublicationRequestStatus.Approved.ToString();
        request.ReviewedBy = publishedBy;
        request.ReviewedAt = DateTime.UtcNow;
        request.ReviewNote = NormalizeReviewNote(reviewNote);
        await authoringContext.SaveChangesAsync(cancellationToken);
        return ToSummary(publication);
    }

    public async Task RejectAsync(
        Guid requestId,
        string reviewNote,
        CancellationToken cancellationToken = default)
    {
        RequireReviewer();
        var normalizedNote = NormalizeReviewNote(reviewNote);
        if (string.IsNullOrWhiteSpace(normalizedNote))
        {
            throw new ArgumentException("Explain what needs to change before rejecting the diagram.", nameof(reviewNote));
        }

        await using var context = await authoringContextFactory.CreateDbContextAsync(cancellationToken);
        var request = await context.PublicationRequests
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken)
            ?? throw new KeyNotFoundException($"Publication request '{requestId}' was not found.");
        if (ParseStatus(request.Status) != FlowPublicationRequestStatus.Pending)
        {
            throw new InvalidOperationException("This publication request has already been reviewed.");
        }
        request.Status = FlowPublicationRequestStatus.Rejected.ToString();
        request.ReviewedBy = RequiredCurrentUser();
        request.ReviewedAt = DateTime.UtcNow;
        request.ReviewNote = normalizedNote;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PublishedFlowSummary>> ListPublishedAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await publishedContextFactory.CreateDbContextAsync(cancellationToken);
        var current = await context.PublishedFlows.AsNoTracking()
            .Where(item => item.IsCurrent)
            .OrderByDescending(item => item.PublishedAt)
            .ToListAsync(cancellationToken);
        var bundles = current.Select(item => new PublishedBundle(item, ReadPublishedBundle(item))).ToList();
        var childIds = bundles
            .SelectMany(item => item.Bundle.Flows)
            .SelectMany(item => item.Nodes)
            .Where(item => item.ChildFlowId.HasValue)
            .Select(item => item.ChildFlowId!.Value)
            .ToHashSet();
        return bundles
            .Where(item => !childIds.Contains(item.Entity.SourceFlowId))
            .Select(item => ToSummary(item.Entity))
            .ToList();
    }

    public async Task<PublishedFlow?> GetPublishedBySourceAsync(
        Guid sourceFlowId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await publishedContextFactory.CreateDbContextAsync(cancellationToken);
        var published = await FindPublishedBundleAsync(context, sourceFlowId, tracking: false, cancellationToken);
        if (published is null) return null;
        var definition = published.Bundle.Flows.Single(item => item.Id == sourceFlowId);
        return new PublishedFlow(ToSummary(published.Entity), definition);
    }

    public async Task<IReadOnlyList<FlowSummary>> ListPublishedBundleAsync(
        Guid sourceFlowId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await publishedContextFactory.CreateDbContextAsync(cancellationToken);
        var published = await FindPublishedBundleAsync(context, sourceFlowId, tracking: false, cancellationToken);
        if (published is null) return [];
        return published.Bundle.Flows
            .Where(item => item.Id != sourceFlowId)
            .Select(item => new FlowSummary(
                item.Id,
                item.Name,
                item.Description,
                item.DiagramType,
                item.Nodes.Count,
                item.UpdatedAt,
                item.Version,
                null,
                false))
            .ToList();
    }

    public async Task<FlowDefinition> UpdatePublishedAsync(
        Guid sourceFlowId,
        FlowDefinition flow,
        CancellationToken cancellationToken = default)
    {
        RequireReviewer();
        var admin = RequiredCurrentUser();
        if (sourceFlowId != flow.Id)
        {
            throw new ArgumentException("The route and document IDs do not match.", nameof(flow));
        }

        await using var publishedContext = await publishedContextFactory.CreateDbContextAsync(cancellationToken);
        var current = await FindPublishedBundleAsync(publishedContext, sourceFlowId, tracking: true, cancellationToken)
            ?? throw new KeyNotFoundException($"Published diagram '{sourceFlowId}' was not found.");
        var target = current.Bundle.Flows.Single(item => item.Id == sourceFlowId);
        var updated = serializer.Deserialize(serializer.Serialize(flow));
        updated.CreatedAt = target.CreatedAt;
        updated.UpdatedAt = DateTimeOffset.UtcNow;
        updated.CreatedBy = null;
        updated.IsShared = false;
        updated.Version = Math.Max(target.Version + 1, 1);
        NormalizeAndAuthorizeComments(target, updated, admin);
        var index = current.Bundle.Flows.FindIndex(item => item.Id == sourceFlowId);
        current.Bundle.Flows[index] = updated;

        var root = RequiredRoot(current.Bundle);
        if (root.Id != updated.Id)
        {
            root.Version = Math.Max(root.Version + 1, 1);
            root.UpdatedAt = updated.UpdatedAt;
        }
        ValidateBundle(current.Bundle);
        var sanitized = SanitizeBundle(current.Bundle);
        root = RequiredRoot(sanitized);
        if (root.DiagramType == DiagramType.WorkCenterWorkflow)
        {
            var hostResult = await hostPublisher.PublishAsync(root, cancellationToken);
            if (!hostResult.Success) throw new InvalidOperationException(string.Join(" ", hostResult.Errors));
        }

        var snapshotJson = serializer.SerializeBundle(sanitized);
        var request = new FlowPublicationRequestEntity
        {
            Id = Guid.NewGuid(),
            FlowId = root.Id,
            FlowName = root.Name,
            DiagramType = root.DiagramType.ToString(),
            SourceVersion = root.Version,
            NodeCount = sanitized.Flows.Sum(item => item.Nodes.Count),
            SnapshotJson = snapshotJson,
            SnapshotHash = Hash(snapshotJson),
            RequestedBy = admin,
            RequestedAt = DateTime.UtcNow,
            Status = FlowPublicationRequestStatus.Pending.ToString()
        };

        await using var authoringContext = await authoringContextFactory.CreateDbContextAsync(cancellationToken);
        authoringContext.PublicationRequests.Add(request);
        await authoringContext.SaveChangesAsync(cancellationToken);
        try
        {
            await PublishBundleAsync(
                publishedContext,
                request.Id,
                sanitized,
                root.Version,
                admin,
                DateTime.UtcNow,
                cancellationToken);
        }
        catch
        {
            authoringContext.PublicationRequests.Remove(request);
            await authoringContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        request.Status = FlowPublicationRequestStatus.Approved.ToString();
        request.ReviewedBy = admin;
        request.ReviewedAt = DateTime.UtcNow;
        request.ReviewNote = "Updated directly by an administrator.";
        await authoringContext.SaveChangesAsync(cancellationToken);
        return sanitized.Flows.Single(item => item.Id == sourceFlowId);
    }

    public async Task<PublishedFlowSummary> DeletePublishedAsync(
        Guid sourceFlowId,
        CancellationToken cancellationToken = default)
    {
        RequireReviewer();
        var deletedBy = RequiredCurrentUser();

        await using var publishedContext = await publishedContextFactory.CreateDbContextAsync(cancellationToken);
        var current = await FindPublishedBundleAsync(
            publishedContext,
            sourceFlowId,
            tracking: false,
            cancellationToken)
            ?? throw new KeyNotFoundException($"Published diagram '{sourceFlowId}' was not found.");
        var deleted = ToSummary(current.Entity);
        var rootFlowId = current.Entity.SourceFlowId;
        var publicationRequestIds = await publishedContext.PublishedFlows
            .AsNoTracking()
            .Where(item => item.SourceFlowId == rootFlowId)
            .Select(item => item.PublicationRequestId)
            .ToListAsync(cancellationToken);

        await using var authoringContext = await authoringContextFactory.CreateDbContextAsync(cancellationToken);
        var approvedRequests = await authoringContext.PublicationRequests
            .Where(item =>
                (publicationRequestIds.Contains(item.Id) || item.FlowId == rootFlowId)
                && item.Status == FlowPublicationRequestStatus.Approved.ToString())
            .ToListAsync(cancellationToken);
        var deletedAt = DateTime.UtcNow;
        foreach (var request in approvedRequests)
        {
            request.Status = FlowPublicationRequestStatus.Deleted.ToString();
            request.DeletedBy = deletedBy;
            request.DeletedAt = deletedAt;
        }
        await authoringContext.SaveChangesAsync(cancellationToken);

        try
        {
            await publishedContext.PublishedFlows
                .Where(item => item.SourceFlowId == rootFlowId)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch
        {
            foreach (var request in approvedRequests)
            {
                request.Status = FlowPublicationRequestStatus.Approved.ToString();
                request.DeletedBy = null;
                request.DeletedAt = null;
            }
            await authoringContext.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return deleted;
    }

    private async Task<FlowDiagramTemplateBundle> LoadAuthoringBundleAsync(
        Guid flowId,
        CancellationToken cancellationToken)
    {
        var definitions = (await flows.ListDefinitionsAsync(cancellationToken)).ToList();
        var byId = definitions.ToDictionary(item => item.Id);
        if (!byId.ContainsKey(flowId)) throw new KeyNotFoundException($"Flow '{flowId}' was not found.");

        var rootId = ResolveRootFlowId(flowId, definitions);
        var ordered = new List<FlowDefinition>();
        var visited = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (!visited.Add(id)) return;
            if (!byId.TryGetValue(id, out var definition))
            {
                throw new InvalidOperationException($"The diagram hierarchy links to missing diagram '{id}'.");
            }
            if (!permissions.CanEdit(definition.CreatedBy))
            {
                throw new UnauthorizedAccessException("Only the owner of the complete diagram hierarchy can request publication.");
            }
            if (definition.Version <= 0)
            {
                throw new InvalidOperationException($"Save '{definition.Name}' before requesting publication.");
            }
            var validation = validator.Validate(definition);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException($"Resolve the validation errors in '{definition.Name}' before requesting publication.");
            }

            ordered.Add(definition);
            foreach (var childId in definition.Nodes
                         .Where(item => item.ChildFlowId.HasValue)
                         .Select(item => item.ChildFlowId!.Value)
                         .Distinct())
            {
                Visit(childId);
            }
        }

        Visit(rootId);
        var bundle = new FlowDiagramTemplateBundle { RootFlowId = rootId, Flows = ordered };
        ValidateBundle(bundle);
        return bundle;
    }

    private async Task<Guid> ResolveRootFlowIdAsync(Guid flowId, CancellationToken cancellationToken)
    {
        var definitions = (await flows.ListDefinitionsAsync(cancellationToken)).ToList();
        if (definitions.All(item => item.Id != flowId))
        {
            throw new KeyNotFoundException($"Flow '{flowId}' was not found.");
        }
        return ResolveRootFlowId(flowId, definitions);
    }

    private static Guid ResolveRootFlowId(Guid flowId, IReadOnlyList<FlowDefinition> definitions)
    {
        var parents = definitions
            .SelectMany(parent => parent.Nodes
                .Where(node => node.ChildFlowId.HasValue)
                .Select(node => new { ChildId = node.ChildFlowId!.Value, ParentId = parent.Id }))
            .GroupBy(item => item.ChildId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.ParentId).Distinct().ToList());
        var roots = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(flowId);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (!parents.TryGetValue(current, out var parentIds) || parentIds.Count == 0)
            {
                roots.Add(current);
                continue;
            }
            foreach (var parentId in parentIds) pending.Push(parentId);
        }

        return roots.Count switch
        {
            1 => roots.Single(),
            0 => throw new InvalidOperationException("The diagram hierarchy contains a parent cycle."),
            _ => throw new InvalidOperationException("This child diagram belongs to multiple root diagrams. Request publication from each root.")
        };
    }

    private void ValidateBundle(FlowDiagramTemplateBundle bundle)
    {
        var ids = bundle.Flows.Select(item => item.Id).ToList();
        if (bundle.RootFlowId == Guid.Empty
            || ids.Count == 0
            || ids.Count != ids.Distinct().Count()
            || !ids.Contains(bundle.RootFlowId))
        {
            throw new InvalidOperationException("The publication bundle is missing a valid root diagram.");
        }

        var idSet = ids.ToHashSet();
        var byId = bundle.Flows.ToDictionary(item => item.Id);
        foreach (var definition in bundle.Flows)
        {
            if (!validator.Validate(definition).IsValid)
            {
                throw new InvalidOperationException($"The publication bundle contains validation errors in '{definition.Name}'.");
            }
            var missing = definition.Nodes
                .Where(item => item.ChildFlowId.HasValue && !idSet.Contains(item.ChildFlowId.Value))
                .Select(item => item.Title)
                .ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The publication bundle is missing child diagram(s) linked from: {string.Join(", ", missing)}.");
            }
        }

        var visited = new HashSet<Guid>();
        var active = new HashSet<Guid>();
        void Visit(Guid id)
        {
            if (active.Contains(id))
            {
                throw new InvalidOperationException("The publication hierarchy contains a child diagram cycle.");
            }
            if (!visited.Add(id)) return;
            active.Add(id);
            foreach (var childId in byId[id].Nodes
                         .Where(item => item.ChildFlowId.HasValue)
                         .Select(item => item.ChildFlowId!.Value)
                         .Distinct())
            {
                Visit(childId);
            }
            active.Remove(id);
        }

        Visit(bundle.RootFlowId);
        if (visited.Count != bundle.Flows.Count)
        {
            throw new InvalidOperationException("Every published child diagram must remain connected to the root diagram.");
        }
    }

    private FlowDiagramTemplateBundle DeserializeBundle(string json, Guid fallbackRootId)
    {
        using var document = JsonDocument.Parse(json);
        var isBundle = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject().Any(
                item => string.Equals(item.Name, "flows", StringComparison.OrdinalIgnoreCase)
                    && item.Value.ValueKind == JsonValueKind.Array);
        if (isBundle)
        {
            var bundle = serializer.DeserializeBundle(json);
            if (bundle.RootFlowId == Guid.Empty) bundle.RootFlowId = fallbackRootId;
            return bundle;
        }
        var definition = serializer.Deserialize(json);
        return new FlowDiagramTemplateBundle { RootFlowId = fallbackRootId, Flows = [definition] };
    }

    private FlowDiagramTemplateBundle SanitizeBundle(FlowDiagramTemplateBundle bundle)
    {
        var clone = serializer.DeserializeBundle(serializer.SerializeBundle(bundle));
        foreach (var definition in clone.Flows)
        {
            foreach (var node in definition.Nodes)
            {
                node.Comments = (node.Comments ?? []).Where(comment => comment.IsPublic).ToList();
                NormalizeCommentThreads(node.Comments);
            }
            definition.IsShared = false;
            definition.CreatedBy = null;
        }
        return clone;
    }

    private static void NormalizeAndAuthorizeComments(
        FlowDefinition existing,
        FlowDefinition updated,
        string currentUserId)
    {
        var existingNodes = existing.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var node in updated.Nodes)
        {
            node.Comments ??= [];
            var originalComments = existingNodes.TryGetValue(node.Id, out var originalNode)
                ? originalNode.Comments ?? []
                : [];
            var originalById = originalComments
                .Where(comment => !string.IsNullOrWhiteSpace(comment.Id))
                .GroupBy(comment => comment.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var retainedIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var comment in node.Comments)
            {
                if (string.IsNullOrWhiteSpace(comment.Id) || !retainedIds.Add(comment.Id))
                {
                    comment.Id = Guid.NewGuid().ToString("N");
                    retainedIds.Add(comment.Id);
                }

                if (!originalById.TryGetValue(comment.Id, out var original))
                {
                    comment.Author = currentUserId;
                    comment.CreatedAt = DateTimeOffset.UtcNow;
                    continue;
                }

                var ownsComment = string.Equals(original.Author, currentUserId, StringComparison.OrdinalIgnoreCase);
                var unchanged = string.Equals(original.Body, comment.Body, StringComparison.Ordinal)
                    && string.Equals(original.Author, comment.Author, StringComparison.Ordinal)
                    && original.CreatedAt == comment.CreatedAt
                    && original.IsPublic == comment.IsPublic
                    && string.Equals(original.ParentCommentId, comment.ParentCommentId, StringComparison.Ordinal);
                if (!ownsComment && !unchanged)
                {
                    throw new UnauthorizedAccessException("You can edit only your own comments.");
                }

                comment.Author = original.Author;
                comment.CreatedAt = original.CreatedAt;
                comment.ParentCommentId = original.ParentCommentId;
            }

            NormalizeCommentThreads(node.Comments);

            if (originalComments.Any(comment =>
                    !retainedIds.Contains(comment.Id)
                    && !string.Equals(comment.Author, currentUserId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new UnauthorizedAccessException("You can delete only your own comments.");
            }
        }
    }

    private static void NormalizeCommentThreads(List<NodeComment> comments)
    {
        var byId = comments.ToDictionary(comment => comment.Id, StringComparer.Ordinal);
        foreach (var comment in comments)
        {
            if (string.IsNullOrWhiteSpace(comment.ParentCommentId)
                || string.Equals(comment.Id, comment.ParentCommentId, StringComparison.Ordinal)
                || !byId.TryGetValue(comment.ParentCommentId, out var parent))
            {
                comment.ParentCommentId = null;
                continue;
            }

            comment.ParentCommentId = parent.ParentCommentId ?? parent.Id;
        }
    }

    private FlowDiagramTemplateBundle ReadPublishedBundle(PublishedFlowEntity entity)
    {
        VerifyHash(entity.GraphJson, entity.SnapshotHash, "The published diagram failed its integrity check.");
        return DeserializeBundle(entity.GraphJson, entity.SourceFlowId);
    }

    private async Task<PublishedBundle?> FindPublishedBundleAsync(
        FlowLibraryDbContext context,
        Guid sourceFlowId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = tracking ? context.PublishedFlows.AsQueryable() : context.PublishedFlows.AsNoTracking();
        var entities = await query
            .Where(item => item.IsCurrent)
            .OrderByDescending(item => item.PublishedAt)
            .ToListAsync(cancellationToken);
        var bundles = entities.Select(item => new PublishedBundle(item, ReadPublishedBundle(item))).ToList();
        return bundles.FirstOrDefault(item => item.Entity.SourceFlowId == sourceFlowId)
            ?? bundles.FirstOrDefault(item => item.Bundle.Flows.Any(flow => flow.Id == sourceFlowId));
    }

    private async Task<PublishedFlowEntity> PublishBundleAsync(
        FlowLibraryDbContext context,
        Guid publicationRequestId,
        FlowDiagramTemplateBundle bundle,
        int sourceVersion,
        string publishedBy,
        DateTime publishedAt,
        CancellationToken cancellationToken)
    {
        var root = RequiredRoot(bundle);
        var bundleIds = bundle.Flows.Select(item => item.Id).ToHashSet();
        var current = await context.PublishedFlows
            .Where(item => item.IsCurrent)
            .ToListAsync(cancellationToken);
        foreach (var item in current)
        {
            var existingIds = ReadPublishedBundle(item).Flows.Select(flow => flow.Id);
            if (existingIds.Any(bundleIds.Contains)) item.IsCurrent = false;
        }

        var publicationVersion = (await context.PublishedFlows
            .Where(item => item.SourceFlowId == root.Id)
            .Select(item => (int?)item.PublicationVersion)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var graphJson = serializer.SerializeBundle(bundle);
        var publication = new PublishedFlowEntity
        {
            Id = Guid.NewGuid(),
            SourceFlowId = root.Id,
            PublicationRequestId = publicationRequestId,
            Name = root.Name,
            Description = root.Description,
            DiagramType = root.DiagramType.ToString(),
            NodeCount = bundle.Flows.Sum(item => item.Nodes.Count),
            SourceVersion = sourceVersion,
            PublicationVersion = publicationVersion,
            GraphJson = graphJson,
            SnapshotHash = Hash(graphJson),
            PublishedAt = publishedAt,
            PublishedBy = publishedBy,
            IsCurrent = true
        };
        context.PublishedFlows.Add(publication);
        await context.SaveChangesAsync(cancellationToken);
        return publication;
    }

    private static FlowDefinition RequiredRoot(FlowDiagramTemplateBundle bundle) =>
        bundle.Flows.SingleOrDefault(item => item.Id == bundle.RootFlowId)
        ?? throw new InvalidOperationException("The publication bundle's root diagram is missing.");

    private static void VerifyHash(string value, string expectedHash, string message)
    {
        if (!string.Equals(Hash(value), expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record PublishedBundle(PublishedFlowEntity Entity, FlowDiagramTemplateBundle Bundle);

    private void RequireReviewer()
    {
        if (!CanReview) throw new UnauthorizedAccessException("Admin access is required to review publication requests.");
    }

    private string RequiredCurrentUser() =>
        currentUser.GetCurrentUserId() is { Length: > 0 } user
            ? user
            : throw new UnauthorizedAccessException("Sign in to continue.");

    private static string? NormalizeReviewNote(string? value)
    {
        var normalized = value?.Trim();
        return normalized is null || normalized.Length <= MaxReviewNoteLength
            ? normalized
            : normalized[..MaxReviewNoteLength];
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static FlowPublicationRequestStatus ParseStatus(string value) =>
        Enum.TryParse<FlowPublicationRequestStatus>(value, out var status) ? status : FlowPublicationRequestStatus.Pending;

    private static DiagramType ParseDiagramType(string value) =>
        Enum.TryParse<DiagramType>(value, out var diagramType) ? diagramType : DiagramType.StandardFlowchart;

    private static FlowPublicationRequestSummary ToSummary(FlowPublicationRequestEntity entity) =>
        new(entity.Id, entity.FlowId, entity.FlowName, ParseDiagramType(entity.DiagramType), entity.SourceVersion,
            entity.NodeCount, entity.RequestedBy, AsUtc(entity.RequestedAt), ParseStatus(entity.Status), entity.ReviewedBy,
            entity.ReviewedAt.HasValue ? AsUtc(entity.ReviewedAt.Value) : null, entity.ReviewNote, entity.DeletedBy,
            entity.DeletedAt.HasValue ? AsUtc(entity.DeletedAt.Value) : null);

    private static PublishedFlowSummary ToSummary(PublishedFlowEntity entity) =>
        new(entity.Id, entity.SourceFlowId, entity.Name, entity.Description, ParseDiagramType(entity.DiagramType),
            entity.NodeCount, entity.SourceVersion, entity.PublicationVersion, AsUtc(entity.PublishedAt), entity.PublishedBy);

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
