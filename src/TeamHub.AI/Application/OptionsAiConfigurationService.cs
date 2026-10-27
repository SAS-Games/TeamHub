using Microsoft.Extensions.Options;
using TeamHub.AI.Contracts;

namespace TeamHub.AI.Application;

internal sealed class OptionsAiConfigurationService(
    IOptionsMonitor<AiRuntimeOptions> runtimeOptions,
    IOptionsMonitor<AiChatOptions> chatOptions,
    IOptionsMonitor<AiFlowDesignerOptions> flowDesignerOptions,
    IOptionsMonitor<AiReportGeneratorOptions> reportGeneratorOptions) : IAiConfigurationService
{
    public Task<AiConfigurationSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = runtimeOptions.CurrentValue;
        var chat = chatOptions.CurrentValue;
#pragma warning disable CS0618
        if (!chat.Enabled && runtime.ChatEnabled) chat.Enabled = true;
#pragma warning restore CS0618
        var credentialName = runtime.GetSelectedCredentialEnvironmentVariable();
        return Task.FromResult(new AiConfigurationSnapshot
        {
            Runtime = runtime,
            Chat = chat,
            FlowDesigner = flowDesignerOptions.CurrentValue,
            ReportGenerator = reportGeneratorOptions.CurrentValue,
            HasCredential = !string.IsNullOrWhiteSpace(credentialName)
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(credentialName))
        });
    }

    public Task SaveAsync(SaveAiConfigurationRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The active AI configuration source is read-only.");

    public ValueTask<string?> GetCredentialAsync(
        AiProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(string.IsNullOrWhiteSpace(settings.CredentialEnvironmentVariable)
            ? null
            : Environment.GetEnvironmentVariable(settings.CredentialEnvironmentVariable));
    }
}
