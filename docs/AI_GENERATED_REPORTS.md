# AI-Generated Studio Reports

## Document control

| Field | Value |
|---|---|
| Branch | `feature/ai-generated-reports` |
| Workspace | `D:\TeamHub` |
| Baseline | `main` at `59cfed5` (`confluence page update`) |
| Status | Controlled Report Generator workflow architecture ready; weekly workflow implementation is next |
| Last updated | 2026-09-14 |

This document is the scope contract, design record, and development checklist for the AI-generated reporting feature. Update it whenever a requirement, decision, milestone, test result, or known issue changes.

## Purpose

Create downloadable weekly and monthly studio-support reports from Team Hub's existing consolidated Confluence data. A locally hosted language model may improve clarity, remove repetition, and organize multiline content, but it must not introduce facts or change the source meaning.

The existing Confluence-backed consolidated report remains the source of truth. AI-generated reports are derived, reviewable artifacts and never replace or edit the source report.

## Confirmed product decisions

- The MVP starts with local inference using Qwen3 8B served by Ollama.
- Ollama is the recommended local runtime, not a hard application dependency; another OpenAI-compatible local runtime can be selected through configuration.
- llama.cpp is registered as a first-class local provider for restricted and air-gapped deployments using an approved llama-server binary and GGUF model.
- Offline-only mode is enabled by default and prevents AI requests to non-loopback endpoints.
- The reporting domain and generation workflow are provider-neutral and must not reference Ollama-specific request types.
- Administrators select the provider, endpoint, model, and generation settings through configuration.
- Changing models within the configured provider requires configuration only.
- Changing to any service that implements the supported OpenAI-compatible contract requires configuration only.
- A service with a proprietary, incompatible API requires one provider adapter to be implemented and registered; after that, selecting it requires configuration only.
- External AI services are disabled by default and may be enabled only through an explicit administrator configuration and credential setup.
- AI reports use only active studio projects.
- Reports retain the configured studio grouping, such as IHP and MHP.
- Each visible report table has exactly four columns:
  1. Studio
  2. Studio Work
  3. HPGDS Support
  4. WMD Support
- Action Items and Notes are excluded before model inference.
- Weekly and monthly reports are generated independently from the original structured weekly source data.
- A monthly report must never use an AI-generated weekly report as its input.
- One studio row is processed independently from other studios to prevent content mixing.
- Generated content never overwrites Team Hub Studio data or Confluence data.
- Users review a generated report before downloading it.
- AI report persistence uses a separate database owned by the AI Reports module.

## User experience

### Report builder

The AI Reports page will provide:

- Period type: Weekly or Monthly.
- Weekly date range or calendar month selection.
- Scope: All active studios, studio group, or individual studio.
- Generate AI Report action.
- Selected provider and model availability indicator.
- Generation progress and clear failure messages.
- Source coverage summary, including missing Confluence weeks or studio rows.
- Read-only preview grouped by week/month and studio group.
- Download actions for the supported formats.

### Weekly AI report

- Read the selected weekly consolidated source data.
- Exclude Action Items and Notes before building model requests.
- Process Studio Work, HPGDS Support, and WMD Support for one studio at a time.
- Preserve multiline content as readable paragraphs or bullet points.
- Display separate tables for IHP, MHP, and other configured groups.
- Show the contributing Confluence page title, ID, and version.

### Monthly AI report

- Select a calendar month.
- Identify the source weekly pages assigned to that month.
- Combine each studio's original weekly Studio Work, HPGDS Support, and WMD Support fields.
- Remove exact repetition and improve organization without losing distinct activities or status changes.
- Preserve dates, quantities, ticket IDs, product names, technical terms, and outcomes.
- Process each studio independently.
- Display one table per studio group.
- Show source coverage, such as `4 of 4 weekly pages included`.
- Identify missing or unreadable weekly sources before generation.

### Download

MVP formats:

- Excel (`.xlsx`) as the primary formatted report.
- CSV (`.csv`) as a portable data export.

Later option:

- PDF after the table layout and pagination rules are approved.

Downloads must contain report title, reporting period, generation timestamp, source coverage, group headings, and the four approved columns.

## Meaning-preservation controls

No generative model can guarantee perfect semantic equivalence. The feature therefore uses several independent controls:

- Temperature set to `0`.
- Qwen thinking mode disabled for this copy-editing task.
- A strict system prompt that permits clarity, grammar, formatting, and deduplication changes only.
- One studio and one period per model request.
- Immutable studio ID, group, source week IDs, and source hashes retained outside model-editable text.
- JSON Schema-constrained output with only the three editable content fields.
- Server-side schema validation before accepting output.
- Checks for altered or newly introduced numbers, dates, URLs, Jira IDs, and configured identifiers.
- Rejection of unexpected studios, fields, or empty replacements for non-empty source content.
- Original-versus-generated comparison available during review.
- Visible warning when automated validation cannot establish sufficient confidence.
- Manual user review before download.

