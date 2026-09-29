# Team Hub AI Architecture

## Decision

Team Hub will implement controlled AI workflows, not autonomous agents, for report generation and Flow Designer generation.

The distinction is based on control, not on which model API is used:

- Team Hub code selects the data, calls each step, validates the response, applies bounded retry rules, and decides when execution stops.
- The model generates structured content only; it cannot select tools, search arbitrary systems, save results, publish diagrams, or expand its own permissions.
- Users review generated drafts before any persistent or consequential action.

Both workflows use the same Ollama or OpenAI-compatible structured-generation API through `TeamHub.AI`. There is no separate workflow API or agent API required.

Ollama is the recommended local runtime, not a required dependency. Team Hub can remain fully offline by using Ollama or any compatible local model server on loopback. The application does not download models or contact a hosted AI service by itself.

## Module boundaries

```text
TeamHub.Web
|-- TeamHub.AI                    shared provider/model runtime
|-- TeamHub.AI.ReportGenerator    controlled report-generation workflow
`-- TeamHub.AI.FlowDesigner       controlled document-to-diagram workflow
    `-- TeamHub.FlowDesigner.Core canonical diagram models and validation

TeamHub.AI.ReportGenerator --> TeamHub.AI
TeamHub.AI.FlowDesigner    --> TeamHub.AI
TeamHub.AI.FlowDesigner    --> TeamHub.FlowDesigner.Core
```

### `TeamHub.AI`

Owns reusable inference infrastructure:

- Provider-neutral `IAiModelService` and `IAiModelProvider` contracts.
- Provider registry and configuration-based provider selection.
- Ollama, llama.cpp, and generic OpenAI-compatible adapters.
- Model health checks and structured JSON-schema generation.
- Timeout, cancellation, protected credential access, and sanitized provider failures.

It does not understand reports, studios, diagrams, nodes, or publishing.

### `TeamHub.AI.ReportGenerator`

Owns the controlled report-generation workflow:

- Weekly and monthly report source models and orchestration.
- Meaning-preserving prompts and deterministic validation.
- Bounded correction/retry behavior controlled by application code.
- Four-column report drafts and export.
- Report audit metadata in `ai-reports.db`.
- Its own feature flag, permissions, and concurrency policy.

Its public orchestration boundary is `IReportGenerationWorkflow`.

### `TeamHub.AI.FlowDesigner`

Will own the controlled document-to-diagram workflow:

- User prompt and explicitly selected source-of-truth documents.
- Document adapters and normalized evidence locations.
- Diagram-specific prompt and JSON schema.
- Conversion into canonical Flow Designer models.
- Evidence, structural, connection, and child-diagram validation.
- Preview and confirmed creation of an editable draft.

Its public orchestration boundary is `IFlowDiagramGenerationWorkflow`. It will never publish automatically; the existing Flow Designer publication workflow and permissions remain authoritative.

## How to identify the implementation

The implementation is still a workflow when it has several steps or retries. Check who makes the decisions:

| Question | Our design |
|---|---|
| Who selects the sources? | User and Team Hub code |
| Who determines the next step? | Team Hub code |
| Who chooses which tools may run? | Team Hub code |
| Who defines retry and stopping rules? | Team Hub code |
| Can the model save or publish directly? | No |
| Is the output schema predetermined? | Yes |

If the model were allowed to choose tools, discover its own sources, plan arbitrary next steps, and decide when its objective was complete, that component would be an agent. That is not the current requirement.

## API boundaries

The internal model call is shared by both workflows:

```text
IAiModelService.GenerateStructuredAsync(...)
        |
        +-- Ollama /v1/chat/completions
        `-- another configured OpenAI-compatible endpoint
