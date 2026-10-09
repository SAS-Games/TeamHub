namespace TeamHub.AI.ReportGenerator.Contracts;

public sealed class ReportGeneratorOptions
{
    public const string SectionName = "AI:ReportGenerator";

    public bool Enabled { get; set; }
    public int MaximumConcurrentRequests { get; set; } = 1;
    public int MaximumSourceCharactersPerStudio { get; set; } = 24000;
    public int MaximumOutputTokens { get; set; } = 4000;
    public string WeeklyWorkSummaryPromptPath { get; set; } =
        Path.Combine("Prompts", "WeeklyWorkSummary.md");
}