The model prompt will explicitly prohibit:

- New facts, recommendations, conclusions, or inferred status.
- Moving information between studios or support categories.
- Changing names, dates, numbers, ticket IDs, technical identifiers, or completion status.
- Converting uncertainty into certainty.
- Removing unique technical details.

## Structured model contract

The model receives one studio record at a time. Identity and grouping are controlled by Team Hub and are not generated by the model.

Illustrative response shape:

```json
{
  "studioWork": "string",
  "hpgdsSupport": "string",
  "wmdSupport": "string"
}
```

Team Hub then combines the immutable studio identity with the validated response to render:

| Studio | Studio Work | HPGDS Support | WMD Support |
|---|---|---|---|

## Module architecture

AI capability is split between a reusable runtime and this report-specific consumer. See `docs/AI_ARCHITECTURE.md` for the cross-feature rules.

```text
src/TeamHub.AI/
├── Contracts/             provider-neutral runtime contracts and configuration
├── Application/           provider registry and model service
├── ModelProviders/        Ollama and generic OpenAI-compatible adapters
└── DependencyInjection/   shared runtime registration

src/TeamHub.AI.ReportGenerator/
├── Contracts/             report requests, results, and feature options
├── Domain/                periods, immutable sources, and four-column rows
├── Application/           weekly/monthly generation, prompts, and validation
├── Persistence/           separate report database and repositories
├── Export/                Excel and CSV exporters
└── DependencyInjection/   report-specific registration
```

Dependency direction:

```text
TeamHub.Web
├── TeamHub.Studio       structured weekly source data
├── TeamHub.AI           configured model runtime
└── TeamHub.AI.ReportGenerator    report orchestration, validation, persistence, and export

TeamHub.AI.ReportGenerator ─────> TeamHub.AI
```

`TeamHub.AI.ReportGenerator` must not depend on `TeamHub.Web`, Flow Designer, or a provider-specific SDK. Other AI consumers, including the future AI Flow Designer workflow, reuse `TeamHub.AI` but do not depend on the Report Generator workflow.

## Data ownership and persistence

Use a separate SQLite database:

```text
ConnectionStrings:AiReportsDb = Data Source=data/ai-reports.db
```

Proposed persisted information:

- Report ID and report type.
- Requested reporting period and scope.
- Generation status: Pending, Generating, ReadyForReview, Failed, or Superseded.
- Immutable studio ID and group at generation time.
- Source weekly page IDs and versions.
- Source field hashes and optional protected source snapshot.
- Generated four-column rows.
- Model name and model/runtime version.
- Prompt version.
- Validation results and warnings.
- Requesting user and generation timestamps.
- Download timestamps if audit requirements need them.

The AI Reports database must not store Confluence credentials or plaintext provider secrets. Ollama on loopback requires no API secret for the initial deployment. If a future provider requires a credential, use the existing protected-configuration pattern and never return the secret to the browser or write it to logs.

## Access management

Register AI Reports as a separate Team Hub module with independently configurable permissions:

- View AI reports.
- Generate or regenerate AI reports.
- Download AI reports.
- Configure the AI provider, model, credentials, and report settings.

Configuration access should remain administrator-only. Generation and download permissions should use the existing Team Hub access-management system.

## Provider portability contract

The report-generation service depends on the shared `IAiModelService`, not on Ollama, Qwen, or a vendor SDK. Shared provider adapters implement `IAiModelProvider`. Every provider adapter must expose the same capabilities:

- Provider name and availability.
- Model discovery or model-name validation when supported.
- Health check.
- Structured text generation.
- JSON Schema response support, or an explicit capability failure.
- Temperature and maximum-output-token settings.
- Cancellation and timeout support.
- Sanitized error reporting.

Configuration selects a provider by a stable key, for example:

```text
Ollama
LlamaCpp
OpenAICompatible
FoundryLocal
```

Portability rules:

- Ollama model change, such as `qwen3:8b` to another installed Ollama model: configuration only.
- Ollama to llama.cpp: change only `AI__Provider`; each runtime retains its own endpoint and model profile.
- Ollama to Foundry Local or another OpenAI-compatible endpoint: configuration only through `OpenAICompatible` when the required structured-output contract is supported.
- OpenAI-compatible local endpoint to an approved hosted endpoint: configuration and protected credential only.
- A provider with a proprietary API or different authentication/response format: implement one new `IAiModelProvider` adapter, register it, and then use configuration for future switching.
- A model that cannot reliably return the required JSON schema cannot be selected for production report generation even if the provider can run it.

