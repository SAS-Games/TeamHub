using Microsoft.Extensions.Options;
using TeamHub.AI.FlowDesigner.Contracts;

namespace TeamHub.AI.FlowDesigner.SourceDocuments;

internal sealed record PreparedFlowDesignerInput(
    string Prompt,
    IReadOnlyList<FlowDesignerSourceDocument> SourceDocuments);

internal sealed class FlowDesignerInputProcessor(IOptions<AiFlowDesignerOptions> options)
{
    private static readonly HashSet<string> SupportedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "text/markdown",
        "text/x-markdown"
    };

    public PreparedFlowDesignerInput Prepare(CreateFlowDiagramDraftRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.Value;
        var prompt = request.Prompt?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
        {
            throw new FlowDiagramGenerationException("A diagram instruction is required.");
        }

        if (prompt.Length > settings.MaximumPromptCharacters)
        {
            throw new FlowDiagramGenerationException(
                $"The diagram instruction exceeds the {settings.MaximumPromptCharacters:N0}-character limit.");
        }

        var documents = request.SourceDocuments
            ?? throw new FlowDiagramGenerationException("At least one source document is required.");
        if (documents.Count == 0)
        {
            throw new FlowDiagramGenerationException("At least one source document is required.");
        }

        if (documents.Count > settings.MaximumSourceDocuments)
        {
            throw new FlowDiagramGenerationException(
                $"No more than {settings.MaximumSourceDocuments} source documents can be used in one generation.");
        }

        var prepared = new List<FlowDesignerSourceDocument>(documents.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var characterCount = 0;
        foreach (var document in documents)
        {
            if (document is null)
            {
                throw new FlowDiagramGenerationException("A source document entry is missing.");
            }

            var id = document.Id?.Trim() ?? string.Empty;
            var title = document.Title?.Trim() ?? string.Empty;
            var mediaType = NormalizeMediaType(document.MediaType);
            var content = NormalizeContent(document.Content);

            if (id.Length == 0 || id.Length > 100)
            {
                throw new FlowDiagramGenerationException("Every source document needs an ID of 1 to 100 characters.");
            }

            if (!ids.Add(id))
            {
                throw new FlowDiagramGenerationException($"Source document ID '{id}' is duplicated.");
            }

            if (title.Length == 0 || title.Length > 200)
            {
                throw new FlowDiagramGenerationException(
                    $"Source document '{id}' needs a title of 1 to 200 characters.");
            }

            if (!SupportedMediaTypes.Contains(mediaType))
            {
                throw new FlowDiagramGenerationException(
                    $"Source document '{id}' uses unsupported media type '{mediaType}'. Only plain text and Markdown are currently supported.");
            }

            if (content.Length == 0)
            {
                throw new FlowDiagramGenerationException($"Source document '{id}' is empty.");
            }

            characterCount += content.Length;
            if (characterCount > settings.MaximumSourceCharacters)
            {
                throw new FlowDiagramGenerationException(
                    $"The selected sources exceed the combined {settings.MaximumSourceCharacters:N0}-character limit.");
            }

            prepared.Add(document with
            {
                Id = id,
                Title = title,
                MediaType = mediaType,
                Content = content,
                SourceReference = string.IsNullOrWhiteSpace(document.SourceReference)
                    ? null
                    : document.SourceReference.Trim()
            });
        }

        return new PreparedFlowDesignerInput(prompt, prepared);
    }

    private static string NormalizeMediaType(string? mediaType) =>
        (mediaType ?? string.Empty).Split(';', 2)[0].Trim().ToLowerInvariant();

    private static string NormalizeContent(string? content) =>
        (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
}