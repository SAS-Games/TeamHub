# Work Center Workflow Lifecycle Design

Status: Living design and implementation reference  
Scope: Work Center workflow authoring, templates, publishing, availability, starting, execution, completion, cancellation, and history

Implementation snapshot (2026-10-02):

| Area | Current state |
|---|---|
| Admin publication | Implemented: Workflow Library provides Publish & make available and redirects to Start New with the workflow selected |
| Start availability | Implemented: published workflows can pause or resume new starts without affecting existing runs |
| Runtime lists | Implemented: Active contains only In Progress; recent Completed and Cancelled are separate |
| Terminal tasks | Implemented: remaining optional tasks are audited as Skipped when final required work completes |
| Multi-assignee tasks | Implemented: one or more email assignees may be selected or entered manually; every assignee must complete their part, and reminders target only outstanding assignees |
| Templates | Existing blueprint reuse retained; template preview, placeholder checks, and new-key UX remain future work |
| Lifecycle hardening | Retirement, version comparison, start idempotency, transactional concurrency, and complete lifecycle auditing remain future work |

## 1. Objective

Work Center must provide one clear lifecycle:

1. An administrator creates or reuses a workflow design.
2. The administrator configures and validates it.
3. The administrator optionally saves it as a reusable template.
4. The administrator publishes it and makes it available to start.
5. A user starts a workflow run from the published definition.
6. Users complete assigned tasks.
7. The run leaves Active Workflows when it is completed or cancelled.
8. Completed and cancelled runs remain available as history.

The UI must make it obvious what each object is, what state it is in, and what action is needed next.

## 2. The four objects that must remain separate

| Object | Purpose | Mutable? | Appears in Start New? |
|---|---|---:|---:|
| Template | Reusable blueprint for creating future drafts | Yes, through explicit template management | No |
| Draft | Editable workflow being designed | Yes | No |
| Published definition | Immutable, versioned executable workflow | No; changes create a new version | Yes, only when current and available |
| Workflow run | One execution of a published definition | Only through runtime actions | No; it appears in Active or History |

These terms must be used consistently. In particular, a published definition must not be called an active workflow because Active Workflows should mean running instances.

## 3. Implemented behavior and object boundaries

### 3.1 Why saving a workflow does not make it startable

Creating and saving a visual workflow stores an editable authoring diagram. Start New deliberately does not read authoring diagrams or templates; it reads current runtime definitions where IsActive and Enabled are both true.

The implemented administrator path is:

1. Open Work Center > Workflow Library.
2. Create a blank workflow or create one from a template.
3. Design and save it in Flow Designer.
4. Return to Workflow Library.
5. Select Publish & make available.
6. Resolve any validation errors reported on the library page.
7. After successful publication, Start New opens with the workflow selected.

The one-click action still creates a frozen publication snapshot and runs the normal publication validation internally. It removes the confusing administrator self-review journey without bypassing validation.

### 3.2 What a template does

Save as template stores a reusable Flow Designer blueprint. Using the template creates a new editable diagram. It does not publish or activate a workflow, and it does not make anything appear in Start New.

This distinction is intentional: a template is reusable source material, not an executable runtime definition.

### 3.3 Active, completed, and cancelled runs

Overview now queries In Progress, Completed, and Cancelled runs separately. Active Workflows contains only In Progress runs; recent terminal runs appear in their own history sections.

My Tasks also requires the parent workflow to be In Progress. When the final required task completes, remaining optional tasks are atomically marked Skipped with audit events before the workflow becomes Completed.

## 4. Target lifecycle

~~~mermaid
flowchart LR
    A[Create blank workflow] --> D[Editable draft]
    B[Create from template] --> D
    C[Create new version] --> D
    D --> V{Validation passes?}
    V -->|No| D
    V -->|Yes| P[Publish version]
    D --> T[Save as template]
    T --> D
    P --> Q{Available to start?}
    Q -->|Yes| S[Shown in Start New]
    Q -->|No| H[Published but paused]
    H -->|Resume| S
    S --> R[Start workflow run]
    R --> I[In progress]
    I -->|Completion rules satisfied| X[Completed history]
    I -->|Admin cancels| Y[Cancelled history]
    S -->|New version published| U[Previous version superseded]
    U --> Z[Existing runs continue on original version]
