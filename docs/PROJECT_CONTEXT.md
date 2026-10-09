# Team Hub project context

## Runtime and composition

Team Hub is a Windows-first .NET 10 ASP.NET Core Razor Pages application.
`global.json` selects the 10.0.4xx SDK series. `src/TeamHub.Web/Program.cs`
is the composition root: it registers module services, resolves configuration
paths, initializes databases, discovers access-matrix modules, and maps pages
and Flow Designer endpoints. Authentication precedes the access middleware.

## Module map

| Module | Responsibility |
|---|---|
| TeamHub.Domain / Application / Infrastructure | Workflow definitions, drafts, instances, dependencies, notification outbox, email and reminder scheduling; EF Core SQLite |
| TeamHub.Authentication | Cookie authentication, authorized users, role defaults, per-user access overrides, access-matrix persistence |
| TeamHub.Team | Team directory, specializations, achievements and configurable team tabs |
| TeamHub.Studio | Active studio/project directory, Atlassian configuration and protected credentials, Jira tickets, read-only consolidated Confluence updates, support-summary planning |
| TeamHub.Excel / Milestones | Local or configured workbook sources and milestone data |
| TeamHub.FlowDesigner.Core / FlowDesigner / FlowDesigner.Web | Canonical diagram contracts, persistence/validation/publication, reusable Razor UI and APIs |
| TeamHub.AI | Provider-neutral structured inference, provider registry, health checks, timeouts, cancellation and credentials |
| TeamHub.AI.FlowDesigner | Document-to-diagram prompts, parsing and validation; uses the shared runtime, never publishes automatically |
| TeamHub.AI.ReportGenerator | Report-specific contracts, generation, validation, concurrency and separate report-run audit storage |
| TeamHub.Web | Page composition, navigation, access enforcement, safe formatting, home content and worklog-effort integration |

## Storage and deployment

The web host owns a `data` directory with separate workflow, access, team,
studio, flow-authoring, flow-library and AI-report databases. Program overrides
connection strings to this directory. Data Protection keys live alongside the
databases and are machine-protected with DPAPI on Windows. Preserve databases
and keys during deployment; do not commit runtime database changes.
Repository `config` paths resolve to the nearest containing config directory.
Hosting/publishing scripts are described in `scripts/hosting/README.md`.

## Access control

`TeamHubModules` defines known matrix rows. `CurrentAccessService` resolves an
active authorized user and caches their snapshot for the request; role defaults
and per-user overrides are resolved by `IUserAccessService`. Admin has Full
Access. Permission levels are ordered No Access, Read Only, Create, Edit,
Delete, Full Access. Route middleware determines module and required operation.
Pages must also check permissions for cross-module actions.

Support Summary is a Studio Support page. Weekly Work Summary additionally
requires AI Reports Create access. Registered/Privileged users default to
AI Reports Read Only, so generation requires an explicit grant. Atlassian
credential access is independent of report-generation permission.

## Support Summary data flow

`src/TeamHub.Web/Pages/SupportSummary.cshtml.cs` owns page filters and dates.
Weekly options use Monday-Friday; the first week can start in the preceding
month. Monthly/custom ranges remain separate existing behaviors.

The page loads active studios, planning records, milestones, Jira and Confluence.
Independent service scopes protect scoped database services during parallel
loads. Effort is loaded separately through the existing worklog integration.
Search filters are presentation filters, not report source selection.

Support Activity Tickets offers on-demand Latest Comments via
`/SupportSummary/Comments` with an Image handler. The separate
`IStudioJiraCommentService` validates active studio/project/component scope,
uses existing personal/shared credential rules and fetches all comment pages
before sorting newest-first. It never changes ticket-search caching or filters.
Only attachments referenced by a separately re-authorized visible comment
may be proxied. Attachment downloads stay under the configured Jira URL with
redirects disabled, supported raster formats/signatures and a 10 MB cap.
No Jira credentials or arbitrary raw HTML reach the browser. External images
remain links; unsupported/unresolvable media shows an explicit Jira-view notice.
Image downloads are bounded to four concurrent requests in the preview; closing
or changing tickets cancels pending work and releases browser object URLs.

Comment-preview verification: 125 focused Studio/comment/page/access tests
passed, including paging/order, shared access, attachment membership and
restricted comments, unsafe hosts/formats and explicit failures. The web/Razor
build and JavaScript syntax checks passed. Mocked browser tests exercised image
decoding, escaped text, external links, ticket switching, error notices, image
concurrency and modal cleanup. A live Jira instance was not used for this check.

`SupportSummaryPlanning.cs` stores expected milestone deliveries and build
reviews; it is not the weekly work source. `IStudioConfluenceUpdateService`
fetches consolidated updates with source week, page ID/version, studio identity,
HPGDS Support, Notes and other columns. Missing pages, studios and configuration
are explicit results. Confluence source access remains read-only.

## Weekly Work Summary boundary

The web adapter selects all active studios for the selected weekly period and
projects only HPGDS Support and Notes into provider-neutral report sources.
No Jira, milestone, effort, Studio Work, WMD Support or Action Items content is
sent to inference. Source fetching uses the requesting user's existing
Confluence credential rules. Missing coverage is disclosed.

The Report Generator processes studios separately through `IAiModelService`,
validates structured responses and protected identifiers, and returns a
reviewable two-column draft. Source content is untrusted data, not instructions.
Model output never controls studio identity or modifies source records.
Human review remains necessary: automated checks cannot prove semantic identity.

Model instructions live in Report Generator's `Prompts/WeeklyWorkSummary.md`,
copied to build/publish output. The file is read once per report; its SHA-256
hash is the audit prompt version. Administrators can edit the deployed file
without a rebuild/restart, or override `WeeklyWorkSummaryPromptPath`.
Schema and validation remain code-owned. Successful previews offer Copy Report
with HTML/plain-text clipboard representations; failures are explicitly shown.

Future weekly/monthly report types should have their own contracts/workflows
inside Report Generator, sharing the runtime rather than adding compatibility
branches to existing source, planning or Flow Designer implementations.

## Configuration and tests

`AI` config selects Ollama, LlamaCpp or OpenAICompatible. Offline-only mode
defaults to true, rejecting non-loopback inference endpoints. Provider profiles
allow model/runtime changes without feature changes. ReportGenerator and
FlowDesigner have independent disabled-by-default feature flags.

Tests use xUnit/VSTest and FluentAssertions. `tests/TeamHub.Tests` covers web,
Studio, authentication, workflows and milestones. AI runtime, report generation
and AI Flow Designer have separate test projects. Run focused report/page/access
tests plus shared AI/Flow Designer regression tests when changing this integration.
See `docs/AI_ARCHITECTURE.md` and `docs/AI_GENERATED_REPORTS.md` for AI decisions.
