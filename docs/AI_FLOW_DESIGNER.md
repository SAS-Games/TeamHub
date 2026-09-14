# AI Flow Designer — Workflow Architecture Readiness

## Objective

Allow a user to provide an instruction and selected source-of-truth documents, then generate a reviewable Flow Designer draft that accurately represents those sources.

This capability will reuse the shared `TeamHub.AI` runtime. It is independent from Report Generator and does not reuse report prompts, schemas, or persistence.

## Classification

This is a controlled AI workflow, not an autonomous agent. Team Hub defines every step, permitted source, retry limit, validation rule, stopping condition, and persistence action. The model performs structured document-to-diagram generation only.

## User workflow

1. The user starts **Create with AI** from Flow Designer.
2. The user describes the intended diagram and explicitly supplies or selects source documents.
3. Team Hub extracts supported content and shows what will be sent to the configured model.
4. The model proposes structured nodes, connections, descriptions, notes, connector-pin placement, and child-diagram relationships.
5. Team Hub validates the proposal and shows a preview, evidence references, assumptions, and warnings.
6. The user confirms creation of a normal editable draft or cancels without saving.
7. Publishing continues through the existing administrator-controlled publication workflow.

## Project structure

```text
src/TeamHub.AI.FlowDesigner/
|-- Contracts/            public workflow request, draft, and evidence contracts
|-- Application/          future fixed orchestration and prompt/schema coordination
|-- SourceDocuments/      future file and approved external-source adapters
|-- Validation/           future AI-output, evidence, and diagram validation
|-- DependencyInjection/  future host registration
`-- README.md             dependency and safety boundary
```

The project references:

- `TeamHub.AI` for provider-neutral structured generation.
- `TeamHub.FlowDesigner.Core` for canonical `FlowDefinition` models and validators.

It does not reference `TeamHub.Web`, `TeamHub.AI.ReportGenerator`, or a model vendor SDK.

## Source-of-truth design

Initial supported sources should be introduced one adapter at a time:

- Plain text and Markdown.
- PDF with page references.
- Word documents with heading or paragraph references.
- Approved Confluence pages using the user's existing connection and access.

Every normalized passage must retain a stable document ID and location. Generated elements should be traceable to those references when practical. Unsupported, encrypted, oversized, empty, or extraction-failed documents must be rejected before inference.

## Output and safety contract

- Output is always a draft proposal.
- Existing node, connection, connector-pin, and subgraph rules remain authoritative.
- Parent and child diagrams must be generated and validated as one draft hierarchy.
- Unsupported process steps must not be introduced silently; uncertainty becomes a warning.
- Source-document instructions cannot override system rules or the user's explicit request.
- Invalid structured output is rejected or retried under a bounded application policy; it is never partially saved.
- Saving requires user confirmation and normal create/edit permission.
- Publishing requires the existing administrative approval flow.

## Persistence boundary

No new database is introduced during architecture preparation. Future generation-run metadata may receive a dedicated store, but actual drafts remain owned by Flow Designer. Source retention must be decided before implementation; the default should be not to retain complete uploaded content after generation.

## Implementation phases

### Phase A — Architecture readiness

- [x] Extract shared provider runtime into `TeamHub.AI`.
- [x] Keep Report Generator as an independent workflow consumer.
- [x] Create `TeamHub.AI.FlowDesigner` project and dependencies.
- [x] Define `IFlowDiagramGenerationWorkflow` and draft/evidence contracts.
- [x] Record safety, data, and publication boundaries.

### Phase B — Product and security decisions

- [ ] Confirm initial document formats and size limits.
- [ ] Confirm whether source content can be retained and for how long.
- [ ] Confirm evidence presentation in the Flow Designer UI.
- [ ] Define AI Flow Designer access-management levels.
- [ ] Define prompt-injection and sensitive-data test cases.

### Phase C — First implementation

- [ ] Implement one source adapter.
- [ ] Define the versioned diagram JSON schema and prompt.
- [ ] Implement structured generation through `IAiModelService`.
- [ ] Validate and map output to `FlowDefinition`.
- [ ] Add preview and explicit draft-creation confirmation.
- [ ] Add unit, integration, and adversarial-source tests.

Implementation must not begin until the Phase B choices affecting data handling and user expectations are confirmed.