The provider configuration must be snapshotted into each report run so an old report remains auditable after the active provider or model changes.

## Model providers and initial runtime

### Recommended MVP

- Runtime: Ollama.
- Model: `qwen3:8b`.
- Endpoint: `http://127.0.0.1:11434`.
- Model package: approximately 5.2 GB for Ollama's Q4_K_M variant.
- Model license: Apache 2.0.

Reasons:

- Suitable instruction-following quality for controlled business-text editing.
- JSON Schema structured outputs through Ollama.
- Straightforward local HTTP integration from the existing .NET application.
- Runs on Windows and Linux without introducing a Python service.
- Model abstraction allows replacement without changing report-generation logic.
- Ollama exposes OpenAI-compatible endpoints, allowing the generic provider contract to be exercised from the first implementation.

Official references:

- <https://ollama.com/library/qwen3:8b>
- <https://docs.ollama.com/capabilities/structured-outputs>
- <https://docs.ollama.com/api/openai-compatibility>
- <https://qwenlm.github.io/blog/qwen3/>

### Hardware guidance

- Recommended minimum system memory: 16 GB RAM.
- CPU inference is acceptable for development but may be slow for many studios.
- A GPU with roughly 8 GB available VRAM should provide a better interactive experience for the quantized 8B model.
- Benchmark generation time with realistic Team Hub reports before defining the production timeout and concurrency limit.

### Alternative

Phi-4-mini through Microsoft Foundry Local can be evaluated if the deployment environment favors a Microsoft-native Windows/.NET runtime. It is not the MVP default because Foundry Local platform requirements and preview status may constrain deployment environments.

## Installation and environment preparation

Current environment status:

- Ollama `0.20.0` is installed and responding.
- `llama3.2:latest` is currently installed locally.
- `qwen3:8b` has not yet been downloaded.

Required before local inference development:

1. Install Ollama on the machine that runs Team Hub.
2. Download the selected model:

   ```text
   ollama pull qwen3:8b
   ```

3. Confirm the local Ollama service is reachable only from the intended host/network boundary.
4. Run a health test and a structured-output test with representative non-sensitive sample data.
5. Confirm available RAM, GPU/VRAM, disk space, and acceptable generation time.

Python, CUDA Toolkit, and a separate vector database are not required for the MVP. GPU drivers appropriate to the host hardware may be required for acceleration.

Proposed application configuration:

```text
AI__ReportGenerator__Enabled=true
AI__Provider=Ollama
AI__Endpoint=http://127.0.0.1:11434
AI__Model=qwen3:8b
AI__OfflineOnly=true
AI__Temperature=0
AI__TimeoutSeconds=180
AI__ReportGenerator__MaximumConcurrentRequests=1
ConnectionStrings__AiReportsDb=Data Source=data/ai-reports.db
```

Provider configuration must be validated at startup and again through an administrator-facing Test Connection action. A provider or model change must not require recompiling or redeploying Team Hub.

Example future OpenAI-compatible configuration:

```text
AI__Provider=OpenAICompatible
AI__Endpoint=https://approved-ai-service.example/v1
AI__Model=approved-model-deployment
AI__CredentialEnvironmentVariable=TEAMHUB_AI_PROVIDER_API_KEY
```

`CredentialEnvironmentVariable` names the server-side environment variable containing the credential; the credential itself is never committed to configuration files.

Do not expose the Ollama endpoint publicly. Deployment-specific endpoint and resource settings belong in environment configuration, not source-controlled production secrets.

## Monthly boundary rule requiring confirmation

A weekly page can cross a calendar-month boundary. The implementation needs one deterministic rule so a week is not counted in two monthly reports.

Proposed default: assign a Monday-Friday weekly page to the month containing the Wednesday of that business week. This assigns the week to the month containing the majority of its workdays.

Status: Product confirmation required before monthly generation is implemented.

## MVP acceptance criteria

- AI code is contained in the dedicated module and AI feature branch.
- Team Hub operates normally when AI Reports is disabled or the configured provider is unavailable.
- The source consolidated report is never modified.
- Only active studios are included.
- Studio grouping matches Team Hub configuration.
- Action Items and Notes never enter the model request.
- Weekly AI report contains exactly the four approved visible columns.
- Monthly AI report is generated from original weekly source data.
- Studios are processed independently with no cross-studio mixing.
- Structured output and semantic guard validations run before preview.
- Missing weekly pages and missing studio rows are visible to the user.
- Users can review original and generated content.
- Users with permission can download Excel and CSV reports.
- Report metadata and source lineage are retained in `ai-reports.db`.
- Unit, integration, security-boundary, and export tests pass.
- With the default Ollama configuration, no external AI or internet inference call is made.
- External inference occurs only when an administrator deliberately enables and configures a supported external provider.

