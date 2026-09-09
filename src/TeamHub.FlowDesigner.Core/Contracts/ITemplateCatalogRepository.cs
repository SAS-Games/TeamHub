using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Core.Contracts;

public interface ITemplateCatalogRepository
{
    Task<IReadOnlyList<TemplateCatalogSummary>> ListAsync(string? templateKind = null, CancellationToken cancellationToken = default);
    Task<TemplateCatalogDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TemplateCatalogDefinition?> GetByKeyAsync(string templateKey, CancellationToken cancellationToken = default);
    Task SaveAsync(TemplateCatalogDefinition template, CancellationToken cancellationToken = default);
}

public interface IFlowTemplateCatalogService
{
    Task<IReadOnlyList<TemplateCatalogSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<FlowDefinition> CreateFlowAsync(Guid templateId, string? name = null, CancellationToken cancellationToken = default);
    Task<FlowDefinition> CreateFlowByKeyAsync(string templateKey, string? name = null, CancellationToken cancellationToken = default);
    Task<TemplateCatalogDefinition> SaveFlowAsync(Guid flowId, SaveFlowTemplateRequest request, CancellationToken cancellationToken = default);
}
