using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.AI.Contracts;
using TeamHub.AI.Usage;
using TeamHub.AI.Usage.Contracts;

namespace TeamHub.AI.Usage.Tests;

public sealed class AiUsageLedgerTests
{
    [Fact]
    public async Task Tracks_multiple_model_calls_for_one_user_operation_and_reports_dimensions()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-ai-usage-{Guid.NewGuid():N}.db");
        try
        {
            await using (var services = CreateServices(databasePath))
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IAiUsageDatabaseInitializer>().InitializeAsync();
                var tracker = scope.ServiceProvider.GetRequiredService<IAiUsageTracker>();
                var operation = new AiUsageOperationContext(
                    Guid.NewGuid(), "User@Example.com", AiUsageFeatures.ReportGenerator,
                    "WeeklyWorkSummary", AiUsageApplications.TeamHubWeb);

                await tracker.TrackGenerationAsync(
                    operation,
                    new AiUsageInvocationContext("Studio", "studio-a"),
                    "PfbAiGateway",
                    "model-a",
                    _ => Task.FromResult(new AiStructuredGenerationResult(
                        "PfbAiGateway", "model-a", "{}", "request-a", new AiTokenUsage(10, 2, 12))));
                await tracker.TrackGenerationAsync(
                    operation,
                    new AiUsageInvocationContext("Studio", "studio-b"),
                    "PfbAiGateway",
                    "model-b",
                    _ => Task.FromResult(new AiStructuredGenerationResult(
                        "PfbAiGateway", "model-b", "{}", "request-b", new AiTokenUsage(20, 3, 23))));

                var ledger = scope.ServiceProvider.GetRequiredService<IAiUsageLedger>();
                var report = await ledger.GetReportAsync(
                    new AiUsageQuery(
                        DateTimeOffset.UtcNow.AddMinutes(-5),
                        DateTimeOffset.UtcNow.AddMinutes(5),
                        UserId: "user@example.com"),
                    AiUsageDimension.FeatureModel);

                report.Totals.Should().Be(new AiUsageTotals(2, 2, 30, 5, 35));
                report.Rows.Should().BeEquivalentTo(
                [
                    new AiUsageAggregateRow(AiUsageFeatures.ReportGenerator, "model-b", 1, 1, 20, 3, 23),
                    new AiUsageAggregateRow(AiUsageFeatures.ReportGenerator, "model-a", 1, 1, 10, 2, 12)
                ], options => options.WithStrictOrdering());

                var daily = await ledger.GetReportAsync(
                    new AiUsageQuery(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5)),
                    AiUsageDimension.Day);
                daily.Rows.Should().ContainSingle().Which.TotalTokens.Should().Be(35);

                var filters = await ledger.GetFilterOptionsAsync(
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    DateTimeOffset.UtcNow.AddMinutes(5));
                filters.Subfeatures.Should().ContainSingle().Which.Should().Be("WeeklyWorkSummary");
                filters.Applications.Should().ContainSingle().Which.Should().Be(AiUsageApplications.TeamHubWeb);
                filters.Providers.Should().ContainSingle().Which.Should().Be("PfbAiGateway");
                filters.Models.Should().BeEquivalentTo(["model-a", "model-b"]);

