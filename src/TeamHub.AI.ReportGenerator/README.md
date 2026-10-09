# TeamHub AI Report Generator

This project owns controlled AI report workflows. Weekly Work Summary is
implemented; the broader weekly/monthly report and export contract remains a
future feature.

## Boundary

- Depends on `TeamHub.AI` through `IAiModelService`.
- Does not call Ollama or another vendor API directly.
- Uses only report sources selected by Team Hub application code.
- Produces a reviewable report draft; it does not make management decisions or modify source data.
- Uses deterministic validation and application-controlled retry/stopping rules.

## Weekly Work Summary

`IWeeklyWorkSummaryWorkflow` accepts immutable studio identity and only HPGDS
Support and Notes for a Monday-Friday week. It invokes `IAiModelService` once
per studio and returns rows with exactly Studio and Support Activity fields.
Prompts require simple management-friendly English without changing meaning.
HPGDS Support and Notes are rephrased and validated independently, then combined
server-side. Empty replacements for non-empty fields are rejected. Structured
output and protected numbers, URLs, ticket IDs and acronyms are validated.
Failed validation rejects the whole draft, not a success-shaped
fallback. Human review is required; semantic equivalence cannot be guaranteed.

A singleton concurrency gate bounds report generation across requests.
Cancellation releases the slot. Oversized sources are rejected without
truncation. Completed generation metadata (source hash, period, user, provider,
model and prompt version) is recorded in the existing separate AI report
database; generated text and source text are not persisted.

Enable with `AI:ReportGenerator:Enabled=true`. Options:

- `MaximumConcurrentRequests`: 1 by default, range 1-16.
- `MaximumSourceCharactersPerStudio`: 24000 by default, range 1-120000.
- `MaximumOutputTokens`: 4000 by default, range 1-16000.
- `WeeklyWorkSummaryPromptPath`: defaults to `Prompts/WeeklyWorkSummary.md`
  relative to the application's binary directory; absolute paths are supported.

## Editing model instructions

The actual system instructions are in
[Prompts/WeeklyWorkSummary.md](Prompts/WeeklyWorkSummary.md), not this README.
The file is copied into build/publish output as `Prompts/WeeklyWorkSummary.md`.
Edit the deployed file to change instructions without recompiling or restarting.
For local development, edit the source file and build to copy it to the output,
or set `AI:ReportGenerator:WeeklyWorkSummaryPromptPath` to the source file's
absolute path to use it directly.

The file is read once at the start of each report. All studios in that report
use the same snapshot; subsequent reports pick up edits. The SHA-256 hash of the
loaded instructions is recorded as the run's prompt version. Missing, unreadable
or empty instructions fail explicitly; there is no embedded fallback prompt.
Only trusted server administrators should be allowed to edit this file.

Keep the two JSON output fields and meaning-preservation rules intact.
Schema, permissions, source selection and deterministic validation remain in
code: editing the prompt does not bypass them or add new report functionality.

## Copying the report

After successful generation, **Copy Report** copies the two-column table as
HTML and plain text where supported, so it can be pasted into an email, document
or spreadsheet. Text-only browsers receive tab-separated text. Clipboard access
requires HTTPS or localhost and browser permission. If copying fails, the page
displays instructions for selecting and copying the table manually.

The host adapter owns Confluence fetching and authorization. In Team Hub,
generation requires Studio Support Read Only and AI Reports Create access
(including role/per-user matrix grants); admins have Full Access. Source
credential rules are unchanged. Monthly/custom ranges are not supported by this
workflow. Existing `IReportGenerationWorkflow` is reserved for the broader
report feature, not reused through report-type compatibility branches.
