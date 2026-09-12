using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;
using TeamHub.FlowDesigner.Persistence;

namespace TeamHub.FlowDesigner.Services;

public sealed class PublishedFlowRecoveryService(
    IDbContextFactory<FlowDesignerDbContext> authoringContextFactory,
    IDbContextFactory<PublishedFlowDbContext> publishedContextFactory,
    IFlowSerializer serializer)
{
    public async Task RecoverIfEmptyAsync(CancellationToken cancellationToken = default)
    {
        await using var publishedContext = await publishedContextFactory.CreateDbContextAsync(cancellationToken);
        if (await publishedContext.PublishedFlows.AnyAsync(cancellationToken)) return;

        await using var authoringContext = await authoringContextFactory.CreateDbContextAsync(cancellationToken);
        var approved = await authoringContext.PublicationRequests
            .AsNoTracking()
            .Where(item => item.Status == FlowPublicationRequestStatus.Approved.ToString())
            .OrderBy(item => item.ReviewedAt ?? item.RequestedAt)
            .ToListAsync(cancellationToken);
        if (approved.Count == 0) return;

        var versions = new Dictionary<Guid, int>();
        var recovered = new List<(PublishedFlowEntity Entity, FlowDiagramTemplateBundle Bundle)>();
        foreach (var request in approved
                     .OrderBy(item => item.ReviewedAt ?? item.RequestedAt)
                     .ThenBy(item => item.Id))
        {
            if (!string.Equals(Hash(request.SnapshotJson), request.SnapshotHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Approved snapshot '{request.Id}' failed its integrity check; published recovery was stopped.");
            }

            var bundle = SanitizeBundle(DeserializeBundle(request.SnapshotJson, request.FlowId));
            var root = bundle.Flows.Single(item => item.Id == bundle.RootFlowId);
            var bundleIds = bundle.Flows.Select(item => item.Id).ToHashSet();
            foreach (var existing in recovered.Where(item => item.Entity.IsCurrent))
            {
                if (existing.Bundle.Flows.Any(item => bundleIds.Contains(item.Id)))
                {
                    existing.Entity.IsCurrent = false;
                }
            }

            var publicationVersion = versions.GetValueOrDefault(root.Id) + 1;
            versions[root.Id] = publicationVersion;
            var graphJson = serializer.SerializeBundle(bundle);
            var entity = new PublishedFlowEntity
            {
                Id = Guid.NewGuid(),
                SourceFlowId = root.Id,
                PublicationRequestId = request.Id,
                Name = root.Name,
                Description = root.Description,
                DiagramType = root.DiagramType.ToString(),
                NodeCount = bundle.Flows.Sum(item => item.Nodes.Count),
                SourceVersion = request.SourceVersion,
                PublicationVersion = publicationVersion,
                GraphJson = graphJson,
                SnapshotHash = Hash(graphJson),
                PublishedAt = request.ReviewedAt ?? request.RequestedAt,
                PublishedBy = request.ReviewedBy ?? "Recovered publication",
                IsCurrent = true
            };
            publishedContext.PublishedFlows.Add(entity);
            recovered.Add((entity, bundle));
        }

        await publishedContext.SaveChangesAsync(cancellationToken);
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
            foreach (var node in definition.Nodes) node.Comments = [];
            definition.IsShared = false;
            definition.CreatedBy = null;
        }
        return clone;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
