using TeamHub.AI.Chat.Contracts;
using TeamHub.AI.Contracts;

namespace TeamHub.Web.AI;

public sealed class MeetingNotesInstructionFileProvider(
    IWebHostEnvironment environment,
    IAiConfigurationService configuration,
    ILogger<MeetingNotesInstructionFileProvider> logger) : IAiMeetingNotesInstructionProvider
{
    public async Task<string> GetInstructionsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await configuration.GetAsync(cancellationToken);
        var configuredPath = snapshot.Chat.MeetingNotes.InstructionPath?.Trim() ?? string.Empty;
        if (configuredPath.Length == 0) return string.Empty;

        var contentRoot = Path.GetFullPath(environment.ContentRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(contentRoot, configuredPath));
        if (!path.StartsWith(contentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Meeting Notes instruction path {Path} is outside the application directory.", path);
            return string.Empty;
        }

        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("Meeting Notes instruction file was not found at {Path}.", path);
                return string.Empty;
            }
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Meeting Notes instruction file at {Path} could not be read.", path);
            return string.Empty;
        }
    }
}
