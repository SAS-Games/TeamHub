using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.AI.Chat.Contracts;
using TeamHub.AI.Contracts;
using TeamHub.Authentication;
using TeamHub.Web.AccessControl;
using TeamHub.Web.AI;

namespace TeamHub.Web.Pages;

[Authorize]
public sealed class AIChatModel(
    IAiChatService chat,
    ICurrentAccessService access,
    IAiConfigurationService configuration,
    IAiChatDocumentExtractor documentExtractor) : PageModel
{
    public IReadOnlyList<AiModelCatalogItem> Models { get; private set; } = [];
    public string? LoadError { get; private set; }
    public string ActiveProvider { get; private set; } = string.Empty;
    public bool IsChatEnabled { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var snapshot = await configuration.GetAsync(cancellationToken);
        ActiveProvider = snapshot.Runtime.Provider;
        IsChatEnabled = snapshot.Chat.Enabled;
        try
        {
            Models = await chat.GetModelsAsync(cancellationToken);
        }
        catch (AiChatException exception)
        {
            LoadError = exception.Message;
        }
    }

    public async Task<IActionResult> OnGetModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var runtime = (await configuration.GetAsync(cancellationToken)).Runtime;
            var models = await chat.GetModelsAsync(cancellationToken);
            return new JsonResult(new
            {
                provider = runtime.Provider,
                models = models.Select(model => new
                {
                    id = model.Id,
                    costTier = model.CostTier
                })
            });
        }
        catch (AiChatException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    public async Task<IActionResult> OnPostMessageAsync(
        [FromBody] ChatInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await access.GetCurrentAsync(cancellationToken);
            if (current.User is null) return Unauthorized();
            if (!Enum.TryParse<AiChatMode>(input.Mode, true, out var mode))
                return BadRequest(new { error = "Select TeamHub or General chat mode." });

            IReadOnlyList<AiChatDocument> documents = [];
            IReadOnlyList<AiImageInput> images = [];
            if (mode == AiChatMode.General && input.Documents.Count > 0)
            {
                var chatSettings = (await configuration.GetAsync(cancellationToken)).Chat;
                var attachments = await documentExtractor.ExtractAsync(
                    input.Documents,
                    chatSettings.MaximumDocuments,
                    chatSettings.MaximumDocumentCharacters,
                    cancellationToken);
                documents = attachments.Documents;
                images = attachments.Images;
            }

            var latestQuestion = input.Messages.LastOrDefault(message =>
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
            var requestedExternalSources = mode == AiChatMode.TeamHub
                && await access.CanAsync(TeamHubModules.SupportSummary, AccessLevel.ReadOnly, cancellationToken)
                    ? SupportSummaryChatKnowledgeSource.GetRequestedExternalSources(latestQuestion)
                    : [];

            if (requestedExternalSources.Count > 0
                && string.IsNullOrWhiteSpace(input.ExternalAccessDecision))
            {
                var sourceNames = string.Join(" and ", requestedExternalSources);
                return new JsonResult(new
                {
                    requiresExternalAccess = true,
                    externalSources = requestedExternalSources,
                    answer = $"To provide more current support details, may TeamHub use your {sourceNames} connection for this question? Your approval applies only to this question."
                });
            }

            var approvedExternalSources = string.Equals(
                input.ExternalAccessDecision,
                "Approved",
                StringComparison.OrdinalIgnoreCase)
                    ? input.ApprovedExternalSources
                        .Where(source => requestedExternalSources.Any(requested =>
                            string.Equals(requested, source, StringComparison.OrdinalIgnoreCase)))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var response = await chat.SendAsync(
                new AiChatUserContext(
                    current.User.UserId,
                    current.User.DisplayName,
                    current.User.UserType,
                    approvedExternalSources),
                new AiChatRequest(mode, input.Model, input.Messages, documents, images),
                cancellationToken);
            return new JsonResult(response);
        }
        catch (AiChatException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "The AI request timed out." });
        }
        catch
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "The AI Gateway request could not be completed." });
        }
    }

    public sealed class ChatInput
    {
        [Required]
        public string Mode { get; set; } = nameof(AiChatMode.TeamHub);

        [Required, StringLength(200)]
        public string Model { get; set; } = string.Empty;

        public List<AiChatMessage> Messages { get; set; } = [];
        public List<AiChatUploadDocument> Documents { get; set; } = [];
        public string? ExternalAccessDecision { get; set; }
        public List<string> ApprovedExternalSources { get; set; } = [];
    }
}
