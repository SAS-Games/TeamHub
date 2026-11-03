using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using TeamHub.AI.Contracts;
using TeamHub.Infrastructure.Persistence;
using TeamHub.Infrastructure.Services;
using TeamHub.Studio;
using TeamHub.Web.AI;

namespace TeamHub.Tests;

public sealed class MeetingNotesTests
{
    [Fact]
    public async Task Instruction_provider_appends_managed_studio_instructions_and_keeps_legacy_path_fallback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"teamhub-meeting-notes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "AI", "MeetingNotes"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "AI", "MeetingNotes.md"), "Shared format");
            await File.WriteAllTextAsync(Path.Combine(root, "AI", "MeetingNotes", "StudioB.md"), "Studio B legacy terminology");
            var provider = new MeetingNotesInstructionFileProvider(
                new WebHostEnvironmentStub(root),
                new InstructionConfigurationStub(new AiMeetingNotesOptions
                {
                    InstructionPath = Path.Combine("AI", "MeetingNotes.md"),
                    StudioInstructions = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["studio-a"] = new AiMeetingNotesStudioInstructionOptions
                        {
                            FileName = "StudioA.md",
                            Content = "Studio A managed terminology"
                        }
                    },
                    StudioInstructionPaths = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["studio-b"] = Path.Combine("AI", "MeetingNotes", "StudioB.md")
                    }
                }),
                NullLogger<MeetingNotesInstructionFileProvider>.Instance);

            var studioInstructions = await provider.GetInstructionsAsync("STUDIO-A");
            var legacyInstructions = await provider.GetInstructionsAsync("studio-b");
            var defaultInstructions = await provider.GetInstructionsAsync("studio-without-additional-instructions");

            studioInstructions.Should().Contain("Shared format").And.Contain("Studio A managed terminology");
            studioInstructions.Should().StartWith("Shared format")
                .And.Contain("# Studio-specific additional instructions");
            legacyInstructions.Should().Contain("Shared format").And.Contain("Studio B legacy terminology");
            defaultInstructions.Should().Be("Shared format");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Teams_configuration_encrypts_webhook_and_blank_input_keeps_it()
    {
        await using var context = CreateDbContext();
        var configuration = new MeetingNotesTeamsConfigurationService(
            context,
            new EphemeralDataProtectionProvider());
        const string webhook = "https://prod-01.westus.logic.azure.com/workflows/test/triggers/manual/paths/invoke";

        await configuration.SaveAsync([
            new SaveMeetingNotesTeamsChannel("studio-a", webhook, false)
        ], "admin@example.com");
        context.ChangeTracker.Clear();
        await configuration.SaveAsync([
            new SaveMeetingNotesTeamsChannel("studio-a", null, false)
        ], "admin@example.com");

        var record = await context.MeetingNotesTeamsChannels.AsNoTracking().SingleAsync();
        record.WebhookUrlProtected.Should().NotBe(webhook).And.NotContain("logic.azure.com");
        (await configuration.GetWebhookAsync("studio-a"))!.AbsoluteUri.Should().Be(webhook);
        (await configuration.ListAsync()).Should().ContainSingle().Which.Should().Be(
            new MeetingNotesTeamsChannelStatus("studio-a", true));
    }

    [Fact]
    public async Task Teams_configuration_rejects_non_Microsoft_webhook_and_can_remove_existing_row()
    {
        await using var context = CreateDbContext();
        var configuration = new MeetingNotesTeamsConfigurationService(
            context,
            new EphemeralDataProtectionProvider());
        var invalid = () => configuration.SaveAsync([
            new SaveMeetingNotesTeamsChannel("studio-a", "https://example.com/hook", false)
        ], "admin@example.com");

        await invalid.Should().ThrowAsync<ArgumentException>().WithMessage("*Microsoft Teams Workflow*");
        await configuration.SaveAsync([
            new SaveMeetingNotesTeamsChannel(
                "studio-a", "https://prod-01.westus.logic.azure.com/test", false)
        ], "admin@example.com");
        await configuration.SaveAsync([
            new SaveMeetingNotesTeamsChannel("studio-a", null, true)
        ], "admin@example.com");

        (await configuration.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Teams_publisher_exposes_only_configured_active_studios_and_posts_notes()
    {
        var webhook = "https://prod-01.westus.logic.azure.com/workflows/test/triggers/manual/paths/invoke";
        var handler = new RecordingHandler();
        var teams = new TeamsConfigurationStub([
            new MeetingNotesTeamsChannelStatus("studio-a", true),
            new MeetingNotesTeamsChannelStatus("studio-b", false),
            new MeetingNotesTeamsChannelStatus("studio-c", true)
        ], new Uri(webhook));
        var publisher = new MeetingNotesTeamsPublisher(
            new ConfigurationStub(),
            teams,
            new StudioDirectoryStub([
                new StudioDetails { Id = "studio-a", StudioName = "Studio A", IsActive = true },
                new StudioDetails { Id = "studio-b", StudioName = "Studio B", IsActive = true },
                new StudioDetails { Id = "studio-c", StudioName = "Studio C", IsActive = false }
            ]),
            new HttpClient(handler),
            NullLogger<MeetingNotesTeamsPublisher>.Instance);

        var destinations = await publisher.ListDestinationsAsync();
        await publisher.PublishAsync("studio-a", "# Notes\n\n- Decision", "Test User");

        destinations.Should().ContainSingle().Which.Should().Be(
            new MeetingNotesTeamsDestination("studio-a", "Studio A"));
        handler.RequestUri.Should().Be(webhook);
        using var payload = JsonDocument.Parse(handler.Body!);
        payload.RootElement.GetProperty("text").GetString().Should().Be("# Notes\n\n- Decision");
    }

    [Fact]
    public async Task Teams_publisher_rejects_an_inactive_studio()
    {
        var publisher = new MeetingNotesTeamsPublisher(
            new ConfigurationStub(),
            new TeamsConfigurationStub([
                new MeetingNotesTeamsChannelStatus("studio-a", true)
            ], new Uri("https://prod-01.westus.logic.azure.com/test")),
            new StudioDirectoryStub([
                new StudioDetails { Id = "studio-a", StudioName = "Studio A", IsActive = false }
            ]),
            new HttpClient(new RecordingHandler()),
            NullLogger<MeetingNotesTeamsPublisher>.Instance);

        var action = () => publisher.PublishAsync("studio-a", "# Notes", "Test User");

        await action.Should().ThrowAsync<ArgumentException>().WithMessage("*not active*");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private sealed class ConfigurationStub : IAiConfigurationService
    {
        public Task<AiConfigurationSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiConfigurationSnapshot
            {
                Chat = new AiChatOptions
                {
                    Enabled = true,
                    MeetingNotes = new AiMeetingNotesOptions { Enabled = true }
                }
            });

        public Task SaveAsync(SaveAiConfigurationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetCredentialAsync(
            AiProviderSettings settings,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
    }

    private sealed class InstructionConfigurationStub(AiMeetingNotesOptions options) : IAiConfigurationService
    {
        public Task<AiConfigurationSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AiConfigurationSnapshot
            {
                Chat = new AiChatOptions { Enabled = true, MeetingNotes = options }
            });

        public Task SaveAsync(SaveAiConfigurationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetCredentialAsync(
            AiProviderSettings settings,
            CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
    }

    private sealed class WebHostEnvironmentStub(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "TeamHub.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TeamsConfigurationStub(
        IReadOnlyList<MeetingNotesTeamsChannelStatus> channels,
        Uri webhook) : IMeetingNotesTeamsConfigurationService
    {
        public Task<IReadOnlyList<MeetingNotesTeamsChannelStatus>> ListAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(channels);

        public Task SaveAsync(
            IReadOnlyList<SaveMeetingNotesTeamsChannel> channels,
            string updatedBy,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Uri?> GetWebhookAsync(string studioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Uri?>(webhook);
    }

    private sealed class StudioDirectoryStub(IReadOnlyList<StudioDetails> studios) : IStudioDirectoryService
    {
        public Task<IReadOnlyList<StudioDetails>> GetStudiosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(studios);

        public Task<StudioDetails?> GetStudioAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(studios.SingleOrDefault(studio =>
                string.Equals(studio.Id, id, StringComparison.OrdinalIgnoreCase)));

        public Task<StudioDetails> SaveStudioAsync(StudioDetails studio, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteStudioAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static WorkflowDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
