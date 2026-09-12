using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Services;

internal sealed class TemplateCatalogSeeder(
    ITemplateCatalogRepository templates,
    IFlowSerializer serializer)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var retiredStudioSupport = await templates.GetByKeyAsync("STUDIO_SUPPORT", cancellationToken);
        if (retiredStudioSupport is not null && retiredStudioSupport.IsActive)
        {
            await templates.DeleteAsync(retiredStudioSupport.Id, cancellationToken);
        }

        await SeedFlowAsync(
            "INTEGRATION_QA",
            "Integration QA",
            "A ready-made QA lifecycle with success and defect paths.",
            "Quality",
            FlowTemplate.IntegrationQa,
            adminOnly: false,
            cancellationToken);
        await SeedFlowAsync(
            "ONBOARDING",
            "Employee Onboarding",
            "EOO ticket, parallel PIC approval and security briefing, then dependent lab access.",
            "Work Center",
            FlowTemplate.Onboarding,
            adminOnly: true,
            cancellationToken);
        await SeedRayTracingAsync(cancellationToken);
    }

    private async Task SeedRayTracingAsync(CancellationToken cancellationToken)
    {
        var existing = await templates.GetByKeyAsync(RayTracingKnowledgeMapTemplate.TemplateKey, cancellationToken);
        if (existing is not null
            && (!existing.IsActive || !existing.IsBuiltIn || existing.Version >= RayTracingKnowledgeMapTemplate.Version))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await templates.SaveAsync(new TemplateCatalogDefinition
        {
            Id = existing?.Id ?? Guid.Parse("6bb6152e-58bc-4d99-a083-8b2fd18f037d"),
            TemplateKey = RayTracingKnowledgeMapTemplate.TemplateKey,
            Name = "Ray Tracing - Master Overview",
            Description = "A beginner-friendly, drill-down map of a complete ray-tracing renderer.",
            Category = "Graphics",
            TemplateKind = TemplateKinds.FlowDiagramBundle,
            DiagramType = DiagramType.StandardFlowchart,
            PayloadJson = serializer.SerializeBundle(RayTracingKnowledgeMapTemplate.Create()),
            Version = RayTracingKnowledgeMapTemplate.Version,
            IsActive = true,
            IsBuiltIn = true,
            AdminOnly = false,
            CreatedBy = existing?.CreatedBy ?? "system",
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        }, cancellationToken);
    }

    private async Task SeedFlowAsync(
        string key,
        string name,
        string description,
        string category,
        FlowTemplate source,
        bool adminOnly,
        CancellationToken cancellationToken)
    {
        if (await templates.GetByKeyAsync(key, cancellationToken) is not null) return;

        var flow = new FlowDefinition { Name = name, Description = description, Version = 0 };
        FlowTemplateFactory.Apply(flow, source);
        flow.Id = Guid.Empty;
        flow.CreatedBy = null;
        flow.CreatedAt = DateTimeOffset.UnixEpoch;
        flow.UpdatedAt = DateTimeOffset.UnixEpoch;
        flow.Version = 0;
        var now = DateTimeOffset.UtcNow;
        await templates.SaveAsync(new TemplateCatalogDefinition
        {
            TemplateKey = key,
            Name = name,
            Description = description,
            Category = category,
            TemplateKind = TemplateKinds.FlowDiagram,
            DiagramType = flow.DiagramType,
            PayloadJson = serializer.Serialize(flow),
            Version = 1,
            IsActive = true,
            IsBuiltIn = true,
            AdminOnly = adminOnly,
            CreatedBy = "system",
            CreatedAt = now,
            UpdatedAt = now
        }, cancellationToken);
    }
}
