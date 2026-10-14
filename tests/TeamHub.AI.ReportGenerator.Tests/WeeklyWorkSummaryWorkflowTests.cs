using FluentAssertions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamHub.AI.Contracts;
using TeamHub.AI.ReportGenerator;
using TeamHub.AI.ReportGenerator.Contracts;
using TeamHub.AI.ReportGenerator.Persistence;

namespace TeamHub.AI.ReportGenerator.Tests;

public sealed class WeeklyWorkSummaryWorkflowTests
{
    [Fact]
    public async Task Generate_ProcessesEachStudioIndependentlyAndRecordsAudit()
    {
        var model = new StubModel();
        var runs = new StubRuns();
        using var services = Services(model, runs);
        var workflow = services.GetRequiredService<IWeeklyWorkSummaryWorkflow>();
        var request = Request(
            new("one", "First", "Reviewed ABC-123 with 2 issues.", "Review pending.", "page", 7),
            new("two", "Second", "Checked build.", "Waiting for approval.", "page", 7));

        var result = await workflow.GenerateAsync(request);

        result.Rows.Select(row => row.Studio).Should().Equal("First", "Second");
        result.Rows[0].SupportActivity.Should().Contain("Review pending.");
        model.Requests.Should().HaveCount(2);
        model.Requests[0].UserPrompt.Should().NotContain("Second").And.NotContain("Waiting for approval");
        model.Requests[1].UserPrompt.Should().NotContain("ABC-123");
        model.Requests[0].SystemPrompt.Should().Contain("management-friendly").And.Contain("untrusted data");
        model.Requests[0].ResponseSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        typeof(WeeklyWorkSummaryRow).GetProperties().Select(property => property.Name)
            .Should().Equal("Studio", "SupportActivity");
        runs.Items.Should().ContainSingle();
        runs.Items[0].ReportType.Should().Be("WeeklyWorkSummary");
        runs.Items[0].SourceHash.Should().HaveLength(64);
        runs.Items[0].PromptVersion.Should().Be(Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(model.Requests[0].SystemPrompt))));
        runs.Items[0].Status.Should().Be(AiReportRunStatus.Completed);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"hpgdsSupport\":\"\",\"notes\":\"\"}")]
    [InlineData("{\"hpgdsSupport\":null,\"notes\":\"\"}")]
    [InlineData("{\"hpgdsSupport\":5,\"notes\":\"\"}")]
    [InlineData("{\"hpgdsSupport\":\"Reviewed ABC-123 with 2 issues.\",\"notes\":\"\",\"studio\":\"Invented\"}")]
    [InlineData("{\"hpgdsSupport\":\"Reviewed ABC-123 with 3 issues.\",\"notes\":\"\"}")]
    [InlineData("{\"hpgdsSupport\":\"Reviewed ABC-124 with 2 issues.\",\"notes\":\"\"}")]
    [InlineData("{\"hpgdsSupport\":\"Reviewed ABC-123 with 2 issues and 5 improvements.\",\"notes\":\"\"}")]
    [InlineData("{\"hpgdsSupport\":\"Reviewed the issues.\",\"notes\":\"\"}")]
    [InlineData("{\"hpgdsSupport\":\"Reviewed ABC-123 with 2 issues.\",\"notes\":\"Invented notes.\"}")]
    public async Task Generate_RejectsMalformedOrChangedProtectedContentWithoutAudit(string content)
    {
        var runs = new StubRuns();
        using var services = Services(new StubModel { Content = content }, runs);
        var workflow = services.GetRequiredService<IWeeklyWorkSummaryWorkflow>();
        var act = () => workflow.GenerateAsync(Request(new WeeklyWorkSummarySource("one", "First",
            "Reviewed ABC-123 with 2 issues.", "", "page", 1)));
        await act.Should().ThrowAsync<WeeklyWorkSummaryException>();
        runs.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, "reviewed", "")]
    [InlineData(true, "", "")]
    public async Task Generate_RejectsDisabledOrEmptySourcesBeforeInference(bool enabled, string support, string notes)
    {
        var model = new StubModel();
        using var services = Services(model, new StubRuns(), enabled);
        var workflow = services.GetRequiredService<IWeeklyWorkSummaryWorkflow>();
        var act = () => workflow.GenerateAsync(Request(
            new WeeklyWorkSummarySource("one", "First", support, notes, "page", 1)));
        await act.Should().ThrowAsync<WeeklyWorkSummaryException>();
        model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Generate_DoesNotTruncateOversizedInput()
    {
        var model = new StubModel();
        using var services = Services(model, new StubRuns());
        var act = () => services.GetRequiredService<IWeeklyWorkSummaryWorkflow>().GenerateAsync(
            Request(new WeeklyWorkSummarySource("one", "First", new string('x', 24001), "", "page", 1)));
        await act.Should().ThrowAsync<WeeklyWorkSummaryException>().WithMessage("*No content was truncated*");
        model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Generate_PreservesUrlsFromConfluenceMarkdownLinks()
    {
        using var services = Services(new StubModel
        {
            Content = "{\"hpgdsSupport\":\"Reviewed https://example.test/build/123.\",\"notes\":\"\"}"
        }, new StubRuns());
        var result = await services.GetRequiredService<IWeeklyWorkSummaryWorkflow>().GenerateAsync(
            Request(new WeeklyWorkSummarySource("one", "First", "Reviewed [build](https://example.test/build/123).", "", "page", 1)));
        result.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Generate_RejectsUnavailableProvider()
    {
        var model = new StubModel { Available = false };
        using var services = Services(model, new StubRuns());
        var act = () => services.GetRequiredService<IWeeklyWorkSummaryWorkflow>().GenerateAsync(
            Request(new WeeklyWorkSummarySource("one", "First", "Reviewed build.", "", "page", 1)));
        await act.Should().ThrowAsync<AiModelProviderException>();
        model.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Generate_RejectsOmittedNotes()
    {
        using var services = Services(new StubModel
        {
            Content = "{\"hpgdsSupport\":\"Reviewed build.\",\"notes\":\"\"}"
        }, new StubRuns());
        var act = () => services.GetRequiredService<IWeeklyWorkSummaryWorkflow>().GenerateAsync(
            Request(new WeeklyWorkSummarySource("one", "First", "Reviewed build.", "Approval pending.", "page", 1)));
        await act.Should().ThrowAsync<WeeklyWorkSummaryException>().WithMessage("*omitted*notes*");
    }

    [Fact]
    public async Task Generate_HandlesNotesOnlyWithoutInventingSupport()
    {
        using var services = Services(new StubModel(), new StubRuns());
        var result = await services.GetRequiredService<IWeeklyWorkSummaryWorkflow>().GenerateAsync(
            Request(new WeeklyWorkSummarySource("one", "First", "", "Approval pending.", "page", 1)));
        result.Rows.Should().ContainSingle().Which.SupportActivity.Should().Be("Approval pending.");
    }

    [Fact]
    public async Task Generate_RejectsChangedTechnicalIdentifiers()
    {
        using var services = Services(new StubModel
        {
            Content = "{\"hpgdsSupport\":\"Reviewed PS4 build v1.3.\",\"notes\":\"\"}"
        }, new StubRuns());
        var act = () => services.GetRequiredService<IWeeklyWorkSummaryWorkflow>().GenerateAsync(
            Request(new WeeklyWorkSummarySource("one", "First", "Reviewed PS5 build v1.2.", "", "page", 1)));
        await act.Should().ThrowAsync<WeeklyWorkSummaryException>().WithMessage("*protected*");
    }

    [Fact]
    public async Task Generate_RejectsDuplicateStudiosAndNonWeeklyRanges()
    {
        using var services = Services(new StubModel(), new StubRuns());
        var workflow = services.GetRequiredService<IWeeklyWorkSummaryWorkflow>();
        var source = new WeeklyWorkSummarySource("one", "First", "Reviewed build.", "", "page", 1);
        var duplicate = () => workflow.GenerateAsync(Request(source, source));
        await duplicate.Should().ThrowAsync<WeeklyWorkSummaryException>();
        var invalidWeek = () => workflow.GenerateAsync(Request(source) with { WeekEnd = new DateOnly(2026, 9, 5) });
        await invalidWeek.Should().ThrowAsync<WeeklyWorkSummaryException>();
    }

    [Fact]
    public async Task Generate_BoundsConcurrencyAndReleasesSlotAfterCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new StubModel { Entered = entered };
        using var services = Services(model, new StubRuns());
        var workflow = services.GetRequiredService<IWeeklyWorkSummaryWorkflow>();
        var request = Request(new WeeklyWorkSummarySource("one", "First", "Reviewed build.", "", "page", 1));
        using var cancellation = new CancellationTokenSource();
        var first = workflow.GenerateAsync(request, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrent = () => workflow.GenerateAsync(request);
        await concurrent.Should().ThrowAsync<WeeklyWorkSummaryException>().WithMessage("*busy*");
        cancellation.Cancel();
        var cancelled = () => first;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        model.Entered = null;
        (await workflow.GenerateAsync(request)).Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Generate_ReloadsMarkdownBetweenReportsButSnapshotsItForAllStudiosInOneReport()
    {
        var path = Path.Combine(Path.GetTempPath(), $"teamhub-prompt-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(path, "# First instructions\nKeep meaning.");
            var model = new StubModel
            {
                AfterGeneration = () => File.WriteAllTextAsync(path, "# Updated instructions\nUse simple English.")
            };
            var runs = new StubRuns();
            using var services = Services(model, runs, promptPath: path);
            var workflow = services.GetRequiredService<IWeeklyWorkSummaryWorkflow>();
            var request = Request(
                new("one", "First", "Reviewed build.", "", "page", 1),
                new("two", "Second", "Checked build.", "", "page", 1));

            await workflow.GenerateAsync(request);
            model.Requests.Select(item => item.SystemPrompt).Should().OnlyContain(
                value => value == "# First instructions\nKeep meaning.");
            model.AfterGeneration = null;
            await workflow.GenerateAsync(request);
            model.Requests.Skip(2).Select(item => item.SystemPrompt).Should().OnlyContain(
                value => value == "# Updated instructions\nUse simple English.");
            runs.Items[0].PromptVersion.Should().NotBe(runs.Items[1].PromptVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public async Task Generate_RejectsMissingOrEmptyInstructionFileBeforeInference(string? content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"teamhub-prompt-{Guid.NewGuid():N}.md");
        try
        {
            if (content is not null) await File.WriteAllTextAsync(path, content);
            var model = new StubModel();
            var runs = new StubRuns();
            using var services = Services(model, runs, promptPath: path);
            var workflow = services.GetRequiredService<IWeeklyWorkSummaryWorkflow>();
            var act = () => workflow.GenerateAsync(Request(
                new WeeklyWorkSummarySource("one", "First", "Reviewed build.", "", "page", 1)));
            await act.Should().ThrowAsync<WeeklyWorkSummaryException>().WithMessage("*instruction file*");
            model.Requests.Should().BeEmpty();
            runs.Items.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static WeeklyWorkSummaryRequest Request(params WeeklyWorkSummarySource[] sources) =>
        new(new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 4), "reader@example.com", sources);

    [Fact]
    public async Task Generate_UsesSavedEnablementLimitsAndPromptInsteadOfBootstrapDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"teamhub-configured-prompt-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(path, "Configured instructions");
            var settings = new StubConfiguration
            {
                Snapshot = new AiConfigurationSnapshot
                {
                    ReportGenerator = new AiReportGeneratorOptions
                    {
                        Enabled = true,
                        MaximumSourceCharactersPerStudio = 20,
                        MaximumOutputTokens = 1234,
                        WeeklyWorkSummaryPromptPath = path
                    }
                }
            };
            var model = new StubModel();
            using var services = Services(model, new StubRuns(), enabled: false, savedSettings: settings);
            var workflow = services.GetRequiredService<IWeeklyWorkSummaryWorkflow>();
            var request = Request(new WeeklyWorkSummarySource("one", "First", "Reviewed build.", "", "page", 1));

            (await workflow.GenerateAsync(request)).Rows.Should().ContainSingle();
            model.Requests.Single().MaximumOutputTokens.Should().Be(1234);
            model.Requests.Single().SystemPrompt.Should().Be("Configured instructions");
            settings.Snapshot.ReportGenerator.MaximumSourceCharactersPerStudio = 5;
            var tooLarge = () => workflow.GenerateAsync(request);
            await tooLarge.Should().ThrowAsync<WeeklyWorkSummaryException>().WithMessage("*source limit*");
            settings.Snapshot.ReportGenerator.Enabled = false;
            var disabled = () => workflow.GenerateAsync(request);
            await disabled.Should().ThrowAsync<WeeklyWorkSummaryException>().WithMessage("*disabled*");
            model.Requests.Should().ContainSingle();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Generate_UpdatesSharedConcurrencyLimitWithoutRestartOrCancellingActiveReports()
    {
        var settings = new StubConfiguration
        {
            Snapshot = new AiConfigurationSnapshot
            {
                ReportGenerator = new AiReportGeneratorOptions { Enabled = true }
            }
        };
        var model = new StubModel { Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var services = Services(model, new StubRuns(), savedSettings: settings);
        using var firstScope = services.CreateScope();
        using var secondScope = services.CreateScope();
        var firstWorkflow = firstScope.ServiceProvider.GetRequiredService<IWeeklyWorkSummaryWorkflow>();
        var secondWorkflow = secondScope.ServiceProvider.GetRequiredService<IWeeklyWorkSummaryWorkflow>();
        var request = Request(new WeeklyWorkSummarySource("one", "First", "Reviewed build.", "", "page", 1));
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var first = firstWorkflow.GenerateAsync(request, firstCancellation.Token);
        await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var busy = () => secondWorkflow.GenerateAsync(request);
        await busy.Should().ThrowAsync<WeeklyWorkSummaryException>().WithMessage("*busy*");
        settings.Snapshot.ReportGenerator.MaximumConcurrentRequests = 2;
        model.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = secondWorkflow.GenerateAsync(request, secondCancellation.Token);
        await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.Snapshot.ReportGenerator.MaximumConcurrentRequests = 1;
        await busy.Should().ThrowAsync<WeeklyWorkSummaryException>().WithMessage("*busy*");
        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();
        firstCancellation.Cancel();
        var cancelledFirst = () => first;
        await cancelledFirst.Should().ThrowAsync<OperationCanceledException>();
        await busy.Should().ThrowAsync<WeeklyWorkSummaryException>().WithMessage("*busy*");
        secondCancellation.Cancel();
        var cancelledSecond = () => second;
        await cancelledSecond.Should().ThrowAsync<OperationCanceledException>();
        model.Entered = null;
        (await firstWorkflow.GenerateAsync(request)).Rows.Should().ContainSingle();
    }

    private static ServiceProvider Services(StubModel model, StubRuns runs, bool enabled = true,
        string? promptPath = null, StubConfiguration? savedSettings = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AI:ReportGenerator:Enabled"] = enabled.ToString()
        };
        if (promptPath is not null) settings["AI:ReportGenerator:WeeklyWorkSummaryPromptPath"] = promptPath;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddReportGenerator(configuration);
        if (savedSettings is not null) services.AddSingleton<IAiConfigurationService>(savedSettings);
        services.AddSingleton<IAiModelService>(model);
        services.AddSingleton<IAiReportRunRepository>(runs);
        return services.BuildServiceProvider();
    }

    private sealed class StubConfiguration : IAiConfigurationService
    {
        public AiConfigurationSnapshot Snapshot { get; set; } = new();
        public Task<AiConfigurationSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);
        public Task SaveAsync(SaveAiConfigurationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<string?> GetCredentialAsync(AiProviderSettings settings, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);
    }

    private sealed class StubModel : IAiModelService
    {
        public string? Content { get; set; }
        public bool Available { get; set; } = true;
        public TaskCompletionSource? Entered { get; set; }
        public Func<Task>? AfterGeneration { get; set; }
        public List<AiStructuredGenerationRequest> Requests { get; } = [];
        public Task<AiProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiProviderHealthResult(Available, "Test", "test-model", Available, "Test"));
        public async Task<AiStructuredGenerationResult> GenerateStructuredAsync(
            AiStructuredGenerationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Entered is not null)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (AfterGeneration is not null) await AfterGeneration();
            return new("Test", "test-model", Content ?? request.UserPrompt);
        }
    }

    private sealed class StubRuns : IAiReportRunRepository
    {
        public List<AiReportRunSnapshot> Items { get; } = [];
        public Task AddAsync(AiReportRunSnapshot run, CancellationToken cancellationToken = default)
        {
            Items.Add(run);
            return Task.CompletedTask;
        }
        public Task<AiReportRunSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.SingleOrDefault(run => run.Id == id));
    }
}