~~~

## 5. Definition lifecycle

A workflow definition has a design lifecycle and an availability lifecycle.

### 5.1 Design states

- Draft: editable and not executable.
- In review: optional state for non-admin authors; the submitted snapshot is frozen.
- Published: immutable runtime version created after validation.
- Superseded: an older published version after a newer version of the same workflow key is published.
- Retired: intentionally unavailable for future starts and retained for history.

### 5.2 Availability states

Availability is operational and should be separate from design versioning:

- Available: current published version appears in Start New.
- Paused: current published version is hidden from Start New; existing runs continue.
- Retired: hidden from Start New permanently unless restored by an explicit admin action.

Pausing or resuming should not create a new content version. Changing tasks, dependencies, owners, or timing must create a new version.

### 5.3 Workflow key rules

The workflow key is the stable identity across versions.

- Keys are required, normalized, and unique case-insensitively.
- Publishing a draft with the same key means Create new version of this workflow.
- Publishing an unrelated draft with an existing key must require explicit confirmation.
- Creating from a reusable template must generate or request a new key by default.
- Duplicate workflow names may be allowed, but the UI must warn because names are user-facing.
- Existing runs remain linked to the exact definition version with which they started.

## 6. Workflow run lifecycle

~~~mermaid
stateDiagram-v2
    [*] --> InProgress: Start
    InProgress --> Completed: Completion rules satisfied
    InProgress --> Cancelled: Admin cancels
    Completed --> [*]
    Cancelled --> [*]
~~~

For the first implementation, keep the runtime states simple:

- In Progress
- Completed
- Cancelled

Not Started is not needed for runs created only when Start is confirmed. A future scheduling feature may introduce Scheduled as a separate state.

### 6.1 Completion rule

The first implementation uses automatic optional-task closure:

1. Every required enabled task is Completed.
2. When the last required task completes, every remaining optional Pending or In Progress task is atomically marked Skipped with an audit event.
3. The workflow is then marked Completed.

This prevents a completed workflow from leaving optional tasks in My Tasks.

If explicit optional-task completion is added later, the workflow must remain active until every optional task is Completed or explicitly Skipped. Silent orphaned tasks are never allowed.

### 6.2 Terminal run behavior

When a run becomes Completed or Cancelled:

- It is removed from the Active Workflows list immediately.
- Its active tasks disappear from My Tasks.
- It remains searchable in History.
- Its detail page remains read-only except for audit/history functions.
- Its definition version, task snapshot, comments, dates, and audit entries are retained.
- Completion or cancellation notifications are sent once.
- Repeating the terminal action is idempotent and does not duplicate audit events or notifications.


### 6.3 Multi-assignee task rule

Work Center tasks use email-only assignment and require every assignee to complete:

- A task must have one or more unique, valid email addresses.
- The editor suggests active registered-user emails and also accepts a valid email entered manually.
- Starting a workflow creates one independent runtime assignee record per email.
- Assignment and reminder notifications are sent separately to every outstanding assignee.
- Completing one person's part removes only that person's My Tasks row.
- The workflow task remains In Progress until every assignee has completed.
- Downstream tasks activate only after the final assignee completes.
- Reminder cycles exclude assignees who have already completed.
- Administrator completion targets a specific assignee and is audited separately.

## 7. Recommended Work Center navigation

### 7.1 Overview

Overview should be for runtime operations, not design configuration.

Sections:

- Summary tiles
  - Active runs
  - Overdue tasks
  - Completed this week
  - Active tasks
- Active Runs table
  - Only In Progress
  - Instance, workflow, version, progress, active task, owner, due date, overdue count
- Recent Completed table
  - Completed in a configurable recent period
- Recent Cancelled table
  - Cancelled in a configurable recent period
- Link to full History

Filters:

- Workflow
- Status
- Owner
- Started by
- Started/completed date range
- Search by instance name

### 7.2 My Tasks

My Tasks should show only tasks where:

- Task status is In Progress.
- Parent workflow run status is In Progress.
- The signed-in user's email has an outstanding assignee record, unless the viewer has admin override permission.

Actions:

- Complete
- Skip, only for optional tasks and authorized users
- View workflow
- Admin override with mandatory reason

### 7.3 Start New

Start New should list only the current published version of definitions that are Available.

Each option should show:

- Workflow name
- Version
- Short description
- Expected number of tasks
- Last published date

Empty states must explain the cause:

- No workflows have been published yet.
- Published workflows exist but are paused or retired.
- The user lacks permission to start workflows.

After a successful publication, the UI should provide a Start this workflow action that opens Start New with the workflow preselected.

### 7.4 Workflow Library

Rename the current Configuration page to Workflow Library or make Workflow Library its primary section.

Use separate tabs or clearly separated lists:

1. Drafts
2. In Review
3. Published
4. Templates
5. Retired

Do not combine these objects in one table.

Suggested draft actions:

- Continue designing
- Validate
- Publish and make available
- Save as template
- Duplicate
- Delete draft

Suggested published actions:

- Start
- Create new version
- Pause or resume starts
- Save as template
- View versions
- Retire

Suggested template actions:

- Create workflow from template
- Edit template metadata
- Replace template from a selected draft
- Duplicate template
- Retire or delete template

## 8. Administrator journey

### 8.1 Create a new workflow

1. Open Work Center > Workflow Library.
2. Select Create Workflow.
3. Choose Blank or From Template.
4. Enter a name, unique workflow key, and description.
5. Design the graph.
6. Configure every task:
   - Task name and stable task key
   - One or more assignee email addresses
   - Required or optional
   - Expected duration
   - Reminder policy
   - Escalation policy and escalation owner
7. Connect tasks and review parallel joins.
8. Save Draft.
9. Select Validate.
10. Resolve all errors and review warnings.
11. Optionally select Save as Template.
12. Select Publish and make available.
13. Confirm the version summary.
14. On success, select Start workflow or return to the library.

For administrators, publication should be direct after confirmation. If non-admin workflow authors are introduced, their action should be Submit for review, followed by an administrator approving the frozen snapshot.

### 8.2 Reuse a template

1. Select Create from Template.
2. Choose a template and preview its structure.
3. Enter a new workflow name and key.
4. Create an independent draft with fresh diagram and node identifiers.
5. Update project-specific owners, dates, descriptions, and policies.
6. Validate and publish normally.

A template is never a live workflow. Editing or deleting it must not change drafts, published versions, or runs previously created from it.

### 8.3 Create a new version

1. Open a published workflow.
2. Select Create new version.
3. Copy the latest published definition into a new draft.
4. Preserve the workflow key.
5. Edit and validate the draft.
6. Publish it.
7. Mark the new version current.
8. Mark the prior version superseded.
9. New starts use the new version.
10. Existing runs continue on their original version.

## 9. Publication validation gate

Publishing must be blocked when any error exists.

### 9.1 Graph validation

- Exactly one Start node.
- At least one End node.
- At least one executable task.
- No incoming connector to Start.
- No outgoing connector from End.
- Every enabled task is reachable from Start.
- At least one End is reachable from Start.
- No cycles or loops until loop execution is explicitly supported.
- Visual-only Section and Annotation nodes are not connected into the executable path.
- Parallel joins preserve all required upstream dependencies.
- No dangling or missing node references.

### 9.2 Task validation

- Stable task key is present and unique after normalization.
- Name is present.
- At least one assignee email is present.
- Every assignee value is a valid email address.
- Duplicate assignee emails are normalized and removed case-insensitively.
- Expected duration is zero or positive.
- Reminder and escalation values are zero or positive.
- Escalation owner is present when escalation is enabled.
- An enabled task cannot depend on a disabled task.
- Dependency keys exist and cannot reference the same task.
- At least one enabled root task can activate.
- Required/optional behavior is explicit.

