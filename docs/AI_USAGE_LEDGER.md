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
feature and model.

Raw operations default to 12 months of retention. `AI:Usage:RetentionMonths` accepts 6 through 120;
startup initialization prunes older operations, their provider invocations and their tool activity. The module also exposes
`DeleteBeforeAsync` for explicit maintenance. Long-lived monthly rollups and notification policies
are intentionally deferred until the organization confirms reporting and alert requirements.

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

## PFB credentials

The PFB provider first checks for the signed-in user's encrypted personal gateway token. When the user
does not have one, it falls back to the encrypted static M2M bearer/application key configured in the
Sony PFB AI Gateway profile under AI Services. Requests without a signed-in user, such as background
application work, use the M2M key. Usage is still attributed to the authenticated TeamHub user because
provider authentication and usage attribution are separate concerns.
