using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using TeamHub.Web.ExternalProcesses;

namespace TeamHub.Web.ExternalProcesses.WorklogAnalytics;

internal sealed class LegacyWorklogAnalyticsOptions
{
    public bool Enabled { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public string PythonExecutable { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed record WorklogEffortSlice(string Label, double Hours);
public sealed record WorklogEmployeeEffort(string Employee, IReadOnlyList<WorklogEffortSlice> Slices);

public sealed class WorklogEffortReport
{
    public IReadOnlyList<WorklogEffortSlice> EffortSummary { get; init; } = [];
    public IReadOnlyList<WorklogEffortSlice> StudioBreakdown { get; init; } = [];
    public IReadOnlyList<WorklogEmployeeEffort> EmployeeEffortBreakdowns { get; init; } = [];
}

public interface IWorklogEffortService
{
    Task<WorklogEffortReport> GetActualEffortAsync(
        DateOnly startDate,
        DateOnly endDate,
        string requestingUserId,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken = default);
}

internal sealed class PythonWorklogEffortService(
    IExternalProcessRunner processRunner,
    IExternalProcessCredentialResolver credentialResolver,
    IMemoryCache memoryCache) : IWorklogEffortService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<WorklogEffortReport> GetActualEffortAsync(
        DateOnly startDate,
        DateOnly endDate,
        string requestingUserId,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = string.Join(
            '|',
            "worklog-effort-v1",
            requestingUserId.Trim().ToUpperInvariant(),
            allowPrivilegedDefaultCredential,
            startDate.ToString("yyyy-MM-dd"),
            endDate.ToString("yyyy-MM-dd"));
        var cached = await memoryCache.GetOrCreateAsync(
            cacheKey,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                return await LoadActualEffortAsync(
                    startDate,
                    endDate,
                    requestingUserId,
                    allowPrivilegedDefaultCredential,
                    cancellationToken);
            });
        return cached ?? throw new InvalidDataException("Worklog analytics returned an empty effort report.");
    }

    private async Task<WorklogEffortReport> LoadActualEffortAsync(
        DateOnly startDate,
        DateOnly endDate,
        string requestingUserId,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken)
    {
        if (!processRunner.IsEnabled(ExternalProcessKeys.WorklogAnalytics))
            throw new InvalidOperationException("Worklog analytics is not enabled.");
        if (startDate > endDate)
            throw new ArgumentException("The effort report start date must be on or before the end date.");
        if (string.IsNullOrWhiteSpace(requestingUserId))
            throw new InvalidOperationException("Sign in and connect your Jira account to load logged effort.");

        IReadOnlyDictionary<string, string> sensitiveEnvironment;
        try
        {
            sensitiveEnvironment = await credentialResolver.ResolveEnvironmentAsync(
                ExternalProcessKeys.WorklogAnalytics,
                requestingUserId,
                allowPrivilegedDefaultCredential,
                cancellationToken);
        }
        catch (ExternalProcessException exception) when (
            exception.Message.StartsWith("No Jira bearer credential", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(allowPrivilegedDefaultCredential
                ? "No Jira credential is available. Ask an administrator to grant your privileged account read-only Jira access, or add a personal token."
                : "Your Jira token is not connected. Open My Connections and add a Bearer API token.", exception);
        }
        var result = await processRunner.RunAsync(new ExternalProcessRequest(
            ExternalProcessKeys.WorklogAnalytics,
            Arguments:
            [
                "--start", startDate.ToString("yyyy-MM-dd"),
                "--end", endDate.ToString("yyyy-MM-dd")
            ],
            SensitiveEnvironment: sensitiveEnvironment), cancellationToken);

        var report = JsonSerializer.Deserialize<WorklogEffortReport>(result.StandardOutput, SerializerOptions)
            ?? throw new InvalidDataException("Worklog analytics returned an empty effort report.");
        return new WorklogEffortReport
        {
            EffortSummary = Normalize(report.EffortSummary),
            StudioBreakdown = Normalize(report.StudioBreakdown),
            EmployeeEffortBreakdowns = (report.EmployeeEffortBreakdowns ?? [])
                .Where(employee => !string.IsNullOrWhiteSpace(employee.Employee))
                .Select(employee => new WorklogEmployeeEffort(
                    employee.Employee.Trim(),
                    Normalize(employee.Slices)))
                .Where(employee => employee.Slices.Count > 0)
                .OrderBy(employee => employee.Employee, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static IReadOnlyList<WorklogEffortSlice> Normalize(
        IEnumerable<WorklogEffortSlice>? slices) =>
        (slices ?? [])
            .Where(slice => !string.IsNullOrWhiteSpace(slice.Label) && slice.Hours > 0)
            .Select(slice => slice with { Label = slice.Label.Trim() })
            .OrderByDescending(slice => slice.Hours)
            .ThenBy(slice => slice.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
