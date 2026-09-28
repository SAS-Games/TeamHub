using Microsoft.Extensions.Options;
using TeamHub.AI.Contracts;
using TeamHub.AI.FlowDesigner.Contracts;
using TeamHub.AI.FlowDesigner.SourceDocuments;
using TeamHub.AI.FlowDesigner.Validation;

namespace TeamHub.AI.FlowDesigner.Application;

internal sealed class FlowDiagramGenerationWorkflow(
    IAiModelService modelService,
    FlowDesignerInputProcessor inputProcessor,
    FlowDiagramPromptBuilder promptBuilder,
    GeneratedFlowDiagramParser parser,
    IOptions<AiFlowDesignerOptions> options) : IFlowDiagramGenerationWorkflow
{
    public async Task<FlowDiagramGenerationDraft> CreateDraftAsync(
        CreateFlowDiagramDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
        {
            throw new FlowDiagramGenerationException("AI Flow Designer is disabled.");
        }

        var input = inputProcessor.Prepare(request);
        var health = await modelService.CheckHealthAsync(cancellationToken);
        if (!health.IsAvailable)
        {
            throw new AiModelProviderException(
                $"AI provider '{health.Provider}' is unavailable for model '{health.Model}'. {health.Message}");
        }
        if (!health.IsModelAvailable)
        {
            throw new AiModelProviderException(
                $"AI provider '{health.Provider}' is running, but configured model '{health.Model}' is not available. " +
                health.Message);
        }

        var modelRequest = promptBuilder.Build(
            input,
            options.Value.MaximumOutputTokens,
            options.Value.MaximumDiagrams,
            options.Value.MaximumHierarchyDepth);
        var generation = await modelService.GenerateStructuredAsync(modelRequest, cancellationToken);
        var parsed = parser.Parse(
            generation,
            input.SourceDocuments.Select(document => document.Id).ToList(),
            options.Value.MaximumDiagrams,
            options.Value.MaximumHierarchyDepth);

        return new FlowDiagramGenerationDraft(
            parsed.Diagram,
            parsed.Evidence,
            parsed.Warnings,
            generation.Provider,
            generation.Model)
        {
            ChildDiagrams = parsed.ChildDiagrams
        };
    }
}