                var byUserFeatureModel = await ledger.GetReportAsync(
                    new AiUsageQuery(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5)),
                    AiUsageDimension.UserFeatureModel);
                byUserFeatureModel.Rows.Should().Contain(row =>
                    row.Key == "User@Example.com"
                    && row.SecondaryKey == AiUsageFeatures.ReportGenerator
                    && row.TertiaryKey == "model-a"
                    && row.TotalTokens == 12);
            }
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Records_unavailable_usage_and_failed_invocations_without_hiding_provider_errors()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-ai-usage-{Guid.NewGuid():N}.db");
        try
        {
            await using (var services = CreateServices(databasePath))
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IAiUsageDatabaseInitializer>().InitializeAsync();
                var tracker = scope.ServiceProvider.GetRequiredService<IAiUsageTracker>();
                var operation = new AiUsageOperationContext(
                    Guid.NewGuid(), "user@example.com", AiUsageFeatures.Chat,
                    "General", AiUsageApplications.TeamHubWeb);

                await tracker.TrackGenerationAsync(
                    operation, new(), "Ollama", "local-model",
                    _ => Task.FromResult(new AiStructuredGenerationResult("Ollama", "local-model", "{}")));
                var action = () => tracker.TrackGenerationAsync(
                    operation, new(), "Ollama", "local-model",
                    _ => throw new AiModelProviderException("provider failed"));
                await action.Should().ThrowAsync<AiModelProviderException>();

                var report = await scope.ServiceProvider.GetRequiredService<IAiUsageLedger>().GetReportAsync(
                    new AiUsageQuery(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5)),
                    AiUsageDimension.Feature);
                report.Totals.Should().Be(new AiUsageTotals(2, 0, 0, 0, 0));
                report.Rows.Should().ContainSingle().Which.RequestCount.Should().Be(2);
            }
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Tracks_tool_activity_separately_without_inflating_provider_calls()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-ai-usage-{Guid.NewGuid():N}.db");
        try
        {
            await using var services = CreateServices(databasePath);
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAiUsageDatabaseInitializer>().InitializeAsync();
            var tracker = scope.ServiceProvider.GetRequiredService<IAiToolActivityTracker>();
            var operation = new AiUsageOperationContext(
                Guid.NewGuid(), "user@example.com", AiUsageFeatures.Chat,
                "TeamHub", AiUsageApplications.TeamHubWeb);

            await tracker.TrackAsync(
                operation,
                new AiToolActivityContext(
                    "get_studio_support_overview", "Studio", "Example Studio"),
                _ => Task.FromResult("ok"));
            var failed = () => tracker.TrackAsync<string>(
                operation,
                new AiToolActivityContext(
                    "get_studio_jira_tickets", "Studio", "Example Studio", "Jira"),
                _ => throw new InvalidOperationException("Jira unavailable"));
            await failed.Should().ThrowAsync<InvalidOperationException>();

            var ledger = scope.ServiceProvider.GetRequiredService<IAiToolActivityLedger>();
            var from = DateTimeOffset.UtcNow.AddMinutes(-5);
            var to = DateTimeOffset.UtcNow.AddMinutes(5);
            var report = await ledger.GetToolReportAsync(
                new AiToolActivityQuery(from, to, UserId: "USER@example.com"),
                AiToolActivityDimension.UserTool);

            report.Totals.CallCount.Should().Be(2);
            report.Totals.CompletedCount.Should().Be(1);
            report.Totals.FailedCount.Should().Be(1);
            report.Totals.CancelledCount.Should().Be(0);
            report.Totals.ExternalCallCount.Should().Be(1);
            report.Totals.TotalDurationMilliseconds.Should().BeGreaterThanOrEqualTo(0);
            report.Rows.Should().HaveCount(2);
            report.Rows.Should().Contain(row =>
                row.Key == "user@example.com"
                && row.SecondaryKey == "get_studio_support_overview"
                && row.CompletedCount == 1);
            report.Rows.Should().Contain(row =>
                row.SecondaryKey == "get_studio_jira_tickets"
                && row.FailedCount == 1);

            var resourceReport = await ledger.GetToolReportAsync(
                new AiToolActivityQuery(from, to),
                AiToolActivityDimension.ToolResource);
            resourceReport.Rows.Should().Contain(row =>
                row.Key == "get_studio_support_overview"
                && row.SecondaryKey == "Example Studio");
            resourceReport.Rows.Should().Contain(row =>
                row.Key == "get_studio_jira_tickets"
                && row.SecondaryKey == "Not specified");
            var filters = await ledger.GetToolFilterOptionsAsync(from, to);
            filters.ToolNames.Should().BeEquivalentTo(
                ["get_studio_support_overview", "get_studio_jira_tickets"]);
            filters.ExternalSources.Should().ContainSingle().Which.Should().Be("Jira");

            var providerReport = await scope.ServiceProvider.GetRequiredService<IAiUsageLedger>()
                .GetReportAsync(new AiUsageQuery(from, to), AiUsageDimension.User);
            providerReport.Totals.RequestCount.Should().Be(0);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Enforces_default_and_per_user_daily_limits_and_resets_at_local_midnight()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-ai-usage-{Guid.NewGuid():N}.db");
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        try
        {
            await using var services = CreateServices(databasePath, time);
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAiUsageDatabaseInitializer>().InitializeAsync();
            var limits = scope.ServiceProvider.GetRequiredService<IAiUsageLimitService>();
            await limits.SaveSettingsAsync(new AiUsageLimitSettings(
                10,
                "UTC",
                [
                    new AiUsageUserDailyLimit("override@example.com", 5),
                    new AiUsageUserDailyLimit("unlimited@example.com", 0)
                ]));
            var tracker = scope.ServiceProvider.GetRequiredService<IAiUsageTracker>();

            await RecordUsageAsync(tracker, "default@example.com", 12);
            var blocked = () => RecordUsageAsync(tracker, "default@example.com", 1);
            var exception = await blocked.Should().ThrowAsync<AiUsageLimitExceededException>();
            exception.Which.DailyTokenLimit.Should().Be(10);
            exception.Which.UsedTokens.Should().Be(12);

            await RecordUsageAsync(tracker, "override@example.com", 6);
            var overrideBlocked = () => RecordUsageAsync(tracker, "override@example.com", 1);
            await overrideBlocked.Should().ThrowAsync<AiUsageLimitExceededException>();

            await RecordUsageAsync(tracker, "unlimited@example.com", 100);
            await RecordUsageAsync(tracker, "unlimited@example.com", 100);

            time.UtcNow = new DateTimeOffset(2026, 11, 4, 0, 0, 1, TimeSpan.Zero);
            await RecordUsageAsync(tracker, "default@example.com", 1);
            (await limits.GetStatusAsync("default@example.com")).UsedTokens.Should().Be(1);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Notifies_only_highest_new_threshold_once_per_daily_window()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-ai-usage-{Guid.NewGuid():N}.db");
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        var notifier = new RecordingThresholdNotifier();
        try
        {
            await using var services = CreateServices(databasePath, time, notifier);
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAiUsageDatabaseInitializer>().InitializeAsync();
            var limits = scope.ServiceProvider.GetRequiredService<IAiUsageLimitService>();
            await limits.SaveSettingsAsync(new AiUsageLimitSettings(
                100,
                "UTC",
                [],
                true,
                [100, 75, 90, 90]));
            var tracker = scope.ServiceProvider.GetRequiredService<IAiUsageTracker>();

            await RecordUsageAsync(tracker, "user@example.com", 95);
            await RecordUsageAsync(tracker, "user@example.com", 1);
            await RecordUsageAsync(tracker, "user@example.com", 5);

            notifier.Notifications.Select(item => item.ThresholdPercent)
                .Should().Equal(90, 100);
            notifier.Notifications.Should().OnlyContain(item =>
                item.UserId == "user@example.com"
                && item.DailyTokenLimit == 100);
            notifier.Notifications.Select(item => item.NotificationId)
                .Should().OnlyHaveUniqueItems();
            var blocked = () => RecordUsageAsync(tracker, "user@example.com", 1);
            await blocked.Should().ThrowAsync<AiUsageLimitExceededException>();
            notifier.Notifications.Should().HaveCount(2);

            time.UtcNow = new DateTimeOffset(2026, 11, 4, 0, 0, 1, TimeSpan.Zero);
            await RecordUsageAsync(tracker, "user@example.com", 80);

            notifier.Notifications.Select(item => item.ThresholdPercent)
                .Should().Equal(90, 100, 75);
            notifier.Notifications[2].ResetsAtUtc.Should()
                .Be(new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Does_not_notify_when_warnings_are_disabled_or_user_is_unlimited()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-ai-usage-{Guid.NewGuid():N}.db");
        var notifier = new RecordingThresholdNotifier();
        try
        {
            await using var services = CreateServices(databasePath, thresholdNotifier: notifier);
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAiUsageDatabaseInitializer>().InitializeAsync();
            var limits = scope.ServiceProvider.GetRequiredService<IAiUsageLimitService>();
            var tracker = scope.ServiceProvider.GetRequiredService<IAiUsageTracker>();

            await limits.SaveSettingsAsync(new AiUsageLimitSettings(100, "UTC", [], false, [75]));
            await RecordUsageAsync(tracker, "limited@example.com", 80);
            await limits.SaveSettingsAsync(new AiUsageLimitSettings(null, "UTC", [], true, [75]));
            await RecordUsageAsync(tracker, "unlimited@example.com", 80);

            notifier.Notifications.Should().BeEmpty();
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Retention_cleanup_deletes_old_operations_and_threshold_markers()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-ai-usage-{Guid.NewGuid():N}.db");
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        var notifier = new RecordingThresholdNotifier();
        try
        {
            await using var services = CreateServices(databasePath, time, notifier);
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAiUsageDatabaseInitializer>().InitializeAsync();
            var limits = scope.ServiceProvider.GetRequiredService<IAiUsageLimitService>();
            await limits.SaveSettingsAsync(new AiUsageLimitSettings(100, "UTC", [], true, [75]));
            await RecordUsageAsync(
                scope.ServiceProvider.GetRequiredService<IAiUsageTracker>(),
                "user@example.com",
                80);

            var deleted = await scope.ServiceProvider.GetRequiredService<IAiUsageLedger>()
                .DeleteBeforeAsync(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero));

            deleted.Should().Be(1);
            await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM AiUsageOperations),
                    (SELECT COUNT(*) FROM AiUsageThresholdNotifications);
                """;
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt32(0).Should().Be(0);
            reader.GetInt32(1).Should().Be(0);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Summarizes_each_users_today_week_month_and_retained_consumption()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-ai-usage-{Guid.NewGuid():N}.db");
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        try
        {
            await using var services = CreateServices(databasePath, time);
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAiUsageDatabaseInitializer>().InitializeAsync();
            await scope.ServiceProvider.GetRequiredService<IAiUsageLimitService>()
                .SaveSettingsAsync(new AiUsageLimitSettings(null, "UTC", []));
            var tracker = scope.ServiceProvider.GetRequiredService<IAiUsageTracker>();

            await RecordUsageAsync(tracker, "user-a@example.com", 100);
            time.UtcNow = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
            await RecordUsageAsync(tracker, "user-a@example.com", 20);
            time.UtcNow = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
            await RecordUsageAsync(tracker, "user-a@example.com", 30);
            time.UtcNow = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
            await RecordUsageAsync(tracker, "user-a@example.com", 40);
            await RecordUsageAsync(tracker, "user-b@example.com", 5);
            time.UtcNow = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

            var summary = await scope.ServiceProvider.GetRequiredService<IAiUsageLedger>()
                .GetUserConsumptionSummaryAsync();

            summary.TimeZoneId.Should().Be("UTC");
            summary.Users.Should().Equal(
                new AiUsageUserConsumption("user-a@example.com", 40, 70, 90, 190),
                new AiUsageUserConsumption("user-b@example.com", 5, 5, 5, 5));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Startup_initializer_adds_new_ledger_tables_to_an_existing_usage_database()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"teamhub-ai-usage-{Guid.NewGuid():N}.db");
        try
        {
            await using (var services = CreateServices(databasePath))
            await using (var scope = services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<IAiUsageDatabaseInitializer>()
                    .InitializeAsync();

            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP TABLE AiToolActivities;
                    DROP TABLE AiUsageThresholdNotifications;
                    DROP TABLE AiUsageLimitSettings;
                    CREATE TABLE AiUsageLimitSettings (
                        Id INTEGER NOT NULL PRIMARY KEY,
                        DefaultDailyTokenLimit INTEGER NULL,
                        ResetTimeZoneId TEXT NOT NULL);
                    INSERT INTO AiUsageLimitSettings (Id, DefaultDailyTokenLimit, ResetTimeZoneId)
                    VALUES (1, 200, 'UTC');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using (var services = CreateServices(databasePath))
            await using (var scope = services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IAiUsageDatabaseInitializer>()
                    .InitializeAsync();
                var settings = await scope.ServiceProvider.GetRequiredService<IAiUsageLimitService>()
                    .GetSettingsAsync();
                settings.DefaultDailyTokenLimit.Should().Be(200);
                settings.NotificationsEnabled.Should().BeTrue();
                settings.NotificationThresholdPercentages.Should().Equal(75, 90, 100);
                var tracker = scope.ServiceProvider.GetRequiredService<IAiToolActivityTracker>();
                await tracker.TrackAsync(
                    new AiUsageOperationContext(
                        Guid.NewGuid(), "user@example.com", AiUsageFeatures.Chat,
                        "TeamHub", AiUsageApplications.TeamHubWeb),
                    new AiToolActivityContext("list_studios"),
                    _ => Task.FromResult("ok"));
                var report = await scope.ServiceProvider.GetRequiredService<IAiToolActivityLedger>()
                    .GetToolReportAsync(
                        new AiToolActivityQuery(
                            DateTimeOffset.UtcNow.AddMinutes(-5),
                            DateTimeOffset.UtcNow.AddMinutes(5)),
                        AiToolActivityDimension.Tool);
                report.Totals.CallCount.Should().Be(1);
            }

            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'AiUsageThresholdNotifications';";
                Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(1);
            }
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static Task<AiStructuredGenerationResult> RecordUsageAsync(
        IAiUsageTracker tracker,
        string userId,
        int totalTokens) => tracker.TrackGenerationAsync(
        new AiUsageOperationContext(
            Guid.NewGuid(), userId, AiUsageFeatures.Chat, "General", AiUsageApplications.TeamHubWeb),
        new(),
        "PfbAiGateway",
        "model-a",
        _ => Task.FromResult(new AiStructuredGenerationResult(
            "PfbAiGateway",
            "model-a",
            "{}",
            Usage: new AiTokenUsage(totalTokens, 0, totalTokens))));

    private static ServiceProvider CreateServices(
        string databasePath,
        TimeProvider? timeProvider = null,
        IAiUsageThresholdNotifier? thresholdNotifier = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AiUsageDb"] = $"Data Source={databasePath};Pooling=False"
            })
            .Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        if (timeProvider is not null) collection.AddSingleton(timeProvider);
        collection.AddAiUsageLedger(configuration);
        if (thresholdNotifier is not null)
            collection.AddSingleton(thresholdNotifier);
        return collection.BuildServiceProvider();
    }

    private sealed class RecordingThresholdNotifier : IAiUsageThresholdNotifier
    {
        public List<AiUsageThresholdNotification> Notifications { get; } = [];

        public Task NotifyAsync(
            AiUsageThresholdNotification notification,
            CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
