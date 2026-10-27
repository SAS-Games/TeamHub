using TeamHub.AI.Chat.Contracts;

namespace TeamHub.Web.AI;

/// <summary>
/// Loads the application-owned TeamHub AI playbook. The file is deliberately
/// separate from user documents and retrieved page content so only trusted
/// deployment content can influence routing instructions.
/// </summary>
public sealed class TeamHubAiInstructionFileProvider(
    IWebHostEnvironment environment,
    ILogger<TeamHubAiInstructionFileProvider> logger) : IAiChatInstructionProvider
{
    private readonly string path = Path.Combine(environment.ContentRootPath, "AI", "TeamHubAI.md");

    public async Task<string> GetTeamHubInstructionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("TeamHub AI playbook was not found at {Path}; core chat rules will be used.", path);
                return string.Empty;
            }

            var content = await File.ReadAllTextAsync(path, cancellationToken);
            return content.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "TeamHub AI playbook at {Path} could not be read; core chat rules will be used.", path);
            return string.Empty;
        }
    }
}
