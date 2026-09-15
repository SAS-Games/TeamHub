# AI Flow Designer - Controlled Workflow

## Objective

Allow a user to provide an instruction and selected source-of-truth documents, then generate a reviewable diagram using the existing Team Hub Flow Designer.

This capability reuses the shared `TeamHub.AI` runtime. It is independent from Report Generator and does not reuse report prompts, schemas, or persistence.

## Classification

This is a controlled AI workflow, not an autonomous agent. Team Hub defines every step, permitted source, schema, validation rule, stopping condition, and persistence action. The model performs structured source-to-diagram generation only.

## Flow Designer ownership

AI Flow Designer does not introduce a second diagram engine or diagram format. It produces the canonical `FlowDefinition` used by Flow Designer and follows its existing:

- diagram and node types;
- node description and notes fields;
- connector pin identifiers and pin limits;
- connector placement properties;
- domain validator;
- draft ownership and access rules;
- child-diagram behavior;
- publication request and administrator approval process.

The AI workflow cannot save or publish a diagram. Its current output is an unsaved proposal. A future UI integration will open that proposal in Flow Designer, where the user can review and explicitly create a normal editable draft.

## User workflow

1. The user starts **Create with AI** from Flow Designer.
2. The user describes the intended diagram and explicitly supplies or selects source documents.
3. Team Hub extracts supported content and shows what will be sent to the configured model.
4. The model proposes structured nodes, connections, descriptions, notes, and connector pins.
5. Team Hub maps the proposal to `FlowDefinition`, applies deterministic layout, runs the existing Flow Designer validator, and shows a preview with evidence and warnings.
6. The user confirms creation of a normal editable draft or cancels without saving.
7. Child diagrams are added or generated through Flow Designer and remain attached to their parent hierarchy.
8. Publishing continues through the existing administrator-controlled publication workflow.

## Current implementation

The first backend slice is available in `src/TeamHub.AI.FlowDesigner`:

- Provider-neutral structured generation through `IAiModelService`.
- Versioned `flow-hierarchy-v2` JSON schema for one root and linked child pages.
- In-memory plain-text and Markdown sources.
- Maximum 10 sources, 120,000 combined source characters, and 4,000 prompt characters by default.
- Source content treated as untrusted data with prompt-injection instructions in the system prompt.
- Strict output parsing and evidence-reference checks.
- Mapping to existing Flow Designer node types, `customProperties.notes`, `output_N` / `input_N` connector pins, and `top-bottom` port layout.
- Deterministic draft layout followed by `IFlowValidator` validation.
- Disabled-by-default production registration under `AI:FlowDesigner`; the local Development profile enables it for testing.
- A Flow Designer **Create with AI** page for up to 10 pasted sources, generated-node/connection preview, warnings, and evidence.
- A protected full-hierarchy preview and explicit confirmation through the existing `IFlowHierarchyService`.
- Bounded root/child generation with one-parent ownership, no orphans or cycles, and defaults of 12 diagrams and four hierarchy levels.
- Transactional Flow Designer persistence: confirmation writes the complete hierarchy or writes nothing; deleting the root removes that draft hierarchy together.
- No source retention, AI-owned database, or AI-controlled publishing.

## Source-of-truth design

The initial source processor accepts:

- `text/plain`
- `text/markdown`
- `text/x-markdown`

Unsupported, oversized, duplicate-ID, or empty documents are rejected before inference. Source content remains in memory for the request and is not stored by this module.

Future adapters can add PDF page references, Word heading/paragraph references, and approved Confluence pages through the user's existing access. Each adapter must retain stable document and location references without bypassing source access.

## Output and safety contract

- Output is always an unsaved draft proposal.
- Existing Flow Designer models and validation remain authoritative.
- Unsupported process steps must not be introduced silently; uncertainty becomes a warning.
- Source-document instructions cannot override system rules or the user's explicit request.
- Invalid structured output is rejected as one unit; it is never partially saved.
- Saving requires user confirmation and normal Flow Designer create/edit permission.
- Publishing requires the existing administrative approval flow.
- Parent and child diagrams are generated, validated, previewed, and confirmed as one hierarchy.
- Every child must be linked exactly once from a parent Subprocess; orphan, shared, missing, cyclic, oversized, or over-depth hierarchies are rejected.

## Persistence boundary

No AI Flow Designer database is introduced. Complete source documents and unsaved proposals are not retained by this module. Confirmed diagrams will be stored by the existing Flow Designer only. Provider/model/request metadata is added to the draft without storing the prompt or source contents.

## Implementation phases

### Phase A - Architecture readiness

- [x] Extract shared provider runtime into `TeamHub.AI`.
- [x] Keep Report Generator as an independent workflow consumer.
- [x] Create `TeamHub.AI.FlowDesigner` project and dependencies.
- [x] Define `IFlowDiagramGenerationWorkflow` and draft/evidence contracts.
- [x] Record safety, data, and publication boundaries.

### Phase B - Initial product and security decisions

- [x] Start with plain text and Markdown and configurable request limits.
- [x] Do not retain complete source content.
- [x] Return evidence by Flow Designer element ID, source ID, location, and explanation.
- [x] Reuse normal Flow Designer create/edit permissions; do not create a separate publication permission path.
- [x] Treat selected content as untrusted and test embedded prompt-injection instructions.

### Phase C - First implementation

- [x] Implement the in-memory text/Markdown source processor.
- [x] Define the versioned diagram JSON schema and prompt.
- [x] Implement structured generation through `IAiModelService`.
- [x] Validate and map output to `FlowDefinition` using existing connector and node conventions.
- [x] Add the Flow Designer **Create with AI** UI, preview, and explicit draft-creation confirmation.
- [ ] Add file-upload and approved external-source adapters.
- [x] Add parent/child hierarchy generation and atomic confirmation.
- [x] Add unit and adversarial-source tests for the backend slice.