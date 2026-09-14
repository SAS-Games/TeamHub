# Team Hub AI Architecture

## Purpose

Team Hub has one reusable AI runtime and separate feature modules that consume it. Model-provider integration must not be implemented independently inside reporting, Flow Designer, or future AI-assisted features.

## Module boundaries

```text
TeamHub.Web
├── TeamHub.AI                     shared provider/model runtime
├── TeamHub.AIReports              report-specific generation and persistence
└── TeamHub.FlowDesigner.Agent     future source-document-to-diagram workflow
    └── TeamHub.FlowDesigner.Core  canonical diagram models and validation

TeamHub.AIReports ────────────────> TeamHub.AI
TeamHub.FlowDesigner.Agent ───────> TeamHub.AI
TeamHub.FlowDesigner.Agent ───────> TeamHub.FlowDesigner.Core
```

### `TeamHub.AI`

Owns only reusable inference infrastructure:

- Provider-neutral `IAiModelService` and `IAiModelProvider` contracts.
- Provider registry and configuration-based provider selection.
- Ollama and generic OpenAI-compatible adapters.
- Model health checks and structured JSON-schema generation.
- Timeout, cancellation, protected credential access, and sanitized provider failures.

It does not understand reports, studios, diagrams, nodes, or publishing.

### `TeamHub.AIReports`

Owns report-specific behavior and data:

- Weekly and monthly report source models and orchestration.
- Meaning-preserving report prompts and validation.
- Four-column report results and export.
- Report audit metadata in `ai-reports.db`.
- Its own feature flag, permissions, and concurrency policy.

### `TeamHub.FlowDesigner.Agent`

Will own document-to-diagram behavior:

- User prompt and explicitly selected source-of-truth documents.
- Document adapters and normalized evidence locations.
- Diagram-specific prompt/schema design.
- Conversion into canonical Flow Designer models.
- Evidence, structural, connection, and child-diagram validation.
- Preview and confirmed creation of an editable draft.

It will never publish automatically. The existing Flow Designer publication workflow and permissions remain authoritative.

## Configuration ownership

Shared runtime configuration:

```text
AI__Provider=Ollama
AI__Endpoint=http://127.0.0.1:11434
AI__Model=qwen3:8b
AI__Temperature=0
AI__TimeoutSeconds=180
AI__CredentialEnvironmentVariable=TEAMHUB_AI_PROVIDER_API_KEY
```

Consumer configuration is separate:

```text
AIReports__Enabled=false
AIReports__MaximumConcurrentRequests=1

# Reserved for the future implementation; not bound or registered yet.
FlowDesignerAgent__Enabled=false
FlowDesignerAgent__MaximumSourceDocuments=10
FlowDesignerAgent__MaximumSourceBytes=10485760
```

Changing between registered providers or models is configuration-only. A provider with an incompatible API requires one new adapter in `TeamHub.AI`; consumer modules must remain unchanged.

## Rules for every AI consumer

- Depend on `IAiModelService`, never a vendor client or Ollama request type.
- Own a separate feature flag, permissions, prompts, schemas, validation, and persistence.
- Snapshot provider, model, prompt version, source versions, and validation results for auditability.
- Treat source content as untrusted data, not as executable instructions.
- Require deterministic schema and domain validation before saving results.
- Keep human review between generated output and consequential actions such as publishing.
- Do not place provider credentials, complete source documents, or sensitive generated content in ordinary logs.

## Current readiness

- Shared runtime extracted and registered by Team Hub Web.
- AI Reports consumes the shared project and retains its separate database.
- Flow Designer Agent project, dependency direction, public draft contract, and folder boundaries exist.
- Flow Designer Agent implementation and host registration have intentionally not started.
