using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TeamHub.AI.Contracts;
using TeamHub.AI.Usage.Contracts;
using TeamHub.AI.Usage.Persistence;

namespace TeamHub.AI.Usage.Application;

internal interface IAiUsageWriter
{
    Task RecordAsync(
        AiUsageOperationContext operation,
        AiUsageInvocationContext invocation,
        AiUsageInvocationStatus status,
        AiUsageMeasurementSource measurementSource,
        string provider,
        string model,
        string? providerRequestId,
        AiTokenUsage? usage,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        long durationMilliseconds,
        string? errorType,
        CancellationToken cancellationToken);
}

internal interface IAiToolActivityWriter
{
    Task RecordToolActivityAsync(
        AiUsageOperationContext operation,
        AiToolActivityContext activity,
        AiToolActivityStatus status,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        long durationMilliseconds,
        string? errorType,
        CancellationToken cancellationToken);
}

internal interface IAiUsageThresholdEvaluator
{
    Task EvaluateAsync(
        AiUsageOperationContext operation,
        CancellationToken cancellationToken = default);
}

internal sealed class AiUsageLedger(
    AiUsageDbContext dbContext,
    TimeProvider timeProvider) : IAiUsageLedger, IAiToolActivityLedger, IAiUsageLimitService,
    IAiUsageWriter, IAiToolActivityWriter
{
    private const string DefaultResetTimeZoneId = "Asia/Kolkata";
    private static readonly int[] DefaultNotificationThresholds = [75, 90, 100];

    public async Task<AiUsageUserConsumptionSummary> GetUserConsumptionSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.LimitSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        var timeZone = ResolveTimeZone(settings?.ResetTimeZoneId ?? DefaultResetTimeZoneId);
        var now = timeProvider.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var localToday = DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified);
        var daysSinceMonday = ((int)localToday.DayOfWeek + 6) % 7;
        var localWeekStart = localToday.AddDays(-daysSinceMonday);
        var localMonthStart = new DateTime(localToday.Year, localToday.Month, 1);
        var todayStart = TimeZoneInfo.ConvertTimeToUtc(localToday, timeZone);
        var weekStart = TimeZoneInfo.ConvertTimeToUtc(localWeekStart, timeZone);
        var monthStart = TimeZoneInfo.ConvertTimeToUtc(localMonthStart, timeZone);
        var nowUtc = now.UtcDateTime;

        var databaseRows = await dbContext.Invocations.AsNoTracking()
            .Where(item => item.Status == AiUsageInvocationStatus.Completed
                && item.TotalTokens != null
                && item.StartedAtUtc < nowUtc)
            .GroupBy(item => new { item.Operation.NormalizedUserId, item.Operation.UserId })
            .Select(group => new UserConsumptionAggregate
            {
                NormalizedUserId = group.Key.NormalizedUserId,
                UserId = group.Key.UserId,
                TodayTokens = group.Sum(item => item.StartedAtUtc >= todayStart
                    ? (long)(item.TotalTokens ?? 0) : 0),
                WeekTokens = group.Sum(item => item.StartedAtUtc >= weekStart
                    ? (long)(item.TotalTokens ?? 0) : 0),
                MonthTokens = group.Sum(item => item.StartedAtUtc >= monthStart
                    ? (long)(item.TotalTokens ?? 0) : 0),
                RetainedTotalTokens = group.Sum(item => (long)(item.TotalTokens ?? 0))
            })
            .ToListAsync(cancellationToken);

        var users = databaseRows
            .GroupBy(item => item.NormalizedUserId, StringComparer.Ordinal)
            .Select(group => new AiUsageUserConsumption(
                group.First().UserId,
                group.Sum(item => item.TodayTokens),
                group.Sum(item => item.WeekTokens),
                group.Sum(item => item.MonthTokens),
                group.Sum(item => item.RetainedTotalTokens)))
            .OrderByDescending(item => item.RetainedTotalTokens)
            .ThenBy(item => item.UserId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AiUsageUserConsumptionSummary(timeZone.Id, now, users);
    }

    public async Task<AiUsageLimitSettings> GetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.LimitSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        var userLimits = await dbContext.UserLimits.AsNoTracking()
            .OrderBy(item => item.NormalizedUserId)
            .Select(item => new AiUsageUserDailyLimit(item.UserId, item.DailyTokenLimit))
            .ToListAsync(cancellationToken);
        return new AiUsageLimitSettings(
            settings?.DefaultDailyTokenLimit,
            settings?.ResetTimeZoneId ?? DefaultResetTimeZoneId,
            userLimits,
            settings?.NotificationsEnabled ?? true,
            ParseThresholds(settings?.NotificationThresholds));
    }

    public async Task SaveSettingsAsync(
        AiUsageLimitSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateLimit(settings.DefaultDailyTokenLimit, "The default daily token limit");
        var timeZoneId = settings.ResetTimeZoneId?.Trim() ?? string.Empty;
        _ = ResolveTimeZone(timeZoneId);
        var thresholds = NormalizeThresholds(settings.NotificationThresholdPercentages);

        var normalizedLimits = new Dictionary<string, AiUsageUserLimitRecord>(StringComparer.Ordinal);
        foreach (var userLimit in settings.UserLimits ?? [])
        {
            var userId = Required(userLimit.UserId, 320, "User ID");
            ValidateLimit(userLimit.DailyTokenLimit, $"The daily token limit for {userId}");
            var normalizedUserId = NormalizeUserId(userId);
            if (!normalizedLimits.TryAdd(normalizedUserId, new AiUsageUserLimitRecord
                {
                    NormalizedUserId = normalizedUserId,
                    UserId = userId,
                    DailyTokenLimit = userLimit.DailyTokenLimit
                }))
                throw new ArgumentException($"The user '{userId}' has more than one daily token limit.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var record = await dbContext.LimitSettings.SingleOrDefaultAsync(
            item => item.Id == 1,
            cancellationToken);
        if (record is null)
        {
            record = new AiUsageLimitSettingsRecord { Id = 1 };
            dbContext.LimitSettings.Add(record);
        }
        record.DefaultDailyTokenLimit = settings.DefaultDailyTokenLimit is 0
            ? null
            : settings.DefaultDailyTokenLimit;
        record.ResetTimeZoneId = timeZoneId;
        record.NotificationsEnabled = settings.NotificationsEnabled;
        record.NotificationThresholds = string.Join(',', thresholds);
        await dbContext.UserLimits.ExecuteDeleteAsync(cancellationToken);
        dbContext.UserLimits.AddRange(normalizedLimits.Values);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AiUsageLimitStatus> GetStatusAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        userId = Required(userId, 320, "User ID");
        var normalizedUserId = NormalizeUserId(userId);
        var settings = await dbContext.LimitSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        var overrideRecord = await dbContext.UserLimits.AsNoTracking()
            .SingleOrDefaultAsync(item => item.NormalizedUserId == normalizedUserId, cancellationToken);
        var limit = overrideRecord is null
            ? settings?.DefaultDailyTokenLimit
            : overrideRecord.DailyTokenLimit == 0 ? null : overrideRecord.DailyTokenLimit;
        var window = GetDailyWindow(
            timeProvider.GetUtcNow(),
            ResolveTimeZone(settings?.ResetTimeZoneId ?? DefaultResetTimeZoneId));
        var used = await dbContext.Invocations.AsNoTracking()
            .Where(item => item.Operation.NormalizedUserId == normalizedUserId
                && item.StartedAtUtc >= window.StartUtc.UtcDateTime
                && item.StartedAtUtc < window.EndUtc.UtcDateTime
                && item.Status == AiUsageInvocationStatus.Completed
                && item.TotalTokens != null)
            .SumAsync(item => (long?)(item.TotalTokens ?? 0), cancellationToken) ?? 0;
        return new AiUsageLimitStatus(
            userId,
            limit,
            used,
            limit.HasValue ? Math.Max(0, limit.Value - used) : null,
            window.StartUtc,
            window.EndUtc,
            limit.HasValue && used >= limit.Value);
    }

    public async Task<AiUsageFilterOptions> GetFilterOptionsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        if (toUtc <= fromUtc)
            throw new ArgumentException("The AI usage filter end must be after its start.", nameof(toUtc));

        var from = fromUtc.UtcDateTime;
        var to = toUtc.UtcDateTime;
        var rows = dbContext.Invocations.AsNoTracking()
            .Where(item => item.StartedAtUtc >= from && item.StartedAtUtc < to);
        var subfeatures = await rows.Select(item => item.Operation.Subfeature)
            .Distinct().ToListAsync(cancellationToken);
        var applications = await rows.Select(item => item.Operation.Application)
            .Distinct().ToListAsync(cancellationToken);
        var providers = await rows.Select(item => item.Provider)
            .Distinct().ToListAsync(cancellationToken);
        var models = await rows.Select(item => item.Model)
            .Distinct().ToListAsync(cancellationToken);
        return new AiUsageFilterOptions(
            SortDistinct(subfeatures),
            SortDistinct(applications),
            SortDistinct(providers),
            SortDistinct(models));
    }

    public async Task RecordAsync(
        AiUsageOperationContext operation,
        AiUsageInvocationContext invocation,
        AiUsageInvocationStatus status,
        AiUsageMeasurementSource measurementSource,
        string provider,
        string model,
        string? providerRequestId,
        AiTokenUsage? usage,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        long durationMilliseconds,
        string? errorType,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.Operations
            .SingleOrDefaultAsync(item => item.Id == operation.OperationId, cancellationToken);
        if (existing is null)
        {
            existing = new AiUsageOperationRecord
            {
                Id = operation.OperationId,
                UserId = Required(operation.UserId, 320, nameof(operation.UserId)),
                NormalizedUserId = NormalizeUserId(operation.UserId),
                Feature = Required(operation.Feature, 100, nameof(operation.Feature)),
                Subfeature = Required(operation.Subfeature, 100, nameof(operation.Subfeature)),
                Application = Required(operation.Application, 160, nameof(operation.Application)),
                ExecutionType = operation.ExecutionType,
                StartedAtUtc = startedAtUtc
            };
            dbContext.Operations.Add(existing);
        }
        else
        {
            EnsureMatchingOperation(existing, operation);
        }

        dbContext.Invocations.Add(new AiUsageInvocationRecord
        {
            Id = Guid.NewGuid(),
            OperationId = operation.OperationId,
            Provider = Required(provider, 128, nameof(provider)),
            Model = Required(model, 256, nameof(model)),
            ProviderRequestId = Optional(providerRequestId, 256),
            InputTokens = usage?.InputTokens,
            OutputTokens = usage?.OutputTokens,
            TotalTokens = usage?.TotalTokens,
            MeasurementSource = measurementSource,
            Status = status,
            ResourceType = Optional(invocation.ResourceType, 100),
            ResourceId = Optional(invocation.ResourceId, 256),
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            DurationMilliseconds = Math.Max(0, durationMilliseconds),
            ErrorType = Optional(errorType, 256)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordToolActivityAsync(
        AiUsageOperationContext operation,
        AiToolActivityContext activity,
        AiToolActivityStatus status,
        DateTime startedAtUtc,
        DateTime completedAtUtc,
        long durationMilliseconds,
        string? errorType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(activity);
        var existing = await dbContext.Operations
            .SingleOrDefaultAsync(item => item.Id == operation.OperationId, cancellationToken);
        if (existing is null)
        {
            existing = new AiUsageOperationRecord
            {
                Id = operation.OperationId,
                UserId = Required(operation.UserId, 320, nameof(operation.UserId)),
                NormalizedUserId = NormalizeUserId(operation.UserId),
                Feature = Required(operation.Feature, 100, nameof(operation.Feature)),
                Subfeature = Required(operation.Subfeature, 100, nameof(operation.Subfeature)),
                Application = Required(operation.Application, 160, nameof(operation.Application)),
                ExecutionType = operation.ExecutionType,
                StartedAtUtc = startedAtUtc
            };
            dbContext.Operations.Add(existing);
        }
        else
        {
            EnsureMatchingOperation(existing, operation);
        }

        dbContext.ToolActivities.Add(new AiToolActivityRecord
        {
            Id = Guid.NewGuid(),
            OperationId = operation.OperationId,
            ToolName = Required(activity.ToolName, 64, nameof(activity.ToolName)),
            // A failed tool may contain an invalid model-supplied resource argument.
            // Retain resource identity only after the provider validated and completed the lookup.
            ResourceType = status == AiToolActivityStatus.Completed
                ? Optional(activity.ResourceType, 100)
                : null,
            ResourceId = status == AiToolActivityStatus.Completed
                ? Optional(activity.ResourceId, 256)
                : null,
            ExternalSource = Optional(activity.ExternalSource, 100),
            Status = status,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            DurationMilliseconds = Math.Max(0, durationMilliseconds),
            ErrorType = Optional(errorType, 256)
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AiUsageReport> GetReportAsync(
        AiUsageQuery query,
        AiUsageDimension dimension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.ToUtc <= query.FromUtc)
            throw new ArgumentException("The AI usage report end must be after its start.", nameof(query));

        var rows = ApplyFilters(dbContext.Invocations.AsNoTracking(), query);
        var totals = await rows.GroupBy(_ => 1).Select(group => new AiUsageTotals(
                group.LongCount(),
                group.LongCount(item => item.MeasurementSource != AiUsageMeasurementSource.Unavailable),
                group.Sum(item => (long?)(item.InputTokens ?? 0)) ?? 0,
                group.Sum(item => (long?)(item.OutputTokens ?? 0)) ?? 0,
                group.Sum(item => (long?)(item.TotalTokens ?? 0)) ?? 0))
            .SingleOrDefaultAsync(cancellationToken)
            ?? new AiUsageTotals(0, 0, 0, 0, 0);

        if (dimension == AiUsageDimension.Day)
            return new AiUsageReport(totals, await AggregateByDayAsync(rows, cancellationToken));

        List<AggregateResult> results;
        switch (dimension)
        {
            case AiUsageDimension.User:
                results = await rows.GroupBy(item => item.Operation.UserId)
                    .Select(group => new AggregateResult { Primary = group.Key, RequestCount = group.LongCount(), MeasuredRequestCount = group.LongCount(item => item.MeasurementSource != AiUsageMeasurementSource.Unavailable), InputTokens = group.Sum(item => (long)(item.InputTokens ?? 0)), OutputTokens = group.Sum(item => (long)(item.OutputTokens ?? 0)), TotalTokens = group.Sum(item => (long)(item.TotalTokens ?? 0)) })
                    .ToListAsync(cancellationToken);
                break;
            case AiUsageDimension.Feature:
                results = await rows.GroupBy(item => item.Operation.Feature)
                    .Select(group => new AggregateResult { Primary = group.Key, RequestCount = group.LongCount(), MeasuredRequestCount = group.LongCount(item => item.MeasurementSource != AiUsageMeasurementSource.Unavailable), InputTokens = group.Sum(item => (long)(item.InputTokens ?? 0)), OutputTokens = group.Sum(item => (long)(item.OutputTokens ?? 0)), TotalTokens = group.Sum(item => (long)(item.TotalTokens ?? 0)) })
                    .ToListAsync(cancellationToken);
                break;
            case AiUsageDimension.Subfeature:
                results = await rows.GroupBy(item => item.Operation.Subfeature)
                    .Select(group => new AggregateResult { Primary = group.Key, RequestCount = group.LongCount(), MeasuredRequestCount = group.LongCount(item => item.MeasurementSource != AiUsageMeasurementSource.Unavailable), InputTokens = group.Sum(item => (long)(item.InputTokens ?? 0)), OutputTokens = group.Sum(item => (long)(item.OutputTokens ?? 0)), TotalTokens = group.Sum(item => (long)(item.TotalTokens ?? 0)) })
                    .ToListAsync(cancellationToken);
                break;
            case AiUsageDimension.Application:
                results = await rows.GroupBy(item => item.Operation.Application)
                    .Select(group => new AggregateResult { Primary = group.Key, RequestCount = group.LongCount(), MeasuredRequestCount = group.LongCount(item => item.MeasurementSource != AiUsageMeasurementSource.Unavailable), InputTokens = group.Sum(item => (long)(item.InputTokens ?? 0)), OutputTokens = group.Sum(item => (long)(item.OutputTokens ?? 0)), TotalTokens = group.Sum(item => (long)(item.TotalTokens ?? 0)) })
                    .ToListAsync(cancellationToken);
                break;
            case AiUsageDimension.Provider:
                results = await rows.GroupBy(item => item.Provider)
                    .Select(group => new AggregateResult { Primary = group.Key, RequestCount = group.LongCount(), MeasuredRequestCount = group.LongCount(item => item.MeasurementSource != AiUsageMeasurementSource.Unavailable), InputTokens = group.Sum(item => (long)(item.InputTokens ?? 0)), OutputTokens = group.Sum(item => (long)(item.OutputTokens ?? 0)), TotalTokens = group.Sum(item => (long)(item.TotalTokens ?? 0)) })
                    .ToListAsync(cancellationToken);
                break;
            case AiUsageDimension.Model:
                results = await rows.GroupBy(item => new { item.Model, item.Provider })
                    .Select(group => new AggregateResult { Primary = group.Key.Model, Secondary = group.Key.Provider, RequestCount = group.LongCount(), MeasuredRequestCount = group.LongCount(item => item.MeasurementSource != AiUsageMeasurementSource.Unavailable), InputTokens = group.Sum(item => (long)(item.InputTokens ?? 0)), OutputTokens = group.Sum(item => (long)(item.OutputTokens ?? 0)), TotalTokens = group.Sum(item => (long)(item.TotalTokens ?? 0)) })
                    .ToListAsync(cancellationToken);
                break;
            case AiUsageDimension.UserFeature:
                results = await rows.GroupBy(item => new { item.Operation.UserId, item.Operation.Feature })
                    .Select(group => new AggregateResult { Primary = group.Key.UserId, Secondary = group.Key.Feature, RequestCount = group.LongCount(), MeasuredRequestCount = group.LongCount(item => item.MeasurementSource != AiUsageMeasurementSource.Unavailable), InputTokens = group.Sum(item => (long)(item.InputTokens ?? 0)), OutputTokens = group.Sum(item => (long)(item.OutputTokens ?? 0)), TotalTokens = group.Sum(item => (long)(item.TotalTokens ?? 0)) })
                    .ToListAsync(cancellationToken);
                break;
            case AiUsageDimension.FeatureModel:
                results = await rows.GroupBy(item => new { item.Operation.Feature, item.Model })
                    .Select(group => new AggregateResult { Primary = group.Key.Feature, Secondary = group.Key.Model, RequestCount = group.LongCount(), MeasuredRequestCount = group.LongCount(item => item.MeasurementSource != AiUsageMeasurementSource.Unavailable), InputTokens = group.Sum(item => (long)(item.InputTokens ?? 0)), OutputTokens = group.Sum(item => (long)(item.OutputTokens ?? 0)), TotalTokens = group.Sum(item => (long)(item.TotalTokens ?? 0)) })
                    .ToListAsync(cancellationToken);
                break;
            case AiUsageDimension.UserFeatureModel:
                results = await rows.GroupBy(item => new
                    { item.Operation.UserId, item.Operation.Feature, item.Model })
                    .Select(group => new AggregateResult
                    {
                        Primary = group.Key.UserId,
                        Secondary = group.Key.Feature,
                        Tertiary = group.Key.Model,
                        RequestCount = group.LongCount(),
                        MeasuredRequestCount = group.LongCount(item =>
                            item.MeasurementSource != AiUsageMeasurementSource.Unavailable),
                        InputTokens = group.Sum(item => (long)(item.InputTokens ?? 0)),
                        OutputTokens = group.Sum(item => (long)(item.OutputTokens ?? 0)),
                        TotalTokens = group.Sum(item => (long)(item.TotalTokens ?? 0))
                    })
                    .ToListAsync(cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        var aggregates = results
            .Select(item => new AiUsageAggregateRow(
                item.Primary, item.Secondary, item.RequestCount, item.MeasuredRequestCount,
                item.InputTokens, item.OutputTokens, item.TotalTokens)
                { TertiaryKey = item.Tertiary })
            .OrderByDescending(item => item.TotalTokens)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AiUsageReport(totals, aggregates);
    }

    public async Task<AiToolActivityFilterOptions> GetToolFilterOptionsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        if (toUtc <= fromUtc)
            throw new ArgumentException("The AI tool activity filter end must be after its start.", nameof(toUtc));
        var from = fromUtc.UtcDateTime;
        var to = toUtc.UtcDateTime;
        var rows = dbContext.ToolActivities.AsNoTracking()
            .Where(item => item.StartedAtUtc >= from && item.StartedAtUtc < to);
        var toolNames = await rows.Select(item => item.ToolName).Distinct().ToListAsync(cancellationToken);
        var externalSources = await rows.Where(item => item.ExternalSource != null)
            .Select(item => item.ExternalSource!)
            .Distinct()
            .ToListAsync(cancellationToken);
        return new AiToolActivityFilterOptions(
            SortDistinct(toolNames),
            SortDistinct(externalSources));
    }

    public async Task<AiToolActivityReport> GetToolReportAsync(
        AiToolActivityQuery query,
        AiToolActivityDimension dimension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.ToUtc <= query.FromUtc)
            throw new ArgumentException("The AI tool activity report end must be after its start.", nameof(query));

        var rows = ApplyToolFilters(dbContext.ToolActivities.AsNoTracking(), query);
        var totals = await rows.GroupBy(_ => 1).Select(group => new AiToolActivityTotals(
                group.LongCount(),
                group.LongCount(item => item.Status == AiToolActivityStatus.Completed),
                group.LongCount(item => item.Status == AiToolActivityStatus.Failed),
                group.LongCount(item => item.Status == AiToolActivityStatus.Cancelled),
                group.LongCount(item => item.ExternalSource != null),
                group.Sum(item => item.DurationMilliseconds)))
            .SingleOrDefaultAsync(cancellationToken)
            ?? new AiToolActivityTotals(0, 0, 0, 0, 0, 0);

        if (dimension == AiToolActivityDimension.Day)
            return new AiToolActivityReport(totals, await AggregateToolsByDayAsync(rows, cancellationToken));

        List<ToolAggregateResult> results;
        switch (dimension)
        {
            case AiToolActivityDimension.Tool:
                results = await rows.GroupBy(item => item.ToolName)
                    .Select(group => new ToolAggregateResult { Primary = group.Key, CallCount = group.LongCount(), CompletedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Completed), FailedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Failed), CancelledCount = group.LongCount(item => item.Status == AiToolActivityStatus.Cancelled), TotalDurationMilliseconds = group.Sum(item => item.DurationMilliseconds) })
                    .ToListAsync(cancellationToken);
                break;
            case AiToolActivityDimension.User:
                results = await rows.GroupBy(item => item.Operation.UserId)
                    .Select(group => new ToolAggregateResult { Primary = group.Key, CallCount = group.LongCount(), CompletedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Completed), FailedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Failed), CancelledCount = group.LongCount(item => item.Status == AiToolActivityStatus.Cancelled), TotalDurationMilliseconds = group.Sum(item => item.DurationMilliseconds) })
                    .ToListAsync(cancellationToken);
                break;
            case AiToolActivityDimension.UserTool:
                results = await rows.GroupBy(item => new { item.Operation.UserId, item.ToolName })
                    .Select(group => new ToolAggregateResult { Primary = group.Key.UserId, Secondary = group.Key.ToolName, CallCount = group.LongCount(), CompletedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Completed), FailedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Failed), CancelledCount = group.LongCount(item => item.Status == AiToolActivityStatus.Cancelled), TotalDurationMilliseconds = group.Sum(item => item.DurationMilliseconds) })
                    .ToListAsync(cancellationToken);
                break;
            case AiToolActivityDimension.ToolResource:
                results = await rows.GroupBy(item => new { item.ToolName, item.ResourceId })
                    .Select(group => new ToolAggregateResult { Primary = group.Key.ToolName, Secondary = group.Key.ResourceId ?? "Not specified", CallCount = group.LongCount(), CompletedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Completed), FailedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Failed), CancelledCount = group.LongCount(item => item.Status == AiToolActivityStatus.Cancelled), TotalDurationMilliseconds = group.Sum(item => item.DurationMilliseconds) })
                    .ToListAsync(cancellationToken);
                break;
            case AiToolActivityDimension.ExternalSource:
                results = await rows.GroupBy(item => item.ExternalSource ?? "Internal")
                    .Select(group => new ToolAggregateResult { Primary = group.Key, CallCount = group.LongCount(), CompletedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Completed), FailedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Failed), CancelledCount = group.LongCount(item => item.Status == AiToolActivityStatus.Cancelled), TotalDurationMilliseconds = group.Sum(item => item.DurationMilliseconds) })
                    .ToListAsync(cancellationToken);
                break;
            case AiToolActivityDimension.Status:
                results = await rows.GroupBy(item => item.Status)
                    .Select(group => new ToolAggregateResult { Primary = group.Key == AiToolActivityStatus.Completed ? "Completed" : group.Key == AiToolActivityStatus.Failed ? "Failed" : "Cancelled", CallCount = group.LongCount(), CompletedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Completed), FailedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Failed), CancelledCount = group.LongCount(item => item.Status == AiToolActivityStatus.Cancelled), TotalDurationMilliseconds = group.Sum(item => item.DurationMilliseconds) })
                    .ToListAsync(cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(dimension));
        }

        var aggregates = results
            .Select(item => new AiToolActivityAggregateRow(
                item.Primary,
                item.Secondary,
                item.CallCount,
                item.CompletedCount,
                item.FailedCount,
                item.CancelledCount,
                item.CallCount == 0 ? 0 : item.TotalDurationMilliseconds / item.CallCount))
            .OrderByDescending(item => item.CallCount)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AiToolActivityReport(totals, aggregates);
    }

    public async Task<int> DeleteBeforeAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        var cutoff = cutoffUtc.UtcDateTime;
        await dbContext.ThresholdNotifications
            .Where(item => item.WindowEndUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        return await dbContext.Operations
            .Where(item => item.StartedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static IQueryable<AiUsageInvocationRecord> ApplyFilters(
        IQueryable<AiUsageInvocationRecord> rows,
        AiUsageQuery query)
    {
        var from = query.FromUtc.UtcDateTime;
        var to = query.ToUtc.UtcDateTime;
        rows = rows.Where(item => item.StartedAtUtc >= from && item.StartedAtUtc < to);
        if (!string.IsNullOrWhiteSpace(query.UserId))
        {
            var normalized = NormalizeUserId(query.UserId);
            rows = rows.Where(item => item.Operation.NormalizedUserId == normalized);
        }
        if (!string.IsNullOrWhiteSpace(query.Feature))
            rows = rows.Where(item => item.Operation.Feature == query.Feature.Trim());
        if (!string.IsNullOrWhiteSpace(query.Subfeature))
            rows = rows.Where(item => item.Operation.Subfeature == query.Subfeature.Trim());
        if (!string.IsNullOrWhiteSpace(query.Application))
            rows = rows.Where(item => item.Operation.Application == query.Application.Trim());
        if (!string.IsNullOrWhiteSpace(query.Provider))
            rows = rows.Where(item => item.Provider == query.Provider.Trim());
        if (!string.IsNullOrWhiteSpace(query.Model))
            rows = rows.Where(item => item.Model == query.Model.Trim());
        return rows;
    }

    private static IQueryable<AiToolActivityRecord> ApplyToolFilters(
        IQueryable<AiToolActivityRecord> rows,
        AiToolActivityQuery query)
    {
        var from = query.FromUtc.UtcDateTime;
        var to = query.ToUtc.UtcDateTime;
        rows = rows.Where(item => item.StartedAtUtc >= from && item.StartedAtUtc < to);
        if (!string.IsNullOrWhiteSpace(query.UserId))
        {
            var normalized = NormalizeUserId(query.UserId);
            rows = rows.Where(item => item.Operation.NormalizedUserId == normalized);
        }
        if (!string.IsNullOrWhiteSpace(query.Feature))
            rows = rows.Where(item => item.Operation.Feature == query.Feature.Trim());
        if (!string.IsNullOrWhiteSpace(query.Subfeature))
            rows = rows.Where(item => item.Operation.Subfeature == query.Subfeature.Trim());
        if (!string.IsNullOrWhiteSpace(query.Application))
            rows = rows.Where(item => item.Operation.Application == query.Application.Trim());
        if (!string.IsNullOrWhiteSpace(query.ToolName))
            rows = rows.Where(item => item.ToolName == query.ToolName.Trim());
        if (query.Status.HasValue)
            rows = rows.Where(item => item.Status == query.Status.Value);
        if (!string.IsNullOrWhiteSpace(query.ExternalSource))
            rows = rows.Where(item => item.ExternalSource == query.ExternalSource.Trim());
        return rows;
    }

    private static async Task<IReadOnlyList<AiUsageAggregateRow>> AggregateByDayAsync(
        IQueryable<AiUsageInvocationRecord> rows,
        CancellationToken cancellationToken)
    {
        var results = await rows.GroupBy(item => item.StartedAtUtc.Date)
            .Select(group => new DayAggregate
            {
                Day = group.Key,
                RequestCount = group.LongCount(),
                MeasuredRequestCount = group.LongCount(item =>
                    item.MeasurementSource != AiUsageMeasurementSource.Unavailable),
                InputTokens = group.Sum(item => (long)(item.InputTokens ?? 0)),
                OutputTokens = group.Sum(item => (long)(item.OutputTokens ?? 0)),
                TotalTokens = group.Sum(item => (long)(item.TotalTokens ?? 0))
            })
            .ToListAsync(cancellationToken);
        return results.OrderBy(item => item.Day).Select(item => new AiUsageAggregateRow(
                item.Day.ToString("yyyy-MM-dd"), null, item.RequestCount, item.MeasuredRequestCount,
                item.InputTokens, item.OutputTokens, item.TotalTokens))
            .ToList();
    }

    private static async Task<IReadOnlyList<AiToolActivityAggregateRow>> AggregateToolsByDayAsync(
        IQueryable<AiToolActivityRecord> rows,
        CancellationToken cancellationToken)
    {
        var results = await rows.GroupBy(item => item.StartedAtUtc.Date)
            .Select(group => new ToolDayAggregate
            {
                Day = group.Key,
                CallCount = group.LongCount(),
                CompletedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Completed),
                FailedCount = group.LongCount(item => item.Status == AiToolActivityStatus.Failed),
                CancelledCount = group.LongCount(item => item.Status == AiToolActivityStatus.Cancelled),
                TotalDurationMilliseconds = group.Sum(item => item.DurationMilliseconds)
            })
            .ToListAsync(cancellationToken);
        return results.OrderBy(item => item.Day).Select(item => new AiToolActivityAggregateRow(
                item.Day.ToString("yyyy-MM-dd"),
                null,
                item.CallCount,
                item.CompletedCount,
                item.FailedCount,
                item.CancelledCount,
                item.CallCount == 0 ? 0 : item.TotalDurationMilliseconds / item.CallCount))
            .ToList();
    }

    private static void EnsureMatchingOperation(
        AiUsageOperationRecord existing,
        AiUsageOperationContext operation)
    {
        if (existing.NormalizedUserId != NormalizeUserId(operation.UserId)
            || !string.Equals(existing.Feature, operation.Feature.Trim(), StringComparison.Ordinal)
            || !string.Equals(existing.Subfeature, operation.Subfeature.Trim(), StringComparison.Ordinal)
            || !string.Equals(existing.Application, operation.Application.Trim(), StringComparison.Ordinal)
            || existing.ExecutionType != operation.ExecutionType)
            throw new InvalidOperationException("An AI usage operation ID was reused with different attribution metadata.");
    }

    private static string Required(string value, int maximumLength, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maximumLength)
            throw new ArgumentException($"{name} must contain 1 to {maximumLength} characters.", name);
        return normalized;
    }

    private static string? Optional(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static string NormalizeUserId(string value) => value.Trim().ToUpperInvariant();

    private static void ValidateLimit(long? value, string label)
    {
        if (value is < 0)
            throw new ArgumentException($"{label} cannot be negative. Use zero for unlimited usage.");
    }

    private static IReadOnlyList<int> NormalizeThresholds(IReadOnlyList<int>? values)
    {
        var thresholds = (values ?? DefaultNotificationThresholds)
            .Distinct()
            .Order()
            .ToList();
        if (thresholds.Count is 0 or > 10 || thresholds.Any(value => value is < 1 or > 100))
            throw new ArgumentException("AI usage notification thresholds must contain 1 to 10 unique percentages from 1 through 100.");
        return thresholds;
    }

    private static IReadOnlyList<int> ParseThresholds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultNotificationThresholds;
        var parsed = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => int.TryParse(item, out var percentage) ? percentage : -1)
            .ToList();
        try
        {
            return NormalizeThresholds(parsed);
        }
        catch (ArgumentException)
        {
            return DefaultNotificationThresholds;
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            throw new ArgumentException("Select a reset timezone.");
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException("Select a supported reset timezone.", nameof(timeZoneId), exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ArgumentException("Select a supported reset timezone.", nameof(timeZoneId), exception);
        }
    }

    private static DailyWindow GetDailyWindow(DateTimeOffset nowUtc, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var localStart = DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified);
        var localEnd = localStart.AddDays(1);
        return new DailyWindow(
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone), TimeSpan.Zero),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone), TimeSpan.Zero));
    }

    private static IReadOnlyList<string> SortDistinct(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private sealed class AggregateResult
    {
        public string Primary { get; init; } = string.Empty;
        public string? Secondary { get; init; }
        public string? Tertiary { get; init; }
        public long RequestCount { get; init; }
        public long MeasuredRequestCount { get; init; }
        public long InputTokens { get; init; }
        public long OutputTokens { get; init; }
        public long TotalTokens { get; init; }
    }

    private sealed class UserConsumptionAggregate
    {
        public string NormalizedUserId { get; init; } = string.Empty;
        public string UserId { get; init; } = string.Empty;
        public long TodayTokens { get; init; }
        public long WeekTokens { get; init; }
        public long MonthTokens { get; init; }
        public long RetainedTotalTokens { get; init; }
    }

    private sealed class DayAggregate
    {
        public DateTime Day { get; init; }
        public long RequestCount { get; init; }
        public long MeasuredRequestCount { get; init; }
        public long InputTokens { get; init; }
        public long OutputTokens { get; init; }
        public long TotalTokens { get; init; }
    }

    private sealed class ToolAggregateResult
    {
        public string Primary { get; init; } = string.Empty;
        public string? Secondary { get; init; }
        public long CallCount { get; init; }
        public long CompletedCount { get; init; }
        public long FailedCount { get; init; }
        public long CancelledCount { get; init; }
        public long TotalDurationMilliseconds { get; init; }
    }

    private sealed class ToolDayAggregate
    {
        public DateTime Day { get; init; }
        public long CallCount { get; init; }
        public long CompletedCount { get; init; }
        public long FailedCount { get; init; }
        public long CancelledCount { get; init; }
        public long TotalDurationMilliseconds { get; init; }
    }

    private sealed record DailyWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
}