## Explicit non-goals for MVP

- Cloud-hosted AI models in the MVP user experience. The architecture remains capable of supporting an approved external provider later through configuration.
- Fine-tuning or training a custom model.
- Embeddings, semantic search, or a vector database.
- Automatic email distribution.
- Automatic publication without user review.
- Replacing the existing consolidated or single-studio report.
- Editing Confluence pages through AI.
- Generating decisions, recommendations, risk ratings, or management conclusions.
- Using AI-generated weekly text as monthly source material.
- PDF export until its layout requirements are approved.

## Delivery phases and progress

### Phase 0 — Scope and environment

- [x] Create dedicated branch `feature/ai-generated-reports`.
- [x] Retain AI development on `feature/ai-generated-reports` in the primary `D:\TeamHub` workspace.
- [x] Record scope, architecture, installation needs, and acceptance criteria.
- [ ] Confirm monthly boundary rule.
- [ ] Confirm production operating system and hardware.
- [x] Install Ollama (`0.20.0` verified).
- [ ] Download and benchmark `qwen3:8b`.

### Phase 1 — Module foundation

- [x] Add `TeamHub.AI.ReportGenerator` project and tests to the solution.
- [x] Add configuration and feature flag.
- [x] Add shared provider-neutral `IAiModelService`, `IAiModelProvider`, and provider registry.
- [x] Add generic OpenAI-compatible provider.
- [x] Add Ollama provider/client and health check.
- [x] Add configuration-only provider and model selection.
- [x] Add separate SQLite initializer and repository.
- [x] Register AI Reports permissions.

### Phase 2 — Weekly AI report

- [ ] Add source adapter for existing consolidated weekly data.
- [ ] Exclude Action Items and Notes before inference.
- [ ] Add row-isolated prompt and JSON Schema contract.
- [ ] Add semantic guard validation.
- [ ] Add preview and original/generated comparison.
- [ ] Add grouped four-column rendering.
- [ ] Add Excel and CSV download.
- [ ] Add unit and integration tests.

### Phase 3 — Monthly AI report

- [ ] Implement confirmed month/week boundary rule.
- [ ] Add source coverage calculation.
- [ ] Aggregate original weekly fields by studio.
- [ ] Add controlled deduplication and chronological organization.
- [ ] Add grouped monthly preview.
- [ ] Add Excel and CSV download.
- [ ] Add missing-week and partial-month tests.

### Phase 4 — Hardening and rollout

- [ ] Benchmark realistic report sizes.
- [ ] Add cancellation, timeout, retry, and concurrency controls.
- [ ] Verify loopback/network restrictions.
- [ ] Verify no sensitive content is written to ordinary logs.
- [ ] Add audit and retention policy.
- [ ] Complete user acceptance testing.
- [ ] Document deployment, backup, recovery, and model upgrade procedures.

## Progress log

| Date | Status | Change | Evidence/Notes |
|---|---|---|---|
| 2026-09-14 | Complete | AI feature branch created | Based on committed consolidated/grouped report baseline `59cfed5` |
| 2026-09-14 | Complete | Initial scope and development plan documented | Implementation has not started |
| 2026-09-14 | Complete | Ollama runtime verified | Ollama `0.20.0`; `llama3.2:latest` present; Qwen3 8B not downloaded |
| 2026-09-14 | Complete | Provider portability requirement confirmed | Model/provider selected by configuration; proprietary APIs require a one-time adapter |
| 2026-09-14 | Complete | Phase 1 module foundation implemented | Provider registry, Ollama/OpenAI-compatible adapters, feature configuration, isolated SQLite store, permissions, and five focused tests |
| 2026-09-14 | Verified | TeamHub solution regression check | Full solution build: 0 warnings/errors; all 145 tests passed |

## Branch and synchronization rules

- Use the single `D:\TeamHub` workspace and verify the active branch before making changes.
- AI report work occurs only on `feature/ai-generated-reports`; normal feature and bug-fix work occurs on `main`.
- Keep AI commits small and scoped to the AI module, its host integration, tests, and this document.
- Bring required main-branch fixes into the AI branch deliberately through merge or rebase after verifying the workspace is clean.
- Do not merge unfinished AI code into main.
- Update this document in the same commit as any material scope, architecture, installation, or progress change.
