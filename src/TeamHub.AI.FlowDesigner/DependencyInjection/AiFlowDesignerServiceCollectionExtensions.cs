using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.AI.FlowDesigner.Application;
using TeamHub.AI.FlowDesigner.Contracts;
using TeamHub.AI.FlowDesigner.SourceDocuments;
using TeamHub.AI.FlowDesigner.Validation;

namespace TeamHub.AI.FlowDesigner;

public static class AiFlowDesignerServiceCollectionExtensions
{
    public static IServiceCollection AddAiFlowDesigner(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AiFlowDesignerOptions>()
            .Bind(configuration.GetSection(AiFlowDesignerOptions.SectionName))
            .Validate(options => options.MaximumSourceDocuments is >= 1 and <= 50,
                "AI:FlowDesigner:MaximumSourceDocuments must be between 1 and 50.")
            .Validate(options => options.MaximumSourceCharacters is >= 1_000 and <= 2_000_000,
                "AI:FlowDesigner:MaximumSourceCharacters must be between 1,000 and 2,000,000.")
            .Validate(options => options.MaximumPromptCharacters is >= 100 and <= 20_000,
                "AI:FlowDesigner:MaximumPromptCharacters must be between 100 and 20,000.")
            .Validate(options => options.MaximumOutputTokens is >= 1_000 and <= 32_000,
                "AI:FlowDesigner:MaximumOutputTokens must be between 1,000 and 32,000.")
            .Validate(options => options.MaximumDiagrams is >= 1 and <= 25,
                "AI:FlowDesigner:MaximumDiagrams must be between 1 and 25.")
            .Validate(options => options.MaximumHierarchyDepth is >= 1 and <= 6,
                "AI:FlowDesigner:MaximumHierarchyDepth must be between 1 and 6.")
            .ValidateOnStart();

        services.AddSingleton<FlowDesignerInputProcessor>();
        services.AddSingleton<FlowDiagramPromptBuilder>();
        services.AddSingleton<GeneratedFlowDiagramParser>();
        services.AddSingleton<IFlowDiagramGenerationWorkflow, FlowDiagramGenerationWorkflow>();
        return services;
    }
}