using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeamHub.AI.Contracts;
using TeamHub.Infrastructure.Persistence;
using TeamHub.Infrastructure.Services;

namespace TeamHub.Tests;

public sealed class AiConfigurationTests
{
    [Fact]
    public async Task Saved_settings_override_bootstrap_defaults_and_encrypt_the_credential()
    {
        await using var context = CreateDbContext();
        using var options = CreateOptions();
        var service = new AiConfigurationService(
            context,
            new EphemeralDataProtectionProvider(),
            options.GetRequiredService<IOptionsMonitor<AiRuntimeOptions>>(),
            options.GetRequiredService<IOptionsMonitor<AiFlowDesignerOptions>>(),
            options.GetRequiredService<IOptionsMonitor<AiReportGeneratorOptions>>());

        await service.SaveAsync(new SaveAiConfigurationRequest(
            new AiRuntimeOptions
            {
                Provider = AiModelProviderKeys.LlamaCpp,
                Providers = new Dictionary<string, AiProviderProfileOptions>
                {
                    [AiModelProviderKeys.LlamaCpp] = new()
                    {
                        Endpoint = "http://127.0.0.1:8080",
                        Model = "configured-model"
                    }
                },
                OfflineOnly = true,
                Temperature = 0.2,
                TimeoutSeconds = 90,
                CredentialEnvironmentVariable = "AI_TEST_KEY"
            },
            new AiFlowDesignerOptions { Enabled = true },
            new AiReportGeneratorOptions { Enabled = false },
            "super-secret",
            false,
            "admin@example.com"));

        context.ChangeTracker.Clear();
        var stored = await context.AiConfiguration.SingleAsync();
        stored.SettingsJson.Should().Contain("configured-model");
        stored.SettingsJson.Should().NotContain("super-secret");
        stored.CredentialProtected.Should().NotBeNullOrWhiteSpace().And.NotContain("super-secret");

        var current = await service.GetAsync();
        current.Runtime.Provider.Should().Be(AiModelProviderKeys.LlamaCpp);
        current.FlowDesigner.Enabled.Should().BeTrue();
        current.HasCredential.Should().BeTrue();
        current.UpdatedBy.Should().Be("admin@example.com");
        var credential = await service.GetCredentialAsync(current.Runtime.ToProviderSettings());
        credential.Should().Be("super-secret");
    }

    [Fact]
    public async Task Database_initializer_adds_ai_configuration_table_to_an_existing_database()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var context = new WorkflowDbContext(options);
        await context.Database.OpenConnectionAsync();

        await new WorkflowDatabaseInitializer(context).InitializeAsync();

        var tables = await context.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = 'AiConfiguration'")
            .ToListAsync();
        tables.Should().ContainSingle().Which.Should().Be("AiConfiguration");
    }

    private static WorkflowDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ServiceProvider CreateOptions()
    {
        var services = new ServiceCollection();
        services.AddOptions<AiRuntimeOptions>().Configure(options =>
        {
            options.Provider = AiModelProviderKeys.Ollama;
            options.Endpoint = "http://127.0.0.1:11434";
            options.Model = "bootstrap-model";
        });
        services.AddOptions<AiFlowDesignerOptions>();
        services.AddOptions<AiReportGeneratorOptions>();
        return services.BuildServiceProvider();
    }
}
