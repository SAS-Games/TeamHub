namespace TeamHub.AI.FlowDesigner.Contracts;

public sealed class AiFlowDesignerOptions
{
    public const string SectionName = "AI:FlowDesigner";

    public bool Enabled { get; set; }
    public int MaximumSourceDocuments { get; set; } = 10;
    public int MaximumSourceCharacters { get; set; } = 120_000;
    public int MaximumPromptCharacters { get; set; } = 4_000;
    public int MaximumOutputTokens { get; set; } = 8_000;
    public int MaximumDiagrams { get; set; } = 12;
    public int MaximumHierarchyDepth { get; set; } = 4;
}