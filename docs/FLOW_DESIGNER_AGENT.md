# Flow Designer Agent — Architecture Readiness

## Objective

Allow a user to provide an instruction and selected source-of-truth documents, then generate a reviewable Flow Designer draft that accurately represents those sources.

This capability will reuse the shared `TeamHub.AI` runtime. It is not part of AI Reports and does not reuse report prompts, report schemas, or report persistence.

## User workflow

1. The user starts **Create with AI** from Flow Designer.
2. The user describes the intended diagram and supplies or selects source-of-truth documents.
3. Team Hub extracts supported content and shows what will be sent to the configured model.
4. The agent proposes nodes, connections, descriptions, notes, connector-pin placement, and child-diagram relationships where justified by the sources.
5. Team Hub validates the proposal and shows a preview, evidence references, assumptions, and warnings.
6. The user confirms creation of a normal editable draft or cancels without saving.
7. Publishing continues through the existing administrator-controlled publication workflow.

## Project structure

```text
src/TeamHub.FlowDesigner.Agent/
├── Contracts/             public request, source-document, draft, and evidence contracts
├── Application/           future orchestration and prompt/schema coordination
├── SourceDocuments/       future file and approved external-source adapters
├── Validation/            future AI-output, evidence, and diagram integrity validation
├── DependencyInjection/   future host registration
└── README.md              dependency and safety boundary
```

The project references:

- `TeamHub.AI` for provider-neutral structured generation.
- `TeamHub.FlowDesigner.Core` for canonical `FlowDefinition` models and validators.

It does not reference `TeamHub.Web`, `TeamHub.AIReports`, or a model vendor SDK.

## Source-of-truth design

Initial supported sources should be introduced one adapter at a time:

- Plain text and Markdown.
- PDF with page references.
- Word documents with heading/paragraph references.
- Approved Confluence pages using the user's existing connection and access.

Every normalized passage must retain a stable document ID and location. Generated diagram elements should be traceable to those references when practical. Unsupported, encrypted, oversized, empty, or extraction-failed documents must be rejected before inference with a clear message.

## Output and safety contract

- Output is always a draft proposal.
- Existing node, connection, connector-pin, and subgraph rules remain authoritative.
- Parent and child diagrams must be generated and validated as one draft hierarchy.
- The agent may not invent unsupported process steps silently; uncertainty becomes a warning.
- Instructions found inside source documents cannot override system rules or the user's explicit request.
- Invalid structured output is rejected or retried within a bounded policy; it is never partially saved.
- Saving requires user confirmation and normal create/edit permission.
- Publishing requires the existing administrative approval flow.

## Persistence boundary

No Flow Designer Agent database is introduced during architecture preparation. Future generation-run metadata may receive a dedicated store, but actual drafts continue to be owned by Flow Designer. Source-document retention must be decided before implementation; the default should be not to retain full uploaded content after generation.

## Implementation phases

### Phase A — Architecture readiness

- [x] Extract shared provider runtime into `TeamHub.AI`.
- [x] Keep AI Reports as an independent consumer.
- [x] Create `TeamHub.FlowDesigner.Agent` project and dependencies.
- [x] Define draft request, source-document, result, and evidence contracts.
- [x] Record module, safety, and publication boundaries.

### Phase B — Product and security decisions

- [ ] Confirm first supported document formats and size limits.
- [ ] Confirm whether source content can be retained and for how long.
- [ ] Confirm evidence/citation presentation in the Flow Designer UI.
- [ ] Define Flow Designer Agent access-management levels.
- [ ] Define prompt-injection and sensitive-data test cases.

### Phase C — First implementation

- [ ] Implement one source adapter.
- [ ] Define the versioned diagram JSON schema and prompt.
- [ ] Implement structured generation through `IAiModelService`.
- [ ] Validate and map output to `FlowDefinition`.
- [ ] Add preview and explicit draft-creation confirmation.
- [ ] Add unit, integration, and adversarial-source tests.

Implementation must not begin until the Phase B choices that affect data handling and user expectations are confirmed.
