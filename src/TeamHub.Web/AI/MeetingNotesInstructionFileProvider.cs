using TeamHub.AI.Chat.Contracts;
using TeamHub.AI.Contracts;

namespace TeamHub.Web.AI;

public sealed class MeetingNotesInstructionFileProvider(
    IWebHostEnvironment environment,
    IAiConfigurationService configuration,
    ILogger<MeetingNotesInstructionFileProvider> logger) : IAiMeetingNotesInstructionProvider
{
    public async Task<string> GetInstructionsAsync(
        string? studioId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await configuration.GetAsync(cancellationToken);
        var options = snapshot.Chat.MeetingNotes;
        var common = await ReadInstructionFileAsync(options.InstructionPath, "default", cancellationToken);
        var normalizedStudioId = studioId?.Trim() ?? string.Empty;
        if (normalizedStudioId.Length == 0) return common;

        var managedInstruction = (options.StudioInstructions ?? [])
            .FirstOrDefault(item => string.Equals(
                item.Key,
                normalizedStudioId,
                StringComparison.OrdinalIgnoreCase))
            .Value;
        var studio = managedInstruction?.Content?.Trim() ?? string.Empty;

        var studioPath = (options.StudioInstructionPaths ?? [])
            .FirstOrDefault(item => string.Equals(
                item.Key,
                normalizedStudioId,
                StringComparison.OrdinalIgnoreCase))
            .Value;
        if (studio.Length == 0 && !string.IsNullOrWhiteSpace(studioPath))
            studio = await ReadInstructionFileAsync(studioPath, $"studio '{normalizedStudioId}'", cancellationToken);
        if (studio.Length == 0) return common;
        if (common.Length == 0) return studio;
        return $"{common}\n\n# Studio-specific additional instructions\n\n{studio}";
    }

    private async Task<string> ReadInstructionFileAsync(
        string? configuredPathValue,
        string description,
        CancellationToken cancellationToken)
    {
        var configuredPath = configuredPathValue?.Trim() ?? string.Empty;
        if (configuredPath.Length == 0) return string.Empty;

        var contentRoot = Path.GetFullPath(environment.ContentRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(contentRoot, configuredPath));
        if (!path.StartsWith(contentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Meeting Notes {Description} instruction path {Path} is outside the application directory.", description, path);
            return string.Empty;
        }

        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("Meeting Notes {Description} instruction file was not found at {Path}.", description, path);
                return string.Empty;
            }
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Meeting Notes {Description} instruction file at {Path} could not be read.", description, path);
            return string.Empty;
        }
    }
}
