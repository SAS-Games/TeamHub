# TeamHub AI Flow Designer

This project implements the controlled source-to-diagram workflow for Team Hub Flow Designer.

## Current boundary

- Accepts a user instruction plus in-memory plain-text or Markdown source documents.
- Uses `TeamHub.AI` for provider-neutral structured generation.
- Maps the response to the canonical `FlowDefinition`, node types, notes, connector pins, and port-layout properties from `TeamHub.FlowDesigner.Core`.
- Applies deterministic coordinates and validates the proposal with the existing `IFlowValidator`.
- Returns an unsaved draft with evidence references and warnings.
- Does not store source content, save a draft, create child diagrams, or publish anything.

The host registers the normal Flow Designer and `AddAiFlowDesigner`, because Flow Designer supplies the authoritative validator and persistence boundary. The Flow Designer web module provides **Create with AI**, a review screen, and a protected explicit-confirmation handoff. Confirmation calls the existing `IFlowService` to create and save a normal editable draft. Editing, child-diagram management, permissions, publication requests, and publication remain owned by Flow Designer.