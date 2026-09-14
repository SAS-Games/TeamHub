using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using TeamHub.AI.Contracts;
using TeamHub.AI.FlowDesigner.Contracts;
using TeamHub.FlowDesigner.Core.Contracts;
using TeamHub.FlowDesigner.Core.Models;

namespace TeamHub.FlowDesigner.Web.Pages.Flows;

public sealed class CreateWithAiModel(
    IFlowDiagramGenerationWorkflow generationWorkflow,
    IFlowService flows,
    IFlowSerializer serializer,
    IFlowValidator validator,
    IFlowPermissionService permissions,
    IOptions<AiFlowDesignerOptions> options,
    IDataProtectionProvider dataProtection) : PageModel
{
    private readonly IDataProtector draftProtector =
        dataProtection.CreateProtector("TeamHub.AI.FlowDesigner.DraftPreview.v1");

    [BindProperty]
    [Required, StringLength(4_000)]
    [Display(Name = "What should the diagram explain?")]
    public string Prompt { get; set; } = string.Empty;

    [BindProperty]
    public List<AiFlowSourceInput> Sources { get; set; } = [new()];

    [BindProperty]
    public string ProtectedDraft { get; set; } = string.Empty;

    public FlowDiagramGenerationDraft? Preview { get; private set; }
    public bool IsEnabled => options.Value.Enabled;

    public IActionResult OnGet()
    {
        if (!IsEnabled) return NotFound();
        return permissions.CanCreate() ? Page() : Forbid();
    }

    public async Task<IActionResult> OnPostGenerateAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled) return NotFound();
        if (!permissions.CanCreate()) return Forbid();

        var selectedSources = Sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Title) || !string.IsNullOrWhiteSpace(source.Content))
            .ToList();
        if (selectedSources.Count == 0)
        {
            ModelState.AddModelError(nameof(Sources), "Add at least one source document.");
        }

        for (var index = 0; index < selectedSources.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(selectedSources[index].Title))
            {
                ModelState.AddModelError(nameof(Sources), $"Source {index + 1} needs a title.");
            }
            if (string.IsNullOrWhiteSpace(selectedSources[index].Content))
            {
                ModelState.AddModelError(nameof(Sources), $"Source {index + 1} has no content.");
            }
        }

        if (!ModelState.IsValid) return Page();

        try
        {
            var documents = selectedSources.Select((source, index) => new FlowDesignerSourceDocument(
                $"source-{index + 1}",
                source.Title,
                source.MediaType,
                source.Content)).ToList();
            Preview = await generationWorkflow.CreateDraftAsync(
                new CreateFlowDiagramDraftRequest(Prompt, documents), cancellationToken);
            ProtectedDraft = draftProtector.Protect(serializer.Serialize(Preview.Diagram));
            Sources = selectedSources;
        }
        catch (FlowDiagramGenerationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
        }
        catch (AiModelProviderException exception)
        {
            ModelState.AddModelError(string.Empty, $"The configured AI model could not generate the diagram. {exception.Message}");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled) return NotFound();
        if (!permissions.CanCreate()) return Forbid();
        if (string.IsNullOrWhiteSpace(ProtectedDraft))
        {
            ModelState.AddModelError(string.Empty, "The AI preview has expired. Generate it again.");
            return Page();
        }

        FlowDefinition proposal;
        try
        {
            proposal = serializer.Deserialize(draftProtector.Unprotect(ProtectedDraft));
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            ModelState.AddModelError(string.Empty, "The AI preview is invalid or has expired. Generate it again.");
            return Page();
        }

        if (!permissions.CanUseDiagramType(proposal.DiagramType)) return Forbid();
        var proposalValidation = validator.Validate(proposal);
        if (!proposalValidation.IsValid)
        {
            ModelState.AddModelError(string.Empty, "The AI preview no longer passes Flow Designer validation. Generate it again.");
            return Page();
        }

        FlowDefinition? created = null;
        try
        {
            created = await flows.CreateAsync(
                proposal.Name,
                proposal.Description,
                proposal.DiagramType,
                FlowTemplate.Blank,
                cancellationToken);
            created.Nodes = proposal.Nodes;
            created.Connections = proposal.Connections;
            created.Metadata = proposal.Metadata;
            var saveResult = await flows.SaveAsync(created, cancellationToken);
            if (!saveResult.IsValid)
            {
                await flows.DeleteAsync(created.Id, cancellationToken);
                created = null;
                ModelState.AddModelError(
                    string.Empty,
                    "Flow Designer rejected the generated draft: "
                    + string.Join(" ", saveResult.Issues.Select(issue => issue.Message)));
                return Page();
            }

            TempData["FlowMessage"] = "AI proposal created as an editable Flow Designer draft.";
            return RedirectToPage("/Flows/Edit", new { id = created.Id });
        }
        catch (UnauthorizedAccessException)
        {
            if (created is not null) await TryDeleteAsync(created.Id, cancellationToken);
            return Forbid();
        }
        catch (Exception) when (created is not null)
        {
            await TryDeleteAsync(created.Id, cancellationToken);
            throw;
        }
    }

    private async Task TryDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await flows.DeleteAsync(id, cancellationToken);
        }
        catch
        {
            // Preserve the original failure. Flow Designer administrators can remove the incomplete draft if cleanup fails.
        }
    }
}

public sealed class AiFlowSourceInput
{
    public string Title { get; set; } = string.Empty;
    public string MediaType { get; set; } = "text/markdown";
    public string Content { get; set; } = string.Empty;
}