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
| TeamHub.MilestoneReview | Isolated milestone-review lifecycle, contributor assignments, feedback ownership and optimistic concurrency |
| TeamHub.Studio | Active studio/project directory, Atlassian configuration and protected credentials, Jira tickets, read-only consolidated Confluence updates, support-summary planning |
| TeamHub.Excel / Milestones | Local or configured workbook sources and milestone data |
| TeamHub.FlowDesigner.Core / FlowDesigner / FlowDesigner.Web | Canonical diagram contracts, persistence/validation/publication, reusable Razor UI and APIs |
| TeamHub.AI | Provider-neutral structured inference, provider registry, health checks, timeouts, cancellation and credentials |
| TeamHub.AI.FlowDesigner | Document-to-diagram prompts, parsing and validation; uses the shared runtime, never publishes automatically |
| TeamHub.AI.ReportGenerator | Report-specific contracts, generation, validation, concurrency and separate report-run audit storage |
| TeamHub.Web | Page composition, navigation, access enforcement, safe formatting, home content and worklog-effort integration |

## Storage and deployment

The web host owns a `data` directory with separate workflow, access, team,
studio, flow-authoring, flow-library, AI-report and milestone-review databases. Program overrides
connection strings to this directory. Data Protection keys live alongside the
databases and are machine-protected with DPAPI on Windows. Preserve databases
and keys during deployment; do not commit runtime database changes.
Repository `config` paths resolve to the nearest containing config directory.
Hosting/publishing scripts are described in `scripts/hosting/README.md`.

## Milestone Review

`TeamHub.MilestoneReview` owns its SQLite database and is separate from the
configurable Team tables. Administrators create one draft at a time, select
active registered reviewers, and publish it. Publishing supersedes the previous
current review and moves it into the Review History archive, retaining all
feedback rows for read-only browsing. Drafts can include an optional Jira ticket ID.

Administrators can edit all creation fields on drafts and the current published
review using Edit Review; archived reviews remain read-only. Contributor changes
add new feedback rows without deleting existing feedback. Removed contributors
can still see their feedback but cannot change it; reassigning them restores
editing on the same row. Changing the Jira ticket clears local comment links and
increments feedback versions without modifying old Jira comments. Each reviewer's
next save syncs to the newly selected ticket. Detail edits do not republish the
review or change its publication date. The Milestone Review page does not display
the shared read-only access banner; permission enforcement is unchanged.

Administrators can also archive the current published review without publishing
a replacement. Archive preserves feedback in Review History and closes editing.
Only administrators can permanently delete published or archived reviews.
Deletion cascades to the review's local contributor assignments and feedback;
it does not delete Jira tickets or comments, restore an older review as current,
or affect an existing draft. Both actions require confirmation in the UI and
admin authorization in the page handlers and service.