internal sealed class AiUsageTracker(
    IAiUsageWriter writer,
    IAiUsageLimitService limits,
    IAiUsageThresholdEvaluator thresholdEvaluator,
    AiUsageUserGate userGate,
    TimeProvider timeProvider,
    ILogger<AiUsageTracker> logger) : IAiUsageTracker
{
    public async Task<AiStructuredGenerationResult> TrackGenerationAsync(
        AiUsageOperationContext operation,
        AiUsageInvocationContext invocation,
        string expectedProvider,
        string expectedModel,
        Func<CancellationToken, Task<AiStructuredGenerationResult>> generate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(generate);
        await using var userLease = await userGate.AcquireAsync(operation.UserId, cancellationToken);
        var limitStatus = await limits.GetStatusAsync(operation.UserId, cancellationToken);
        if (limitStatus.IsExceeded)
            throw new AiUsageLimitExceededException(
                operation.UserId,
                limitStatus.DailyTokenLimit!.Value,
                limitStatus.UsedTokens,
                limitStatus.ResetsAtUtc);

        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        var timestamp = timeProvider.GetTimestamp();
        try
        {
            var result = await generate(cancellationToken);
            await TryRecordAsync(operation, invocation, AiUsageInvocationStatus.Completed,
                result.Usage is null ? AiUsageMeasurementSource.Unavailable : AiUsageMeasurementSource.ProviderReported,
                result.Provider, result.Model, result.RequestId, result.Usage, startedAt,
                timeProvider.GetElapsedTime(timestamp), null);
            if (result.Usage?.TotalTokens > 0)
                await thresholdEvaluator.EvaluateAsync(operation, CancellationToken.None);
            return result;
        }
        catch (OperationCanceledException)
        {
            await TryRecordAsync(operation, invocation, AiUsageInvocationStatus.Cancelled,
                AiUsageMeasurementSource.Unavailable, expectedProvider, expectedModel, null, null, startedAt,
                timeProvider.GetElapsedTime(timestamp), nameof(OperationCanceledException));
            throw;
        }
        catch (Exception exception)
        {
            await TryRecordAsync(operation, invocation, AiUsageInvocationStatus.Failed,
                AiUsageMeasurementSource.Unavailable, expectedProvider, expectedModel, null, null, startedAt,
                timeProvider.GetElapsedTime(timestamp), exception.GetType().FullName);
            throw;
        }
    }

    private async Task TryRecordAsync(
        AiUsageOperationContext operation,
        AiUsageInvocationContext invocation,
        AiUsageInvocationStatus status,
        AiUsageMeasurementSource measurementSource,
        string provider,
        string model,
        string? requestId,
        AiTokenUsage? usage,
        DateTime startedAt,
        TimeSpan duration,
        string? errorType)
    {
        try
        {
            await writer.RecordAsync(operation, invocation, status, measurementSource,
                provider, model, requestId, usage, startedAt, timeProvider.GetUtcNow().UtcDateTime,
                (long)Math.Max(0, duration.TotalMilliseconds), errorType, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "AI usage could not be recorded for operation {OperationId}, feature {Feature} and user {UserId}.",
                operation.OperationId, operation.Feature, operation.UserId);
        }
    }
}

