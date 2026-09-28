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

namespace TeamHub.FlowDesigner.Web.Pages.Flows;

public sealed class CreateWithAiModel(
    IFlowDiagramGenerationWorkflow generationWorkflow,
    IFlowHierarchyService hierarchies,
    IFlowSerializer serializer,
    IFlowPermissionService permissions,
    IOptions<AiFlowDesignerOptions> options,
    IDataProtectionProvider dataProtection) : PageModel
{
    private readonly IDataProtector draftProtector =
        dataProtection.CreateProtector("TeamHub.AI.FlowDesigner.DraftPreview.v2");

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
            ProtectedDraft = draftProtector.Protect(serializer.SerializeBundle(Preview.Hierarchy));
            Sources = selectedSources;
        }
        catch (FlowDiagramGenerationException exception)
        {
            ModelState.AddModelError(
                string.Empty,
                $"The model returned a diagram that Team Hub could not use. {exception.Message} " +
                "Try again with clearer source content or select a larger local model.");
        }
        catch (AiModelProviderException exception)
        {
            ModelState.AddModelError(
                string.Empty,
                $"The configured AI model could not generate the diagram. {exception.Message} " +
                "Confirm that the local model server is running and that its model alias matches Team Hub configuration.");
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

        try
        {
            var proposal = serializer.DeserializeBundle(draftProtector.Unprotect(ProtectedDraft));
            var created = await hierarchies.CreateDraftAsync(proposal, cancellationToken);
            TempData["FlowMessage"] = created.Flows.Count == 1
                ? "AI proposal created as an editable Flow Designer draft."
                : $"AI proposal created as an editable hierarchy with {created.Flows.Count} diagrams.";
            return RedirectToPage("/Flows/Edit", new { id = created.RootFlowId });
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            ModelState.AddModelError(string.Empty, "The AI preview is invalid or has expired. Generate it again.");
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }
}

public sealed class AiFlowSourceInput
{
    public string Title { get; set; } = string.Empty;
    public string MediaType { get; set; } = "text/markdown";
    public string Content { get; set; } = string.Empty;
}