### 9.3 Definition validation

- Workflow name is present.
- Workflow key is present and normalized.
- The key collision intent is explicit: new workflow or new version.
- At least one enabled task exists.
- Available to start cannot be enabled until the definition is successfully published.
- Publishing unchanged content reports No changes and does not create a redundant version.

### 9.4 Warnings requiring confirmation

Warnings do not necessarily block publication, but the admin must acknowledge them:

- Owner does not match an active user.
- Zero-hour task.
- No reminder configured.
- No escalation configured.
- Very long critical path.
- Duplicate display name.
- Optional task is on the only path to a required task.
- A template-derived placeholder value has not been changed.

## 10. Start rules and edge cases

| Scenario | Required behavior |
|---|---|
| Draft saved but not published | Not shown in Start New; library shows Draft and a Publish action |
| Publication awaiting review | Not shown in Start New; library shows In review |
| Published and available | Shown in Start New |
| Published but paused | Hidden from Start New; existing runs unaffected |
| Retired definition | Hidden from Start New; history retained |
| New version published | New starts use the new version; existing runs stay on the old version |
| Form loaded before workflow is paused | Submit fails with a friendly availability message |
| Duplicate active instance name | Block by normalized workflow key plus normalized instance name |
| Same name on completed run | Allowed, or warn according to configured naming policy |
| Concurrent duplicate starts | Enforce transactionally or with a database constraint |
| Definition has no enabled tasks | Publication blocked |
| Enabled task depends on disabled task | Publication blocked |
| Owner becomes inactive after publication | Warn at start; require reassignment or admin override based on policy |
| Notification delivery fails | Run/task transition succeeds; notification is retried from the outbox |
| Authoring draft is deleted after publication | Published definition and existing runs remain intact |
| Template is deleted | Derived drafts, published definitions, and runs remain intact |
| Published workflow is retired with active runs | Block hard deletion; allow retirement while active runs continue |
| Admin republishes identical content | No new version; show Already current |
| Two drafts use the same workflow key | Require explicit new-version intent and show the current published workflow |
| Browser resubmits Start | Use idempotency token to prevent duplicate runs |

## 11. Task execution edge cases

| Scenario | Required behavior |
|---|---|
| Two assignees complete the same task concurrently | Each completion is recorded independently; the task transitions once when the final outstanding assignee completes |
| User completes someone else's task | Denied unless admin override is authorized |
| Admin override | Mandatory reason stored in audit log |
| Dependency task completes | Activate downstream task only when all dependencies are terminal-successful |
| Required task is skipped | Denied except an explicit admin exception policy |
| Optional task is skipped | Mark Skipped and reevaluate downstream activation and workflow completion |
| Parent workflow is completed/cancelled | No task remains actionable |
| Task completion notification fails | Completion remains committed; notification retries separately |
| Active definition changes during a run | No effect; run uses its snapshot/version |
| Final required task completes while optional task remains | Remaining optional tasks are atomically marked Skipped with audit events, then the run completes |
| Cancellation races with completion | One terminal status wins transactionally; audit records the accepted action |
| No due date can be calculated | Show No due date; do not treat as overdue |
| Reminder worker runs twice | Notification idempotency prevents duplicate sends for the same threshold |

## 12. Template design

Templates should help administrators reuse structure without confusing them with executable workflows.

A template should contain:

- Diagram structure
- Task names and task keys
- Dependencies
- Required/optional flags
- Default durations
- Default reminder/escalation policy
- Optional owner placeholders
- Guidance notes

A template should not carry into a new independent workflow:

- Published status
- Availability state
- Runtime definition version
- Existing workflow key, unless explicitly creating a new version
- Workflow runs
- Task completion data
- Audit history
- Publication request state

Template versioning should be independent from published workflow versioning.

## 13. Permission model