```

Future Team Hub HTTP endpoints describe business operations, not AI autonomy:

```text
POST /api/ai/reports/generate-draft
POST /api/ai/flows/generate-draft
```

Those endpoints invoke fixed application workflows. We do not need an agent framework, agent SDK, tool-calling loop, or a separate agent service for the current scope.

## Configuration ownership

Shared model runtime:

```text
AI__Provider=Ollama
AI__Providers__Ollama__Endpoint=http://127.0.0.1:11434
AI__Providers__Ollama__Model=qwen3:8b
AI__Providers__LlamaCpp__Endpoint=http://127.0.0.1:8080
AI__Providers__LlamaCpp__Model=qwen3:8b
AI__OfflineOnly=true
AI__Temperature=0
AI__TimeoutSeconds=180
AI__CredentialEnvironmentVariable=TEAMHUB_AI_PROVIDER_API_KEY
```

Feature configuration is separate:

```text
AI__ReportGenerator__Enabled=false
AI__ReportGenerator__MaximumConcurrentRequests=1

# Reserved for future Flow Designer implementation; not bound yet.
AI__FlowDesigner__Enabled=false
AI__FlowDesigner__MaximumSourceDocuments=10
AI__FlowDesigner__MaximumSourceBytes=10485760
```

Changing between registered providers or models is configuration-only. Ollama and llama.cpp retain separate endpoint/model profiles, so switching between them requires changing only `AI__Provider`. The legacy top-level `AI__Endpoint` and `AI__Model` settings remain available for generic OpenAI-compatible runtimes and older deployments. A provider with an incompatible API requires one adapter in `TeamHub.AI`; feature workflows remain unchanged.

`AI__OfflineOnly=true` is the safe default. It rejects any AI endpoint that is not `localhost`, `127.0.0.1`, or another loopback address, preventing accidental prompt transmission to a hosted service. Set it to `false` only as an explicit deployment decision to use an approved remote endpoint.

## Fully offline deployment

Team Hub still needs an inference runtime capable of loading and executing the local model. That runtime can be Ollama, llama.cpp server, LM Studio, Foundry Local, or another server that implements the required OpenAI-compatible model-list, chat-completions, and structured-output behavior.

- With Ollama, use `AI__Provider=Ollama` and the default loopback endpoint.
- With llama.cpp, use `AI__Provider=LlamaCpp`; its default profile uses `http://127.0.0.1:8080` and model alias `qwen3:8b`.
- With another compatible local runtime, use `AI__Provider=OpenAICompatible` and its loopback endpoint.
- No provider credential is required unless the selected local runtime is configured to require one.
- After the runtime and model files are installed, normal generation requires no internet connection.
- Model installation or download is a separate deployment step. For an air-gapped host, transfer an approved runtime installer and model artifact through the organization's offline software-distribution process.

The current architecture uses a separate local inference process; it does not load GGUF or ONNX weights directly inside the Team Hub web process. Keeping inference out of process isolates native model dependencies and allows the local runtime or model to change without changing the report or Flow Designer workflows.

For restricted or air-gapped environments, `scripts/ai/Start-TeamHubLlamaCpp.ps1` starts an organization-approved llama-server binary and GGUF model on loopback. It can verify approved SHA-256 hashes and performs no downloads. `scripts/ai/Test-TeamHubLocalAi.ps1` validates the model-list endpoint and configured model alias. Published Team Hub packages include both scripts under `tools/ai`.

## Rules for every AI workflow

- Depend on `IAiModelService`, never a vendor client or Ollama request type.
- Own separate feature flags, permissions, prompts, schemas, validation, and persistence.
- Snapshot provider, model, prompt version, source versions, and validation results for auditability.
- Treat source content as untrusted data, not executable instructions.
- Require schema and domain validation before saving results.
- Keep human review between generated output and consequential actions.
- Do not place credentials, complete source documents, or sensitive generated content in ordinary logs.

## Current readiness

- Shared runtime is registered by Team Hub Web.
- Report Generator has a workflow contract and retains its separate report database.
- AI Flow Designer has a workflow contract and architectural folder boundaries.
- Neither generation workflow has been implemented yet.
- AI Flow Designer has no host registration, UI, endpoint, database, save behavior, or publication behavior.
