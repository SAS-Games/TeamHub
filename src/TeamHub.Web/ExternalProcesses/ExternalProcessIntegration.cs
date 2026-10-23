using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TeamHub.Studio;

namespace TeamHub.Web.ExternalProcesses;

public static class ExternalProcessKeys
{
    public const string WorklogAnalytics = "WorklogAnalytics";
    public const string MilestoneReviewReport = "MilestoneReviewReport";
}

public sealed class ExternalProcessCatalogOptions
{
    public const string SectionName = "ExternalProcesses";

    public Dictionary<string, ExternalProcessOptions> Processes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExternalProcessOptions
{
    public bool Enabled { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string EntryPointPath { get; set; } = string.Empty;
    public string EntryPointBase { get; set; } = "RootPath";
    public List<string> Arguments { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 120;
    public int MaximumOutputCharacters { get; set; } = 5_000_000;
    public ExternalProcessCredentialOptions Credential { get; set; } = new();
}

public sealed class ExternalProcessCredentialOptions
{
    public string Mode { get; set; } = ExternalProcessCredentialModes.None;
    public string ConfiguredUserId { get; set; } = string.Empty;
    public string EnvironmentVariable { get; set; } = "TEAMHUB_JIRA_PAT";
    public bool AllowDefaultFallbackForPrivileged { get; set; }
}

public static class ExternalProcessCredentialModes
{
    public const string None = "None";
    public const string RequestingUser = "RequestingUser";
    public const string ConfiguredUser = "ConfiguredUser";
    public const string DefaultShared = "DefaultShared";
}

public sealed record ExternalProcessRequest(
    string ProcessKey,
    string? StandardInput = null,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string>? SensitiveEnvironment = null);

public sealed record ExternalProcessResult(string StandardOutput, string StandardError, int ExitCode);

public sealed class ExternalProcessException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public interface IExternalProcessRunner
{
    bool IsEnabled(string processKey);
    Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request,
        CancellationToken cancellationToken = default);
}

public interface IExternalProcessCredentialResolver
{
    Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(
        string processKey,
        string requestingUserId,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken = default);
}

internal sealed partial class ExternalProcessRunner(
    IOptionsMonitor<ExternalProcessCatalogOptions> options,
    IWebHostEnvironment environment,
    ILogger<ExternalProcessRunner> logger) : IExternalProcessRunner
{
    public bool IsEnabled(string processKey) =>
        TryGetOptions(processKey, out var settings) && settings.Enabled;

    public async Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ProcessKey().IsMatch(request.ProcessKey))
            throw new ExternalProcessException("The external process key is invalid.");
        if (!TryGetOptions(request.ProcessKey, out var settings) || !settings.Enabled)
            throw new ExternalProcessException($"External process '{request.ProcessKey}' is not enabled.");

        var rootPath = RequiredDirectory(settings.RootPath, request.ProcessKey);
        var executablePath = ResolvePath(settings.ExecutablePath, rootPath);
        if (!File.Exists(executablePath))
            throw new ExternalProcessException($"The executable configured for '{request.ProcessKey}' was not found.");

        string? entryPointPath = null;
        if (!string.IsNullOrWhiteSpace(settings.EntryPointPath))
        {
            var entryPointRoot = settings.EntryPointBase.Equals("ContentRoot", StringComparison.OrdinalIgnoreCase)
                ? environment.ContentRootPath
                : rootPath;
            entryPointPath = ResolvePath(settings.EntryPointPath, entryPointRoot);
            if (!File.Exists(entryPointPath))
                throw new ExternalProcessException($"The entry point configured for '{request.ProcessKey}' was not found.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = rootPath,
            RedirectStandardInput = request.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (entryPointPath is not null) startInfo.ArgumentList.Add(entryPointPath);
        foreach (var argument in settings.Arguments ?? [])
            startInfo.ArgumentList.Add(Expand(argument, rootPath, entryPointPath));
        foreach (var argument in request.Arguments ?? [])
            startInfo.ArgumentList.Add(argument);
        foreach (var item in request.SensitiveEnvironment ?? new Dictionary<string, string>())
        {
            if (!EnvironmentVariable().IsMatch(item.Key))
                throw new ExternalProcessException("An external process environment variable name is invalid.");
            startInfo.Environment[item.Key] = item.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        var started = false;
        try
        {
            if (!process.Start())
                throw new ExternalProcessException($"External process '{request.ProcessKey}' could not be started.");
            started = true;
            logger.LogInformation("Started external process {ProcessKey} with process ID {ProcessId}.",
                request.ProcessKey, process.Id);

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (request.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                process.StandardInput.Close();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 3600)));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new ExternalProcessException($"External process '{request.ProcessKey}' timed out.");
            }

            var output = await outputTask;
            var error = await errorTask;
            var maximumOutput = Math.Clamp(settings.MaximumOutputCharacters, 1_000, 20_000_000);
            if (output.Length > maximumOutput || error.Length > maximumOutput)
                throw new ExternalProcessException($"External process '{request.ProcessKey}' exceeded its output limit.");
            if (process.ExitCode != 0)
            {
                var detail = Redact(LastNonEmptyLine(error), request.SensitiveEnvironment?.Values);
                throw new ExternalProcessException(string.IsNullOrWhiteSpace(detail)
                    ? $"External process '{request.ProcessKey}' failed."
                    : detail);
            }

            logger.LogInformation("External process {ProcessKey} completed successfully.", request.ProcessKey);
            return new(output, error, process.ExitCode);
        }
        catch (ExternalProcessException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new ExternalProcessException(
                $"External process '{request.ProcessKey}' could not be executed.", exception);
        }
        finally
        {
            try
            {
                if (started && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the state check and cleanup.
            }
        }
    }

    private bool TryGetOptions(string key, out ExternalProcessOptions settings)
    {
        settings = options.CurrentValue.Processes
            .FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
        return settings is not null;
    }

    private static string RequiredDirectory(string path, string key)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ExternalProcessException($"The root directory for external process '{key}' is not configured.");
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new ExternalProcessException($"The root directory configured for '{key}' was not found.");
        return fullPath;
    }

