# AI Usage Ledger

TeamHub records AI consumption in the separate `TeamHub.AI.Usage` module and `data/ai-usage.db`.
The database is independent from AI reports, workflow data and provider credentials so it can be
moved to another persistence implementation without changing AI features.

## Attribution

An operation represents a user-visible action. An invocation represents one provider call. AI Chat
and Flow Designer normally create one invocation per operation. Weekly Work Summary creates one
operation with one invocation per studio. Every invocation retains the provider and model actually
used, even when the configured model changes later.

Current feature values are `Chat`, `ReportGenerator` and `FlowDesigner`. Chat mode and report/flow
type are stored as subfeatures. Studio-scoped calls store the studio ID as invocation resource
metadata. Provider request IDs, status, duration and provider-reported input/output/total tokens are
stored. Prompts, source documents, generated content and credentials are never stored.

## Tool activity

Server-controlled AI tool execution is recorded in a separate `AiToolActivities` stream linked to
the parent operation. Each row contains the user attribution inherited from that operation, tool
name, completion status, duration, optional external-source category, and an optional sanitized
resource identifier such as the requested studio. Tool arguments, prompts, returned tool content,
error messages and credentials are never stored; failures retain only the exception type.

Tool activity does not create provider-invocation rows and therefore does not inflate request counts
or token totals. Administrators can filter and group the Tool Activity report by user, tool,
tool/resource, external source, status or UTC day from the existing AI Usage Ledger page. Jira and
Confluence tools are categorized as external while TeamHub-owned lookups remain internal.

## Reporting and retention

`IAiUsageLedger.GetReportAsync` filters by UTC range, user, feature, subfeature, application,
provider or model. It can group by user, feature, subfeature, application, provider, model,
user/feature, feature/model, user/feature/model or UTC day. Missing provider telemetry is counted as
unavailable rather than as zero. Administrators can run these reports from **Configuration > AI
Services > AI Usage Ledger**; the default view covers the last 30 UTC days and groups by user,
feature and model. The active filters and grouping can be downloaded as a usage CSV, a tool-activity
CSV, or one formatted Excel workbook with separate **Usage** and **Tool activity** worksheets.

Raw operations default to 12 months of retention. `AI:Usage:RetentionMonths` accepts 6 through 120;
startup initialization prunes older operations, their provider invocations, tool activity and old
threshold markers. A background maintenance service repeats this cleanup every 24 hours without
requiring an application restart. `AI:Usage:MaintenanceIntervalHours` can set the interval from 1 to
168 hours. The module also exposes `DeleteBeforeAsync` for explicit maintenance. Every cycle logs its
cutoff, deleted operation count and any failure. Long-lived monthly rollups are intentionally deferred
until the organization confirms reporting requirements.

Back up `data/ai-usage.db` with the rest of TeamHub's application data. Use a SQLite-consistent backup
procedure while TeamHub is running, or stop TeamHub before copying or restoring the database file.

## Daily consumption limits

Administrators configure a default per-user daily token limit and optional per-user overrides from
the AI Usage Ledger page. A blank or zero organization default is unlimited. A blank user override
inherits the organization default, while a zero user override makes that user unlimited. The reset
timezone is configurable and defaults to `Asia/Kolkata`.

The ledger calculates each daily window from local midnight, so no scheduled reset job or database
counter reset is needed. Before a provider call, TeamHub serializes calls for that user, totals the
provider-reported tokens in the current window, and blocks the new call when usage has already reached
the effective limit. The final allowed call can exceed the limit because its token usage is known only
after completion. Calls for which the provider supplies no token telemetry cannot count toward limit
enforcement.

Administrators can enable in-app consumption warnings and configure 1-10 global percentage
thresholds (75, 90 and 100 by default). A user with an effective finite limit receives each threshold
at most once in the applicable daily reset window. If one provider call crosses multiple thresholds,
TeamHub records all of them as satisfied and displays only the highest newly crossed warning to avoid
notification flooding. Unlimited users receive no warning. Warnings are written to the TeamHub
notification inbox only and are never queued for email; the 100% warning also explains that later
provider calls are blocked until the next local-midnight reset.

Threshold markers use the same timezone-derived window boundaries as enforcement and are persisted
in the AI usage database. Each marker keeps a stable notification identifier, so a retry after a
partial failure cannot create a duplicate inbox item.

## PFB credentials

The PFB provider first checks for the signed-in user's encrypted personal gateway token. When the user
does not have one, it falls back to the encrypted static M2M bearer/application key configured in the
Sony PFB AI Gateway profile under AI Services. Requests without a signed-in user, such as background
application work, use the M2M key. Usage is still attributed to the authenticated TeamHub user because
provider authentication and usage attribution are separate concerns.