| Capability | Admin | Workflow participant |
|---|---:|---:|
| Create/edit Work Center draft | Yes | No by default |
| Save/manage templates | Yes | No |
| Publish/pause/resume/retire definition | Yes | No |
| Start a workflow | Configurable; yes by default | Configurable |
| View active runs | Yes | According to Work Center access |
| Complete assigned task | Yes with override reason, or as owner | Yes, as owner |
| Cancel run | Yes | No |
| View completed/cancelled history | Yes | According to Work Center access |

If non-admin authors are later enabled, they may submit for review but cannot directly publish or change availability.

## 14. Audit requirements

Record immutable audit events for:

- Draft created, duplicated, and deleted
- Template created, updated, retired, and deleted
- Publication submitted, approved, rejected, and withdrawn
- Definition published, paused, resumed, superseded, and retired
- Workflow run started, completed, and cancelled
- Task activated, completed, skipped, reassigned, and admin-overridden
- Reminder and escalation queued
- Notification permanently failed

Each event should include actor, UTC timestamp, relevant object IDs, version, and a concise reason or payload.

## 15. Proposed implementation sequence

### Phase 1: Fix terminology and runtime lists

- Rename the definition table from Active Workflows to Published Workflows.
- Filter Overview Active Runs to In Progress.
- Add Completed and Cancelled history sections or tabs.
- Filter My Tasks by parent run status.
- Add empty-state explanations.

### Phase 2: Make publication discoverable

- Show Draft, In review, Published/Available, Published/Paused, Superseded, and Retired badges.
- Add a primary Publish and make available action for admins.
- Add a post-publication Start workflow action.
- Show validation errors directly beside the draft.
- Remove the administrator self-review requirement for admin-owned Work Center workflows, while retaining review for future non-admin authors.

### Phase 3: Separate operational availability

- Add Pause starts, Resume starts, and Retire actions.
- Keep availability outside the content hash/version.
- Make Start New query the single current, available version.
- Add friendly stale-form and no-workflow messages.

### Phase 4: Complete template/version UX

- Create blank/from-template/new-version entry choices.
- Generate a new key for independent template use.
- Add template preview and placeholder warnings.
- Add version history and comparisons.

### Phase 5: Harden runtime transitions

- Preserve the selected automatic optional-task closure policy with transition and concurrency tests.
- Close or explicitly skip remaining optional tasks.
- Add transactional duplicate-start protection.
- Add idempotency for start and terminal actions.
- Add concurrency tests and audit/notification assertions.

## 16. Acceptance criteria

The redesign is complete when all of the following are true:

1. An admin can create, validate, publish, and start a workflow without leaving an unexplained state.
2. Every draft shows exactly why it is or is not startable.
3. Publishing an available workflow makes it appear in Start New immediately.
4. A template never appears in Start New by itself.
5. Creating from a template produces an independent draft and a new key by default.
6. Publishing a new version does not alter existing runs.
7. Pausing or retiring prevents new starts without disrupting existing runs.
8. Active Workflows contains only In Progress runs.
9. Completed and cancelled runs appear only in history-oriented views.
10. My Tasks never shows a task from a completed or cancelled run.
11. Completing a run leaves no actionable pending or in-progress tasks.
12. Duplicate starts and repeated completion requests are safe under concurrency.
13. Every lifecycle transition has an audit event.
14. Automated tests cover all state transitions and the edge cases listed above.

## 17. Current operator guidance

To create and start a visual Work Center workflow:

1. Open Work Center > Workflow Library as an administrator.
2. Select Create Workflow, or choose a stored template.
3. In Flow Designer, add exactly one Start, at least one Task, and at least one End.
4. Configure an assignee and execution settings for every task.
5. Set a stable workflow key in Diagram settings and save the diagram.
6. Return to Workflow Library.
7. Select Publish & make available.
8. Fix any validation errors shown by the library and publish again.
9. On success, Start New opens with the published workflow selected.
10. Enter an instance name and select Start Workflow.

To stop future runs without disrupting work already in progress, use Pause starts in Published Workflows. Use Resume starts to show it in Start New again.

Saving as a template is optional. A template must still be instantiated, updated, saved, and published before it can be started.
