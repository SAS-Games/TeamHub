# TeamHub Flow Designer Agent

This project is the future document-to-diagram module. It is intentionally limited to architecture and contracts in the current phase; no AI orchestration, document reader, UI, endpoint, persistence, or dependency-injection registration has been implemented.

## Boundary

- Depends on `TeamHub.AI` for provider-neutral structured generation.
- Depends on `TeamHub.FlowDesigner.Core` for canonical diagram models and validation contracts.
- Must not depend on the Flow Designer web UI, a particular AI provider, Ollama request types, or report-generation code.
- Produces a draft only. Saving, publishing, and replacing an existing diagram require explicit user actions and existing Flow Designer permissions.

## Planned pipeline

1. Accept a user instruction and explicitly selected source-of-truth documents.
2. Extract supported document content through source adapters.
3. Treat document content as untrusted data and isolate it from system instructions.
4. Generate a versioned, structured diagram proposal through `IAiModelService`.
5. Validate node types, connections, child-diagram references, evidence coverage, and Flow Designer rules.
6. Show a preview with warnings and source references.
7. Create a normal editable draft only after user confirmation.

Automatic publication is outside this module's boundary.
