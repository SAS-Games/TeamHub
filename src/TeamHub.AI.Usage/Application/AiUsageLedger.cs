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

internal sealed class AiUsageLedger(
    AiUsageDbContext dbContext,
    TimeProvider timeProvider) : IAiUsageLedger, IAiUsageLimitService, IAiUsageWriter
{
    private const string DefaultResetTimeZoneId = "Asia/Kolkata";

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
            userLimits);
    }

    public async Task SaveSettingsAsync(
        AiUsageLimitSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateLimit(settings.DefaultDailyTokenLimit, "The default daily token limit");
        var timeZoneId = settings.ResetTimeZoneId?.Trim() ?? string.Empty;
        _ = ResolveTimeZone(timeZoneId);

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

    public async Task<int> DeleteBeforeAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        var cutoff = cutoffUtc.UtcDateTime;
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

    private sealed class DayAggregate
    {
        public DateTime Day { get; init; }
        public long RequestCount { get; init; }
        public long MeasuredRequestCount { get; init; }
        public long InputTokens { get; init; }
        public long OutputTokens { get; init; }
        public long TotalTokens { get; init; }
    }

    private sealed record DailyWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
}

internal sealed class AiUsageTracker(
    IAiUsageWriter writer,
    IAiUsageLimitService limits,
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
