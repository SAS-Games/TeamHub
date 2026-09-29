using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TeamHub.Web.WorklogAnalytics;

public sealed class WorklogAnalyticsOptions
{
    public const string SectionName = "WorklogAnalytics";

    public bool Enabled { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public string PythonExecutable { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed record WorklogEffortSlice(string Label, double Hours);

public sealed class WorklogEffortReport
{
    public IReadOnlyList<WorklogEffortSlice> EffortSummary { get; init; } = [];
    public IReadOnlyList<WorklogEffortSlice> StudioBreakdown { get; init; } = [];
}

public interface IWorklogEffortService
{
    Task<WorklogEffortReport> GetActualEffortAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);
}

internal sealed class PythonWorklogEffortService(
    IOptions<WorklogAnalyticsOptions> options,
    IWebHostEnvironment environment) : IWorklogEffortService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<WorklogEffortReport> GetActualEffortAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
            throw new InvalidOperationException("Worklog analytics is not enabled.");
        if (startDate > endDate)
            throw new ArgumentException("The effort report start date must be on or before the end date.");

        var rootPath = Path.GetFullPath(settings.RootPath);
        var pythonExecutable = string.IsNullOrWhiteSpace(settings.PythonExecutable)
            ? Path.Combine(rootPath, ".venv", "Scripts", "python.exe")
            : Path.GetFullPath(settings.PythonExecutable);
        var bridgeScript = Path.Combine(
            environment.ContentRootPath,
            "WorklogAnalytics",
            "teamhub_effort_report.py");
        if (!Directory.Exists(rootPath))
            throw new InvalidOperationException("The configured WorklogAnalytics directory was not found.");
        if (!File.Exists(pythonExecutable))
            throw new InvalidOperationException("The configured WorklogAnalytics Python executable was not found.");
        if (!File.Exists(bridgeScript))
            throw new InvalidOperationException("The TeamHub WorklogAnalytics bridge script was not found.");

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            WorkingDirectory = rootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(bridgeScript);
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(rootPath);
        startInfo.ArgumentList.Add("--start");
        startInfo.ArgumentList.Add(startDate.ToString("yyyy-MM-dd"));
        startInfo.ArgumentList.Add("--end");
        startInfo.ArgumentList.Add(endDate.ToString("yyyy-MM-dd"));

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Worklog analytics could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 600)));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new InvalidOperationException("Worklog analytics timed out.");
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(error)
                ? "Worklog analytics failed to produce an effort report."
                : error.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Last();
            throw new InvalidOperationException(message);
        }

        var report = JsonSerializer.Deserialize<WorklogEffortReport>(output, SerializerOptions)
            ?? throw new InvalidDataException("Worklog analytics returned an empty effort report.");
        return new WorklogEffortReport
        {
            EffortSummary = Normalize(report.EffortSummary),
            StudioBreakdown = Normalize(report.StudioBreakdown)
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
