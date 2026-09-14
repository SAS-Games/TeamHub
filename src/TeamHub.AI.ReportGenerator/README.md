# TeamHub AI Report Generator

This project owns the controlled weekly and monthly report-generation workflow. The workflow implementation has not started; the current project contains its public boundary, feature configuration, and separate report-run persistence foundation.

## Boundary

- Depends on `TeamHub.AI` through `IAiModelService`.
- Does not call Ollama or another vendor API directly.
- Uses only report sources selected by Team Hub application code.
- Produces a reviewable report draft; it does not make management decisions or modify source data.
- Uses deterministic validation and application-controlled retry/stopping rules.

The public orchestration contract is `IReportGenerationWorkflow`.
