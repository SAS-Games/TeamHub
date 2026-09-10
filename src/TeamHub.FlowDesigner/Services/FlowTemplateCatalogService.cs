using System.Text;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Services;

public sealed class FlowTemplateCatalogService(
    ITemplateCatalogRepository templates,
    IFlowRepository flows,
    IFlowSerializer serializer,
    ICurrentUserProvider currentUser,
    IFlowPermissionService permissions) : IFlowTemplateCatalogService
{
    public async Task<IReadOnlyList<TemplateCatalogSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var available = await templates.ListAsync(TemplateKinds.FlowDiagram, cancellationToken);
        return available
            .Where(template => template.DiagramType.HasValue)
            .Where(template => !template.AdminOnly || permissions.CanManageTemplates())
            .Where(template => permissions.CanUseDiagramType(template.DiagramType!.Value))
            .ToList();
    }

    public async Task<FlowDefinition> CreateFlowAsync(
        Guid templateId,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        var template = await templates.GetAsync(templateId, cancellationToken)
            ?? throw new KeyNotFoundException($"Template '{templateId}' was not found.");
        return await CreateFromDefinitionAsync(template, name, cancellationToken);
    }

    public async Task<FlowDefinition> CreateFlowByKeyAsync(
        string templateKey,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        var template = await templates.GetByKeyAsync(templateKey, cancellationToken)
            ?? throw new KeyNotFoundException($"Template '{templateKey}' was not found.");
        return await CreateFromDefinitionAsync(template, name, cancellationToken);
    }

    public async Task<TemplateCatalogDefinition> SaveFlowAsync(
        Guid flowId,
        SaveFlowTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!permissions.CanManageTemplates())
        {
            throw new UnauthorizedAccessException("Only template administrators can save templates.");
        }

        var flow = await flows.GetAsync(flowId, cancellationToken)
            ?? throw new KeyNotFoundException($"Flow '{flowId}' was not found.");
        if (!permissions.CanEdit(flow.CreatedBy))
        {
            throw new UnauthorizedAccessException("The current user cannot use this diagram as a template.");
        }
        if (flow.Version <= 0)
        {
            throw new InvalidOperationException("Save the diagram before saving it as a template.");
        }

        var key = NormalizeKey(request.TemplateKey);
        var existing = await templates.GetByKeyAsync(key, cancellationToken);
        if (existing is not null && existing.TemplateKind != TemplateKinds.FlowDiagram)
        {
            throw new InvalidOperationException($"Template key '{key}' belongs to a different template type.");
        }
        var now = DateTimeOffset.UtcNow;
        var payload = serializer.Deserialize(serializer.Serialize(flow));
        payload.Id = Guid.Empty;
        payload.CreatedBy = null;
        payload.CreatedAt = DateTimeOffset.UnixEpoch;
        payload.UpdatedAt = DateTimeOffset.UnixEpoch;
        payload.IsShared = false;
        payload.Version = 0;
        foreach (var node in payload.Nodes) node.Comments = [];

        var template = new TemplateCatalogDefinition
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            TemplateKey = key,
            Name = NormalizeRequired(request.Name, "Template name", 200),
            Description = NormalizeOptional(request.Description, 2000),
            Category = NormalizeRequired(request.Category, "Category", 100),
            TemplateKind = TemplateKinds.FlowDiagram,
            DiagramType = flow.DiagramType,
            PayloadJson = serializer.Serialize(payload),
            Version = (existing?.Version ?? 0) + 1,
            IsActive = true,
            IsBuiltIn = existing?.IsBuiltIn ?? false,
            AdminOnly = existing?.AdminOnly ?? (flow.DiagramType == DiagramType.WorkCenterWorkflow),
            CreatedBy = existing?.CreatedBy ?? currentUser.GetCurrentUserId(),
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        };
        await templates.SaveAsync(template, cancellationToken);
        return template;
    }

    public async Task DeleteAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        if (!permissions.CanManageTemplates())
        {
            throw new UnauthorizedAccessException("Only template administrators can delete templates.");
        }

        var template = await templates.GetAsync(templateId, cancellationToken)
            ?? throw new KeyNotFoundException($"Template '{templateId}' was not found.");
        if (!template.IsActive)
        {
            throw new KeyNotFoundException($"Template '{templateId}' was not found.");
        }

        await templates.DeleteAsync(templateId, cancellationToken);
    }

    private async Task<FlowDefinition> CreateFromDefinitionAsync(
        TemplateCatalogDefinition template,
        string? name,
        CancellationToken cancellationToken)
    {
        if (!template.IsActive || template.TemplateKind != TemplateKinds.FlowDiagram || !template.DiagramType.HasValue)
        {
            throw new InvalidOperationException("This catalog entry is not an active flow diagram template.");
        }
        if (!permissions.CanCreate()
            || !permissions.CanUseDiagramType(template.DiagramType.Value)
            || template.AdminOnly && !permissions.CanManageTemplates())
        {
            throw new UnauthorizedAccessException("The current user cannot create a diagram from this template.");
        }

        var flow = serializer.Deserialize(template.PayloadJson);
        var now = DateTimeOffset.UtcNow;
        flow.Id = Guid.NewGuid();
        flow.Name = string.IsNullOrWhiteSpace(name) ? template.Name : NormalizeRequired(name, "Diagram name", 200);
        flow.DiagramType = template.DiagramType.Value;
        flow.CreatedAt = now;
        flow.UpdatedAt = now;
        flow.CreatedBy = currentUser.GetCurrentUserId();
        flow.IsShared = false;
        flow.Version = 0;
        await flows.SaveAsync(flow, cancellationToken);
        return flow;
    }

    private static string NormalizeKey(string value)
    {
        var normalized = new StringBuilder();
        foreach (var character in value.Trim().ToUpperInvariant())
        {
            normalized.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        var key = string.Join('_', normalized.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(key)
            ? throw new ArgumentException("Enter a template key.", nameof(value))
            : key.Length <= 100 ? key : key[..100];
    }

    private static string NormalizeRequired(string value, string label, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new ArgumentException($"{label} is required.");
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static string NormalizeOptional(string? value, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}
