using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TeamHub.AI.Chat.Contracts;
using TeamHub.AI.Contracts;
using TeamHub.Web.AccessControl;

namespace TeamHub.Web.Pages;

[Authorize]
public sealed class AIChatModel(
    IAiChatService chat,
    ICurrentAccessService access,
    IAiConfigurationService configuration) : PageModel
{
    public IReadOnlyList<AiModelCatalogItem> Models { get; private set; } = [];
    public string? LoadError { get; private set; }
    public string ActiveProvider { get; private set; } = string.Empty;
    public bool IsChatEnabled { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var runtime = (await configuration.GetAsync(cancellationToken)).Runtime;
        ActiveProvider = runtime.Provider;
        IsChatEnabled = runtime.ChatEnabled;
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

            var response = await chat.SendAsync(
                new AiChatUserContext(
                    current.User.UserId,
                    current.User.DisplayName,
                    current.User.UserType),
                new AiChatRequest(mode, input.Model, input.Messages, input.Documents),
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
        public List<AiChatDocument> Documents { get; set; } = [];
    }
}
