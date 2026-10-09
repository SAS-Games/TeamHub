using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TeamHub.AI.Contracts;
using TeamHub.AI.ReportGenerator.Contracts;
using TeamHub.AI.ReportGenerator.Persistence;

namespace TeamHub.AI.ReportGenerator.Application;

internal sealed class ReportGenerationGate(IOptions<ReportGeneratorOptions> options) : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(
        options.Value.MaximumConcurrentRequests,
        options.Value.MaximumConcurrentRequests);

    public void Dispose() => Semaphore.Dispose();
}

internal sealed partial class WeeklyWorkSummaryWorkflow(
    IAiModelService modelService,
    IAiReportRunRepository runs,
    ReportGenerationGate gate,
    IOptions<ReportGeneratorOptions> options) : IWeeklyWorkSummaryWorkflow
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "hpgdsSupport", "notes" },
        properties = new
        {
            hpgdsSupport = new { type = "string" },
            notes = new { type = "string" }
        }
    });

    public async Task<WeeklyWorkSummaryDraft> GenerateAsync(
        WeeklyWorkSummaryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
            throw new WeeklyWorkSummaryException("AI report generation is disabled.");
        if (request.WeekStart.DayOfWeek != DayOfWeek.Monday
            || request.WeekEnd != request.WeekStart.AddDays(4))
            throw new WeeklyWorkSummaryException("Select a Monday-Friday weekly period.");
        if (string.IsNullOrWhiteSpace(request.RequestedBy) || request.Sources.Count == 0)
            throw new WeeklyWorkSummaryException("No weekly support activities are available to summarize.");
        if (request.Sources.Select(source => source.StudioId).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != request.Sources.Count)
            throw new WeeklyWorkSummaryException("The weekly source contains duplicate studios.");
        foreach (var source in request.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.StudioId) || string.IsNullOrWhiteSpace(source.StudioName)
                || string.IsNullOrWhiteSpace(source.HpgdsSupport + source.Notes))
                throw new WeeklyWorkSummaryException("The weekly source contains an incomplete studio record.");
            if (source.HpgdsSupport.Length + source.Notes.Length > options.Value.MaximumSourceCharactersPerStudio)
                throw new WeeklyWorkSummaryException(
                    $"The support activity for {source.StudioName} exceeds the configured AI source limit. No content was truncated.");
        }

        if (!await gate.Semaphore.WaitAsync(0, cancellationToken))
            throw new WeeklyWorkSummaryException("AI report generation is busy. Please try again after the current report finishes.");
        try
        {
            var prompt = await WeeklyWorkSummaryPrompt.LoadAsync(options, cancellationToken);
            var health = await modelService.CheckHealthAsync(cancellationToken);
            if (!health.IsAvailable || !health.IsModelAvailable)
                throw new AiModelProviderException(
                    $"AI provider '{health.Provider}' or model '{health.Model}' is unavailable. {health.Message}");

            var rows = new List<WeeklyWorkSummaryRow>();
            string provider = health.Provider, model = health.Model;
            foreach (var source in request.Sources)
            {
                var generation = await modelService.GenerateStructuredAsync(
                    new AiStructuredGenerationRequest(
                        prompt.Instructions,
                        JsonSerializer.Serialize(new { hpgdsSupport = source.HpgdsSupport, notes = source.Notes }),
                        Schema,
                        options.Value.MaximumOutputTokens),
                    cancellationToken);
                var activity = ParseAndValidate(generation.Content, source);
                rows.Add(new WeeklyWorkSummaryRow(source.StudioName, activity));
                provider = generation.Provider;
                model = generation.Model;
            }

            var now = DateTimeOffset.UtcNow;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(request.Sources))));
            await runs.AddAsync(new AiReportRunSnapshot(
                Guid.NewGuid(), "WeeklyWorkSummary", request.WeekStart, request.WeekEnd,
                "All active studios", AiReportRunStatus.Completed, provider, model, prompt.Version,
                hash, request.RequestedBy, now, now, null), cancellationToken);
            return new WeeklyWorkSummaryDraft(rows, provider, model);
        }
        finally
        {
            gate.Semaphore.Release();
        }
    }

    private static string ParseAndValidate(string content, WeeklyWorkSummarySource source)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 2)
                throw new JsonException();
            var support = ValidateField(root, "hpgdsSupport", source.HpgdsSupport, source.StudioName);
            var notes = ValidateField(root, "notes", source.Notes, source.StudioName);
            return string.Join("\n", new[] { support, notes }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch (JsonException)
        {
            throw new WeeklyWorkSummaryException(
                $"The AI response for {source.StudioName} did not match the required summary format. No draft was accepted.");
        }
    }

    private static string ValidateField(JsonElement root, string field, string source, string studioName)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException();
        var activity = value.GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source) != string.IsNullOrWhiteSpace(activity))
            throw new WeeklyWorkSummaryException(
                $"The AI omitted or invented content in {field} for {studioName}. No draft was accepted.");
        var original = ProtectedValues().Matches(source)
            .Select(match => match.Value.TrimEnd('.', ',', ';')).ToHashSet(StringComparer.Ordinal);
        var generated = ProtectedValues().Matches(activity)
            .Select(match => match.Value.TrimEnd('.', ',', ';')).ToHashSet(StringComparer.Ordinal);
        if (!original.SetEquals(generated))
            throw new WeeklyWorkSummaryException(
                $"The AI changed a protected number, URL, ticket ID or acronym for {studioName}. No draft was accepted.");
        return activity;
    }

    [GeneratedRegex(@"https?://[^\s<>""\\)\]]+|\b[A-Za-z][A-Za-z0-9._+-]*\d[A-Za-z0-9._+-]*\b|\b\d+(?:[./:%-]\d+)*(?:%)?|\b[A-Z]{2,}\b",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ProtectedValues();
}