Each selected reviewer receives one row, with Reviewer populated from the
authorized user's display name and their email retained for Jira identity.
Feedback uses four sections: Session Details (platform, duration, areas explored),
What's Good, Needs Improvement, and Additional Feedback (optional feature or
discipline-specific notes). Guidance remains visible under each column heading;
editable fields also include placeholders and accessible labels. All four fields
support up to 20,000 characters and are used in storage, previews, and Jira comments.
Descriptions and all four feedback sections support the shared safe Markdown
renderer used by Team achievements. Editable rows retain Markdown source and
offer a saved-feedback preview; read-only rows and local history previews render
formatting. History copy preserves the original Markdown text. Jira sync retains
the submitted source text (Jira's own comment formatting remains separate).
Raw HTML is encoded and only supported safe links are rendered.
The development schema update adds SessionDetails and AdditionalFeedback and
removes the obsolete WhatsBad column; existing WhatGood and NeedImprovements
values are retained. No legacy feedback display mode is used.
All users with module read access can see the current review and all feedback.
Only the assigned reviewer can edit their row; administrators can edit any row.
The service enforces ownership independently of the UI and uses a version
concurrency token to reject stale updates.

When a review has a Jira ticket, saving a row creates or updates one Jira
comment authored through that reviewer's personal Jira credential. The
comment body uses bold Jira-wiki titles for Reviewer, Reviewer email, Session
Details, What's Good, Needs Improvement, and Additional Feedback, without the
repeated review/project/milestone header. Jira previews render these titles
bold using text-safe elements; feedback content and images remain intact. A stable
feedback ID marker provides safe retry reconciliation if Jira succeeds before
Team Hub stores the returned comment ID. Shared/read-only Jira credentials are
rejected for this write operation. Feedback is persisted locally first; a Jira
failure is recorded on the row and surfaced to the reviewer for retry. Jira
projects may be shared by multiple studios. Ticket-only milestone sync, comment
previews and images validate the ticket prefix and actual Jira project against
the shared Default Jira project key(s) configured under Jira Access.
They do not require studio mappings or components. The review's Project Name
is display metadata, not a Jira project key. An empty allowed-key list denies
milestone Jira access; credentials and Jira's own ticket permissions still apply.
Blank per-studio Jira project keys inherit these defaults for ticket queries,
batch Support Summary queries, comments, and images; explicit studio keys
override rather than merge with defaults. Studio component checks still apply
to studio-scoped requests. Inheritance is resolved at read time and does not
populate saved studio fields, so future default changes affect inheriting studios.
The initializer renames the previous milestone-only project-key column while
preserving its saved values.
Support Summary keeps its explicit studio scope. Both paths use the same Jira
comment service and credential/image protections.

The separate SQLite database initializer adds new Jira/reviewer columns to
existing Milestone Review databases without replacing their persisted reviews.
The Milestone Review page uses its own access-matrix module; Registered and
Privileged defaults are Read Only, while administrators retain Full Access.

## Access control

`TeamHubModules` defines known matrix rows. `CurrentAccessService` resolves an
active authorized user and caches their snapshot for the request; role defaults
and per-user overrides are resolved by `IUserAccessService`. Admin has Full
Access. Permission levels are ordered No Access, Read Only, Create, Edit,
Delete, Full Access. Route middleware determines module and required operation.
Pages must also check permissions for cross-module actions.

Support Summary is a Studio Support page. Its bottom AI Reports section and
generation endpoint require AI Reports Create access or higher, Studio Support
Read Only access, and Report Generator enabled in Configuration > AI Services. Administrators receive
Full Access through the existing permission resolver; non-admin matrix grants
also authorize generation. Atlassian
credential access is independent of report-generation permission.

## Support Summary data flow

`src/TeamHub.Web/Pages/SupportSummary.cshtml.cs` owns page filters and dates.
Weekly options use Monday-Friday; the first week can start in the preceding
month. Monthly/custom ranges remain separate existing behaviors.

The page loads active studios, planning records, milestones, Jira and Confluence.
Independent service scopes protect scoped database services during parallel
loads. Effort is loaded separately through the existing worklog integration.
Search filters are presentation filters, not report source selection.

Support Activity Tickets and Studios > Jira Tickets offer on-demand Latest
Comments via `/SupportSummary/Comments` with an Image handler. Both pages reuse
the shared `_JiraCommentsModal` partial and `jira-comments.js`. The separate
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
Report generation opens a scrollable modal, like the Jira comments preview.
The bottom AI Reports section currently offers only Weekly Work Summary;
future report buttons can be added without modifying this workflow's contract.

Future weekly/monthly report types should have their own contracts/workflows
inside Report Generator, sharing the runtime rather than adding compatibility
branches to existing source, planning or Flow Designer implementations.

## Configuration and tests

Configuration > AI Services selects Ollama, LlamaCpp, OpenAICompatible or PFB AI Gateway. Offline-only mode
defaults to true, rejecting non-loopback inference endpoints. Provider profiles
allow model/runtime changes without feature changes. ReportGenerator and
FlowDesigner have independent disabled-by-default feature flags.
`IAiConfigurationService` supplies database-backed runtime and feature settings
to both workflows; `appsettings.json` supplies bootstrap defaults only until
settings are saved. Support Summary reads report enablement asynchronously from
the same service. Each report snapshots its source/output limits and prompt path.
A process-wide gate counts active reports against the latest saved concurrency
limit, without cancelling already-running reports when the limit is lowered.

Tests use xUnit/VSTest and FluentAssertions. `tests/TeamHub.Tests` covers web,
Studio, authentication, workflows and milestones. AI runtime, report generation
and AI Flow Designer have separate test projects. Run focused report/page/access
tests plus shared AI/Flow Designer regression tests when changing this integration.
See `docs/AI_ARCHITECTURE.md` and `docs/AI_GENERATED_REPORTS.md` for AI decisions.
