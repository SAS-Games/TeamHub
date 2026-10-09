namespace TeamHub.AI.ReportGenerator.Contracts;

public interface IWeeklyWorkSummaryWorkflow
{
    Task<WeeklyWorkSummaryDraft> GenerateAsync(
        WeeklyWorkSummaryRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WeeklyWorkSummarySource(
    string StudioId,
    string StudioName,
    string HpgdsSupport,
    string Notes,
    string PageId,
    int PageVersion);

public sealed record WeeklyWorkSummaryRequest(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    string RequestedBy,
    IReadOnlyList<WeeklyWorkSummarySource> Sources);

public sealed record WeeklyWorkSummaryRow(string Studio, string SupportActivity);

public sealed record WeeklyWorkSummaryDraft(
    IReadOnlyList<WeeklyWorkSummaryRow> Rows,
    string Provider,
    string Model);

public sealed class WeeklyWorkSummaryException(string message) : Exception(message);
