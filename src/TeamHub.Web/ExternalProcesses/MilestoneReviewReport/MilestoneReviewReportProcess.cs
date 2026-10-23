using System.Text.Json;
using System.Text.Json.Serialization;
using TeamHub.MilestoneReview;
using TeamHub.Web.ExternalProcesses;

namespace TeamHub.Web.ExternalProcesses.MilestoneReviewReport;

public sealed record MilestoneReviewReportProcessResult(
    bool Success,
    string? ReportId,
    string? ReportUrl,
    string Message);

public interface IMilestoneReviewReportProcessService
{
    bool IsEnabled { get; }
    Task<MilestoneReviewReportProcessResult> GenerateAsync(
        Guid reviewId,
        Guid requestingUserId,
        string requestingUserIdentifier,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken = default);
}

internal sealed class MilestoneReviewReportProcessService(
    IMilestoneReviewService reviews,
    IExternalProcessRunner processRunner,
    IExternalProcessCredentialResolver credentialResolver) : IMilestoneReviewReportProcessService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public bool IsEnabled => processRunner.IsEnabled(ExternalProcessKeys.MilestoneReviewReport);

    public async Task<MilestoneReviewReportProcessResult> GenerateAsync(
        Guid reviewId,
        Guid requestingUserId,
        string requestingUserIdentifier,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            throw new ExternalProcessException("Milestone review report generation is not enabled.");
        if (reviewId == Guid.Empty || requestingUserId == Guid.Empty
            || string.IsNullOrWhiteSpace(requestingUserIdentifier))
            throw new UnauthorizedAccessException("A signed-in report generator is required.");

        var review = (await reviews.GetActiveAsync(cancellationToken))
            .SingleOrDefault(item => item.Id == reviewId)
            ?? throw new InvalidOperationException("The active milestone review was not found.");
        if (review.ReportGenerators.All(item => item.Id != requestingUserId))
            throw new UnauthorizedAccessException(
                "You are not assigned to generate a report for this milestone review.");

        var correlationId = Guid.NewGuid();
        var input = new MilestoneReviewReportInput(
            SchemaVersion: 1,
            CorrelationId: correlationId,
            RequestedBy: requestingUserIdentifier.Trim(),
            RequestedAtUtc: DateTimeOffset.UtcNow,
            MilestoneReview: new MilestoneReviewReportSource(
                review.Id,
                review.ProjectName,
                review.MilestoneName,
                review.Description,
                review.JiraTicketId,
                review.WorkflowKey,
                review.WorkflowInstanceId,
                review.PublishedAt,
                review.Feedback.Select(item => new MilestoneReviewReportFeedbackSource(
                    item.ReviewerId,
                    item.Reviewer,
                    item.SessionDetails,
                    item.WhatGood,
                    item.NeedImprovements,
                    item.AdditionalFeedback)).ToList()));
        var sensitiveEnvironment = await credentialResolver.ResolveEnvironmentAsync(
            ExternalProcessKeys.MilestoneReviewReport,
            requestingUserIdentifier,
            allowPrivilegedDefaultCredential,
            cancellationToken);
        var processResult = await processRunner.RunAsync(
            new ExternalProcessRequest(
                ExternalProcessKeys.MilestoneReviewReport,
                JsonSerializer.Serialize(input, SerializerOptions),
                SensitiveEnvironment: sensitiveEnvironment),
            cancellationToken);

        MilestoneReviewReportOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<MilestoneReviewReportOutput>(
                processResult.StandardOutput, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new ExternalProcessException(
                "The milestone report process returned an invalid response.", exception);
        }
        if (output is null)
            throw new ExternalProcessException("The milestone report process returned an empty response.");
        if (!output.Success)
            throw new ExternalProcessException(NormalizeMessage(
                output.Message, "The milestone report process did not generate a report."));

        return new(
            true,
            NormalizeOptional(output.ReportId, 256),
            NormalizeReportUrl(output.ReportUrl),
            NormalizeMessage(output.Message, "The milestone report was generated successfully."));
    }

    private static string NormalizeMessage(string? value, string fallback)
    {
        var message = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return message.Length <= 1000 ? message : message[..1000];
    }

    private static string? NormalizeOptional(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static string? NormalizeReportUrl(string? value)
    {
        var normalized = NormalizeOptional(value, 2048);
        if (normalized is null) return null;
        if (normalized.StartsWith('/') && !normalized.StartsWith("//", StringComparison.Ordinal))
            return normalized;
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return normalized;
        throw new ExternalProcessException("The milestone report process returned an invalid report URL.");
    }

    private sealed record MilestoneReviewReportInput(
        int SchemaVersion,
        Guid CorrelationId,
        string RequestedBy,
        DateTimeOffset RequestedAtUtc,
        MilestoneReviewReportSource MilestoneReview);

    private sealed record MilestoneReviewReportSource(
        Guid Id,
        string ProjectName,
        string MilestoneName,
        string Description,
        string? JiraTicketId,
        string? WorkflowKey,
        Guid? WorkflowInstanceId,
        DateTimeOffset? PublishedAt,
        IReadOnlyList<MilestoneReviewReportFeedbackSource> Feedback);

    private sealed record MilestoneReviewReportFeedbackSource(
        Guid ReviewerId,
        string Reviewer,
        string SessionDetails,
        string WhatGood,
        string NeedImprovements,
        string AdditionalFeedback);

    private sealed record MilestoneReviewReportOutput(
        bool Success,
        string? ReportId,
        string? ReportUrl,
        string? Message);
}
