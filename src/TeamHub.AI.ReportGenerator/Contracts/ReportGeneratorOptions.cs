namespace TeamHub.AI.ReportGenerator.Contracts;

public sealed class ReportGeneratorOptions
{
    public const string SectionName = "AI:ReportGenerator";

    public bool Enabled { get; set; }
    public int MaximumConcurrentRequests { get; set; } = 1;
}