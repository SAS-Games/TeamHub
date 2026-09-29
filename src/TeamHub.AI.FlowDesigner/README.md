# TeamHub AI Flow Designer

This project implements the controlled source-to-diagram workflow for Team Hub Flow Designer.

## Current boundary

- Accepts a user instruction plus in-memory plain-text or Markdown source documents.
- Uses `TeamHub.AI` for provider-neutral structured generation.
- Maps the response to the canonical `FlowDefinition`, node types, notes, connector pins, and port-layout properties from `TeamHub.FlowDesigner.Core`.
- Applies deterministic coordinates and validates the proposal with the existing `IFlowValidator`.
- Returns an unsaved draft with evidence references and warnings.
- Can propose one root plus linked child diagrams, but does not store source content, save anything, or publish anything.

The host registers the normal Flow Designer and `AddAiFlowDesigner`, because Flow Designer supplies the authoritative validator and persistence boundary. The Flow Designer web module provides **Create with AI**, a review screen, and a protected explicit-confirmation handoff. Confirmation calls the existing `IFlowHierarchyService` to validate, remap, and transactionally save a normal editable hierarchy. Editing, child navigation, permissions, publication requests, and publication remain owned by Flow Designer.