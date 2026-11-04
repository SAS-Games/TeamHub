using TeamHub.AI.Contracts;

namespace TeamHub.AI.Usage.Contracts;

public sealed class AiUsageOptions
{
    public const string SectionName = "AI:Usage";
    public int RetentionMonths { get; set; } = 12;
    public int MaintenanceIntervalHours { get; set; } = 24;
}

public static class AiUsageFeatures
{
    public const string Chat = "Chat";
    public const string ReportGenerator = "ReportGenerator";
    public const string FlowDesigner = "FlowDesigner";
}

public static class AiUsageApplications
{
    public const string TeamHubWeb = "TeamHub.Web";
}

public enum AiUsageExecutionType
{
    Interactive,
    Background,
    Scheduled,
    System
}

public enum AiUsageInvocationStatus
{
    Completed,
    Failed,
    Cancelled
}

public enum AiToolActivityStatus
{
    Completed,
    Failed,
    Cancelled
}

public enum AiToolActivityDimension
{
    Tool,
    User,
    UserTool,
    ToolResource,
    ExternalSource,
    Status,
    Day
}

public enum AiUsageMeasurementSource
{
    ProviderReported,
    Estimated,
    Unavailable
}

public enum AiUsageDimension
{
    User,
    Feature,
    Subfeature,
    Application,
    Provider,
    Model,
    UserFeature,
    FeatureModel,
    UserFeatureModel,
    Day
}

public sealed record AiUsageOperationContext(
    Guid OperationId,
    string UserId,
    string Feature,
    string Subfeature,
    string Application,
    AiUsageExecutionType ExecutionType = AiUsageExecutionType.Interactive);

public sealed record AiUsageInvocationContext(
    string? ResourceType = null,
    string? ResourceId = null);

public sealed record AiToolActivityContext(
    string ToolName,
    string? ResourceType = null,
    string? ResourceId = null,
    string? ExternalSource = null);

public sealed record AiToolActivityQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? UserId = null,
    string? Feature = null,
    string? Subfeature = null,
    string? Application = null,
    string? ToolName = null,
    AiToolActivityStatus? Status = null,
    string? ExternalSource = null);

public sealed record AiToolActivityTotals(
    long CallCount,
    long CompletedCount,
    long FailedCount,
    long CancelledCount,
    long ExternalCallCount,
    long TotalDurationMilliseconds);

public sealed record AiToolActivityAggregateRow(
    string Key,
    string? SecondaryKey,
    long CallCount,
    long CompletedCount,
    long FailedCount,
    long CancelledCount,
    long AverageDurationMilliseconds);

public sealed record AiToolActivityReport(
    AiToolActivityTotals Totals,
    IReadOnlyList<AiToolActivityAggregateRow> Rows);

public sealed record AiToolActivityFilterOptions(
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> ExternalSources);

public sealed record AiUsageQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? UserId = null,
    string? Feature = null,
    string? Subfeature = null,
    string? Application = null,
    string? Provider = null,
    string? Model = null);

public sealed record AiUsageTotals(
    long RequestCount,
    long MeasuredRequestCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens);

public sealed record AiUsageAggregateRow(
    string Key,
    string? SecondaryKey,
    long RequestCount,
    long MeasuredRequestCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens)
{
    public string? TertiaryKey { get; init; }
}

public sealed record AiUsageReport(
    AiUsageTotals Totals,
    IReadOnlyList<AiUsageAggregateRow> Rows);

public sealed record AiUsageFilterOptions(
    IReadOnlyList<string> Subfeatures,
    IReadOnlyList<string> Applications,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> Models);

public sealed record AiUsageUserDailyLimit(string UserId, long DailyTokenLimit);

public sealed record AiUsageLimitSettings(
    long? DefaultDailyTokenLimit,
    string ResetTimeZoneId,
    IReadOnlyList<AiUsageUserDailyLimit> UserLimits,
    bool NotificationsEnabled = true,
    IReadOnlyList<int>? NotificationThresholdPercentages = null);

public sealed record AiUsageLimitStatus(
    string UserId,
    long? DailyTokenLimit,
    long UsedTokens,
    long? RemainingTokens,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset ResetsAtUtc,
    bool IsExceeded);

public sealed record AiUsageThresholdNotification(
    Guid NotificationId,
    string UserId,
    int ThresholdPercent,
    long UsedTokens,
    long DailyTokenLimit,
    long RemainingTokens,
    DateTimeOffset ResetsAtUtc);

public interface IAiUsageLimitService
{
    Task<AiUsageLimitSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(
        AiUsageLimitSettings settings,
        CancellationToken cancellationToken = default);

    Task<AiUsageLimitStatus> GetStatusAsync(
        string userId,
        CancellationToken cancellationToken = default);
}

public interface IAiUsageThresholdNotifier
{
    Task NotifyAsync(
        AiUsageThresholdNotification notification,
        CancellationToken cancellationToken = default);
}

public sealed class AiUsageLimitExceededException(
    string userId,
    long dailyTokenLimit,
    long usedTokens,
    DateTimeOffset resetsAtUtc)
    : InvalidOperationException(
        $"The daily AI token limit of {dailyTokenLimit:N0} has been reached. " +
        $"Usage resets at {resetsAtUtc:yyyy-MM-dd HH:mm} UTC.")
{
    public string UserId { get; } = userId;
    public long DailyTokenLimit { get; } = dailyTokenLimit;
    public long UsedTokens { get; } = usedTokens;
    public DateTimeOffset ResetsAtUtc { get; } = resetsAtUtc;
}

public interface IAiUsageLedger
{
    Task<AiUsageFilterOptions> GetFilterOptionsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    Task<AiUsageReport> GetReportAsync(
        AiUsageQuery query,
        AiUsageDimension dimension,
        CancellationToken cancellationToken = default);

    Task<int> DeleteBeforeAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default);
}

public interface IAiToolActivityLedger
{
    Task<AiToolActivityFilterOptions> GetToolFilterOptionsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    Task<AiToolActivityReport> GetToolReportAsync(
        AiToolActivityQuery query,
        AiToolActivityDimension dimension,
        CancellationToken cancellationToken = default);
}

public interface IAiUsageTracker
{
    Task<AiStructuredGenerationResult> TrackGenerationAsync(
        AiUsageOperationContext operation,
        AiUsageInvocationContext invocation,
        string expectedProvider,
        string expectedModel,
        Func<CancellationToken, Task<AiStructuredGenerationResult>> generate,
        CancellationToken cancellationToken = default);
}

public interface IAiToolActivityTracker
{
    Task<T> TrackAsync<T>(
        AiUsageOperationContext operation,
        AiToolActivityContext activity,
        Func<CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken = default);
}

public interface IAiUsageDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