    private static string ResolvePath(string path, string basePath)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(basePath, path));
    }

    private string Expand(string argument, string rootPath, string? entryPointPath) => argument
        .Replace("{RootPath}", rootPath, StringComparison.Ordinal)
        .Replace("{ContentRoot}", environment.ContentRootPath, StringComparison.Ordinal)
        .Replace("{EntryPoint}", entryPointPath ?? string.Empty, StringComparison.Ordinal);

    private static string LastNonEmptyLine(string value) =>
        value.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

    private static string Redact(string value, IEnumerable<string>? secrets)
    {
        foreach (var secret in secrets ?? [])
        {
            if (!string.IsNullOrEmpty(secret))
                value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }
        return value;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessKey();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariable();
}

internal sealed partial class ExternalProcessCredentialResolver(
    IOptionsMonitor<ExternalProcessCatalogOptions> options,
    IAtlassianCredentialAccessor credentials) : IExternalProcessCredentialResolver
{
    public async Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(
        string processKey,
        string requestingUserId,
        bool allowPrivilegedDefaultCredential,
        CancellationToken cancellationToken = default)
    {
        var settings = options.CurrentValue.Processes
            .FirstOrDefault(item => item.Key.Equals(processKey, StringComparison.OrdinalIgnoreCase)).Value
            ?? throw new ExternalProcessException($"External process '{processKey}' is not configured.");
        var credential = settings.Credential ?? new();
        if (credential.Mode.Equals(ExternalProcessCredentialModes.None, StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, string>();
        if (!EnvironmentVariable().IsMatch(credential.EnvironmentVariable))
            throw new ExternalProcessException($"The credential environment variable for '{processKey}' is invalid.");

        AtlassianResolvedCredential? resolved;
        if (credential.Mode.Equals(ExternalProcessCredentialModes.RequestingUser, StringComparison.OrdinalIgnoreCase))
        {
            resolved = await credentials.ResolveJiraCredentialAsync(
                requestingUserId,
                credential.AllowDefaultFallbackForPrivileged && allowPrivilegedDefaultCredential,
                cancellationToken);
        }
        else if (credential.Mode.Equals(ExternalProcessCredentialModes.ConfiguredUser, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(credential.ConfiguredUserId))
                throw new ExternalProcessException($"A credential user is not configured for '{processKey}'.");
            resolved = await credentials.ResolveJiraCredentialAsync(
                credential.ConfiguredUserId, false, cancellationToken);
        }
        else if (credential.Mode.Equals(ExternalProcessCredentialModes.DefaultShared, StringComparison.OrdinalIgnoreCase))
        {
            resolved = await credentials.ResolveDefaultJiraCredentialAsync(cancellationToken);
        }
        else
        {
            throw new ExternalProcessException($"The credential mode for '{processKey}' is invalid.");
        }

        if (resolved is null)
            throw new ExternalProcessException($"No Jira bearer credential is available for '{processKey}'.");
        return new Dictionary<string, string> { [credential.EnvironmentVariable] = resolved.Token };
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariable();
}