internal sealed class AiUsageThresholdEvaluator(
    AiUsageDbContext dbContext,
    IAiUsageLimitService limits,
    IAiUsageThresholdNotifier notifier,
    TimeProvider timeProvider,
    ILogger<AiUsageThresholdEvaluator> logger) : IAiUsageThresholdEvaluator
{
    public async Task EvaluateAsync(
        AiUsageOperationContext operation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await limits.GetSettingsAsync(cancellationToken);
            if (!settings.NotificationsEnabled) return;
            var status = await limits.GetStatusAsync(operation.UserId, cancellationToken);
            if (!status.DailyTokenLimit.HasValue || status.DailyTokenLimit.Value <= 0) return;

            var normalizedUserId = operation.UserId.Trim().ToUpperInvariant();
            var windowStart = status.WindowStartUtc.UtcDateTime;
            var pending = await dbContext.ThresholdNotifications
                .Where(item => item.NormalizedUserId == normalizedUserId
                    && item.WindowStartUtc == windowStart
                    && item.DeliveredAtUtc == null)
                .OrderByDescending(item => item.ThresholdPercent)
                .FirstOrDefaultAsync(cancellationToken);
            if (pending is not null)
            {
                await DeliverAsync(pending, status, cancellationToken);
                return;
            }

            var thresholds = (settings.NotificationThresholdPercentages ?? [75, 90, 100])
                .Distinct()
                .Order()
                .Where(threshold => (decimal)status.UsedTokens * 100m
                    >= (decimal)status.DailyTokenLimit.Value * threshold)
                .ToList();
            if (thresholds.Count == 0) return;

            var existing = await dbContext.ThresholdNotifications.AsNoTracking()
                .Where(item => item.NormalizedUserId == normalizedUserId
                    && item.WindowStartUtc == windowStart)
                .Select(item => item.ThresholdPercent)
                .ToListAsync(cancellationToken);
            var missing = thresholds.Except(existing).Order().ToList();
            if (missing.Count == 0) return;

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var highest = missing[^1];
            AiUsageThresholdNotificationRecord? notification = null;
            foreach (var threshold in missing)
            {
                var record = new AiUsageThresholdNotificationRecord
                {
                    Id = Guid.NewGuid(),
                    NormalizedUserId = normalizedUserId,
                    UserId = operation.UserId.Trim(),
                    WindowStartUtc = windowStart,
                    WindowEndUtc = status.ResetsAtUtc.UtcDateTime,
                    ThresholdPercent = threshold,
                    UsedTokens = status.UsedTokens,
                    DailyTokenLimit = status.DailyTokenLimit.Value,
                    CreatedAtUtc = now,
                    DeliveredAtUtc = threshold == highest ? null : now
                };
                dbContext.ThresholdNotifications.Add(record);
                if (threshold == highest) notification = record;
            }
            await dbContext.SaveChangesAsync(cancellationToken);
            await DeliverAsync(notification!, status, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "AI usage threshold evaluation failed for operation {OperationId} and user {UserId}.",
                operation.OperationId, operation.UserId);
        }
    }

    private async Task DeliverAsync(
        AiUsageThresholdNotificationRecord record,
        AiUsageLimitStatus status,
        CancellationToken cancellationToken)
    {
        await notifier.NotifyAsync(new AiUsageThresholdNotification(
            record.Id,
            record.UserId,
            record.ThresholdPercent,
            status.UsedTokens,
            record.DailyTokenLimit,
            Math.Max(0, record.DailyTokenLimit - status.UsedTokens),
            status.ResetsAtUtc), cancellationToken);
        record.UsedTokens = status.UsedTokens;
        record.DeliveredAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class AiToolActivityTracker(
    IAiToolActivityWriter writer,
    TimeProvider timeProvider,
    ILogger<AiToolActivityTracker> logger) : IAiToolActivityTracker
{
    public async Task<T> TrackAsync<T>(
        AiUsageOperationContext operation,
        AiToolActivityContext activity,
        Func<CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(execute);
        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        var timestamp = timeProvider.GetTimestamp();
        try
        {
            var result = await execute(cancellationToken);
            await TryRecordAsync(operation, activity, AiToolActivityStatus.Completed, startedAt,
                timeProvider.GetElapsedTime(timestamp), null);
            return result;
        }
        catch (OperationCanceledException)
        {
            await TryRecordAsync(operation, activity, AiToolActivityStatus.Cancelled, startedAt,
                timeProvider.GetElapsedTime(timestamp), nameof(OperationCanceledException));
            throw;
        }
        catch (Exception exception)
        {
            await TryRecordAsync(operation, activity, AiToolActivityStatus.Failed, startedAt,
                timeProvider.GetElapsedTime(timestamp), exception.GetType().FullName);
            throw;
        }
    }

    private async Task TryRecordAsync(
        AiUsageOperationContext operation,
        AiToolActivityContext activity,
        AiToolActivityStatus status,
        DateTime startedAt,
        TimeSpan duration,
        string? errorType)
    {
        try
        {
            await writer.RecordToolActivityAsync(
                operation,
                activity,
                status,
                startedAt,
                timeProvider.GetUtcNow().UtcDateTime,
                (long)Math.Max(0, duration.TotalMilliseconds),
                errorType,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "AI tool activity could not be recorded for operation {OperationId}, tool {ToolName} and user {UserId}.",
                operation.OperationId, activity.ToolName, operation.UserId);
        }
    }
}

internal sealed class AiUsageUserGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates =
        new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var key = userId?.Trim() ?? string.Empty;
        var gate = gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class NullAiUsageTracker : IAiUsageTracker
{
    public Task<AiStructuredGenerationResult> TrackGenerationAsync(
        AiUsageOperationContext operation,
        AiUsageInvocationContext invocation,
        string expectedProvider,
        string expectedModel,
        Func<CancellationToken, Task<AiStructuredGenerationResult>> generate,
        CancellationToken cancellationToken = default) => generate(cancellationToken);
}

public sealed class NullAiToolActivityTracker : IAiToolActivityTracker
{
    public Task<T> TrackAsync<T>(
        AiUsageOperationContext operation,
        AiToolActivityContext activity,
        Func<CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken = default) => execute(cancellationToken);
}

public sealed class NullAiUsageThresholdNotifier : IAiUsageThresholdNotifier
{
    public Task NotifyAsync(
        AiUsageThresholdNotification notification,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
