# TeamHub AI Flow Designer

This project is the future controlled document-to-diagram workflow. It currently contains architecture and public contracts only; no model orchestration, document reader, UI, endpoint, persistence, or dependency-injection registration has been implemented.

## Boundary

- Depends on `TeamHub.AI` for provider-neutral structured generation.
- Depends on `TeamHub.FlowDesigner.Core` for canonical diagram models and validation contracts.
- Does not depend on the Flow Designer web UI, Report Generator, a particular AI provider, or vendor request types.
- Produces a draft only. Saving, publishing, and replacing diagrams require explicit user actions and existing Flow Designer permissions.

## Planned fixed pipeline

1. Accept a user instruction and explicitly selected source documents.
2. Extract supported content through configured source adapters.
3. Treat source content as untrusted data and isolate it from system instructions.
4. Request a versioned structured diagram proposal through `IAiModelService`.
5. Validate nodes, connections, child diagrams, evidence coverage, and Flow Designer rules.
6. Show a preview with warnings and source references.
7. Create a normal editable draft only after user confirmation.

The model cannot choose arbitrary tools or publish diagrams.