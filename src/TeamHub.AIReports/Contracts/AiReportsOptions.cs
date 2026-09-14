namespace TeamHub.AIReports.Contracts;

public sealed class AiReportsOptions
{
    public const string SectionName = "AIReports";

    public bool Enabled { get; set; }
    public int MaximumConcurrentRequests { get; set; } = 1;
}
