using Microsoft.EntityFrameworkCore;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Persistence;

public sealed class SqliteFlowRepository(
    IDbContextFactory<FlowDesignerDbContext> contextFactory,
    IFlowSerializer serializer) : IFlowRepository
{
    public async Task<IReadOnlyList<FlowSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Flows
            .AsNoTracking()
            .OrderByDescending(flow => flow.UpdatedAt)
            .Select(flow => new FlowSummary(
                flow.Id,
                flow.Name,
                flow.Description,
                ParseDiagramType(flow.DiagramType),
                flow.NodeCount,
                new DateTimeOffset(DateTime.SpecifyKind(flow.UpdatedAt, DateTimeKind.Utc)),
                flow.Version,
                flow.CreatedBy))
            .ToListAsync(cancellationToken);
    }

    public async Task<FlowDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Flows.AsNoTracking().SingleOrDefaultAsync(flow => flow.Id == id, cancellationToken);
        return entity is null ? null : serializer.Deserialize(entity.GraphJson);
    }

    public async Task SaveAsync(FlowDefinition flow, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Flows.SingleOrDefaultAsync(item => item.Id == flow.Id, cancellationToken);
        if (entity is null)
        {
            entity = new FlowDefinitionEntity { Id = flow.Id };
            context.Flows.Add(entity);
        }

        entity.Name = flow.Name;
        entity.Description = flow.Description;
        entity.DiagramType = flow.DiagramType.ToString();
        entity.GraphJson = serializer.Serialize(flow);
        entity.CreatedAt = flow.CreatedAt.UtcDateTime;
        entity.UpdatedAt = flow.UpdatedAt.UtcDateTime;
        entity.CreatedBy = flow.CreatedBy;
        entity.Version = flow.Version;
        entity.NodeCount = flow.Nodes.Count;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Flows.Where(flow => flow.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    private static DiagramType ParseDiagramType(string value) => value switch
    {
        "DiagramOnly" => DiagramType.StandardFlowchart,
        "ExecutableWorkflow" => DiagramType.BusinessWorkflow,
        _ when Enum.TryParse<DiagramType>(value, out var diagramType) => diagramType,
        _ => DiagramType.StandardFlowchart
    };
}
