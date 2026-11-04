# TeamHub AI Usage Ledger

This module owns durable AI-consumption telemetry. It stores operation attribution separately from
provider invocations so one user action, such as a weekly report, can contain several model calls.

The ledger records user, feature, subfeature, application, provider, model, provider request ID,
status, duration and provider-reported input/output/total tokens. It never stores prompts, source
documents, model responses or credentials. Missing provider usage is recorded as unavailable rather
than as zero.

Register `AddAiUsageLedger(configuration)`, initialize `IAiUsageDatabaseInitializer` during startup,
and route every generation through `IAiUsageTracker`. Raw operations default to 12 months of
retention and can be configured from 6 to 120 months with `AI:Usage:RetentionMonths`; pruning runs
during database initialization and then on a background interval. The interval defaults to 24 hours
and can be configured from 1 to 168 hours with `AI:Usage:MaintenanceIntervalHours`. Reports can filter by time, user, feature,
application, provider or model and group by the supported `AiUsageDimension` values. Raw-operation
retention is exposed through `IAiUsageLedger.DeleteBeforeAsync`; long-lived rollups remain deferred.

TeamHub.Web exposes the query API through the admin-only `/Configuration/AI/Usage` report page.
The page exports the filtered usage or tool-activity view as CSV and both reports as a formatted
two-sheet Excel workbook.
The same page configures hard daily token limits with an organization default, per-user overrides,
and a local-midnight reset timezone. `IAiUsageLimitService` exposes settings and current user status;
`IAiUsageTracker` enforces the effective limit immediately before each provider call.
