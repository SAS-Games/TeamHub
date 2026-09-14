namespace TeamHub.AI.ReportGenerator.Contracts;

public interface IReportGenerationWorkflow
{
    Task<ReportGeneratorDraft> GenerateDraftAsync(
        GenerateReportDraftRequest request,
        CancellationToken cancellationToken = default);
}

public enum ReportGenerationPeriod
{
    Weekly,
    Monthly
}

public sealed record GenerateReportDraftRequest(
    ReportGenerationPeriod Period,
    DateOnly StartDate,
    DateOnly EndDate,
    IReadOnlyList<string> StudioIds);

public sealed record ReportGeneratorDraft(
    IReadOnlyList<ReportGeneratorRow> Rows,
    IReadOnlyList<string> Warnings,
    string Provider,
    string Model);

public sealed record ReportGeneratorRow(
    string StudioId,
    string StudioName,
    string StudioGroup,
    string StudioWork,
    string HpgdsSupport,
    string WmdSupport);
