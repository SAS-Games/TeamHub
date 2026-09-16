using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Services;

internal sealed class TemplateCatalogSeeder(
    ITemplateCatalogRepository templates,
    IFlowSerializer serializer)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await RetireTemplateAsync("STUDIO_SUPPORT", cancellationToken);
        await RetireTemplateAsync("RAY_TRACING_MASTER_OVERVIEW", cancellationToken);

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
    }

    private async Task RetireTemplateAsync(string templateKey, CancellationToken cancellationToken)
    {
        var template = await templates.GetByKeyAsync(templateKey, cancellationToken);
        if (template is not null && template.IsActive)
        {
            await templates.DeleteAsync(template.Id, cancellationToken);
        }
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
        if (await templates.IsDeletedAsync(key, cancellationToken)) return;
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
