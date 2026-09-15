# Workflow System POC (Local, Configuration-Driven)

This solution is a local Windows-first POC for a generic workflow automation platform.

## Stack

- .NET 10 LTS (SDK 10.0.400 or a later 10.0.4xx patch)
- ASP.NET Core Razor Pages
- EF Core + SQLite
- ClosedXML for Excel-based workflow configuration import
- BackgroundService scheduler for reminders

## Project Layout

- src/TeamHub.Domain: Entities and enums
- src/TeamHub.Application: Service interfaces and DTOs
- src/TeamHub.Infrastructure: EF Core, Excel provider, engine, configuration sync, reminders, notifications
- src/TeamHub.Team: Database-backed team directory and support specialization configuration
- src/TeamHub.Web: Local web app UI
- src/TeamHub.FlowDesigner.Core: Flow diagram contracts and domain models
- src/TeamHub.FlowDesigner: Flow persistence, validation, and application services
- src/TeamHub.FlowDesigner.Web: Reusable Razor Class Library UI and API endpoints
- tests/TeamHub.Tests: Unit tests for engine and validation
- tests/TeamHub.FlowDesigner.Tests: Unit tests for flow behavior and access control

## Run

Install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) before building. `global.json` selects the stable 10.0.4xx SDK series. The solution targets `net10.0` and uses EF Core / Microsoft.Extensions 10.0.11.

When upgrading an existing local checkout from .NET 8, stop the app and back up `src/TeamHub.Web/bin/Debug/net8.0/data` before the first .NET 10 run. Copy its database files into `src/TeamHub.Web/bin/Debug/net10.0/data` without overwriting existing data (use `Release` instead of `Debug` for a release build). For a published deployment, back up and preserve the existing deployment's `data` folder when replacing application files.

1. Place the workflow import workbook at `config/Workflows.xlsx` (not included), or update its path in `src/TeamHub.Web/appsettings.json`.
2. Ensure Workflows.xlsx contains structured tables named Workflows and WorkflowSteps
3. Run:

```powershell
dotnet run --project src/TeamHub.Web/TeamHub.Web.csproj
```

4. Open: http://localhost:5051
5. Go to Configuration -> Sync Configuration
6. Start workflow instances from Start Workflow
7. Complete tasks from My Tasks

## Configuration file paths

All file paths in `src/TeamHub.Web/appsettings.json` use portable `config/...` paths. Paths resolve to the nearest `config` directory at or above the application's content root, so local runs use the repository-level `config` directory directly. Absolute overrides are still supported. This resolution is shared by Home, workflow imports, and milestones.

Repository configuration files are not copied into `bin`; changes are read from the repository-level `config` directory. Database files continue to live in the application's `data` directory.

## Home content and page background

Home text, the banner image URL, and links are stored on the server in `config/Home/project-info.json` and `config/Home/useful-links.json`. Clearing browser storage does not remove this configuration.

After editing these repository files, refresh the page; Home content is read on each request. In a deployed app, place the files in a `config/Home` directory at or above the application's content root.

`HomeConfiguration` paths in `appsettings.json` may be absolute or relative. Relative `config/...` paths use the nearest containing configuration directory. Missing configuration produces an explicit server error instead of silently displaying a blank home page.

Set `--page-bg-color` at the top of `src/TeamHub.Web/wwwroot/css/site.css` to change the shared page background. The Home banner image remains configured by `backgroundImage` in `project-info.json`.

## Flow Designer

After signing in, open the **Flow Designer** tab in the normal TeamHub navigation. It is part of the same authenticated application and uses the existing TeamHub user/admin roles.

- A regular user can create, view, edit, duplicate, and delete only their own diagrams.
- An administrator can view and manage every user's diagrams.
- Owners submit a saved diagram with **Request publication**. A request made from a child resolves its single root parent and freezes the entire recursive parent/child hierarchy.
- Only root diagrams appear in the authoring and published catalogs. Child diagrams are reached by drilling down from their parent.
- Only Admin users can open the publication review queue, traverse the frozen hierarchy, and approve or reject it.
- Approval writes the complete hierarchy as one immutable, versioned bundle in the `PublishedDiagrams` table of `data/flow-library.db`. The catalog reads only the current root bundle.
- Admins can edit a live published parent or child. Each save creates a new immutable version of the complete published hierarchy; it does not silently overwrite the owner's authoring draft.
- Publication requests and their original snapshots remain in `data/flowdesigner.db`, providing an independent recovery copy of approved content.
- If the `PublishedDiagrams` table is empty, startup reconstructs approved bundle history-including descendants-from the retained authoring snapshots after verifying their SHA-256 hashes.
- Collaboration comments and author identity are omitted from published snapshots. Comment authors can edit or delete only their own comments.
- `flowdesigner.db` stores authoring diagrams and publication requests. `flow-library.db` stores reusable templates, deleted-template markers, and published diagram bundles; the existing workflow database is unchanged.
- `flow-library.db` is tracked as the repository baseline. Runtime database changes are not committed automatically, so production deployments still need scheduled, versioned off-host backups. Avoid merging independently modified SQLite binaries.
- The host port is `5051` for HTTP (`7288` for the HTTPS launch profile).

Built-in templates have reproducible source definitions. Their active or
deleted catalog state is stored in `src/TeamHub.Web/data/flow-library.db`, so an
admin-deleted built-in template is not silently recreated at startup. A hierarchy
template creates its master diagram and linked detail diagrams with fresh IDs;
only the master appears in the Flow Designer catalog.

## Administration

Administrators have a single **Configuration** navigation tab that links to the Team, Studio, and Workflow configuration pages. Team Configuration contains forms for team members, support specializations, achievements, and text appearance. Each person can be assigned to either the Team Members or Management section. The Text Appearance panel controls bold and italic styling per column for Team Directory, Achievements, and Responsibility Matrix.

Custom Team tables can use manual entry, an anonymous directly downloadable `.xlsx` URL, a SharePoint/OneDrive workbook through Microsoft Graph, or a workbook uploaded from the administrator's computer. Synchronization matches records by the configured primary key rather than row position, keeps Excel columns read-only, preserves editable Team Hub columns, and marks missing source records for reviewed removal. Browser uploads are copied to the private `data/team-excel` directory rather than storing an inaccessible client-side file path.

For SharePoint/OneDrive synchronization, configure the Team Hub Microsoft Entra application through environment variables:

- `TeamExcel__MicrosoftGraph__TenantId`
- `TeamExcel__MicrosoftGraph__ClientId`
- `TeamExcel__MicrosoftGraph__ClientSecret`

Grant the application read access only to the approved SharePoint site or workbook, then enter the workbook's stable Microsoft Graph drive ID and item ID in Team configuration. Do not commit the client secret. Imported workbook data is stored in the Team Hub database and is governed by the custom tab's existing View/Edit access assignments; new tab access should be assigned explicitly before exposing restricted source data.

Access Management also supports per-user overrides for custom Team tabs. An administrator can assign No Access, Read Only, or Edit to an individual authorized user by email. An explicit user setting takes precedence over that user's category setting; choosing **Use user type setting** removes the override and returns to inheritance. Custom tab categories default to No Access, while administrators always retain Full Access.

The Admin Portal also contains **Authorized Users** and **Access Management**:

- The Authorized User List is the source of truth for TeamHub access. Admins can add, update, activate/deactivate, or remove Registered, Privileged, and Admin users.
- Only active, pre-authorized Registered users can complete self-registration. Privileged and Admin users can be provisioned with a temporary password or recognized through a configured organization authentication provider.
- Access Management assigns No Access, Read Only, Create, Edit, Delete, or Full Access to each supported module for Guest, Registered, Privileged, and Admin users. These permissions are enforced in both the UI and request pipeline.
- New Razor Page areas are discovered automatically at startup using their first folder or page-name segment. Missing permission rows are created with No Access for Guest, Registered, and Privileged users and Full Access for Admin, then shown automatically in Access Management.
- Existing `WorkflowUsers` settings are imported once as bootstrap users for backward compatibility. Subsequent user and permission changes are stored in `src/TeamHub.Web/data/access.db`.

TeamHub can map an externally authenticated organization identity to an authorized user when its name identifier or user name matches the Authorized User List. The deployment must still configure the desired SSO or Windows authentication provider; organization membership alone does not grant access.

Team data is stored in `data/team.db`. The Team pages no longer read `TeamInfo.xlsx`, so the application is unaffected when that spreadsheet is open, locked, empty, or absent.

Studio Configuration stores each studio's time zone and an ordered list of internal contacts with Name and Role fields. The Studio dashboard shows a live local clock and the internal contacts in a responsive right-hand sidebar. Existing studio records default to UTC until another time zone is selected.

Jira and Confluence are configured from **Configuration → Jira & Confluence**. Admins manage the shared server URLs and API paths, per-studio Jira project/component mappings and Confluence table identifiers, one global Confluence Activities/year/month/weekly-page hierarchy, and optional default Bearer tokens; no API tokens are stored in JSON or application settings. The Studio Support area contains Jira Tickets and read-only Weekly Updates pages. Weekly Updates defaults to the current Monday–Friday page and can read each week touched by a selected date range, returning only the configured studio row. Jira component matching is exact apart from capitalization, so separators must match. Admins can grant active Privileged users Jira and Confluence read-only access through those encrypted default credentials. Other authorized users open **My Atlassian Connection** and supply their own Bearer API tokens. Personal and default tokens are encrypted with ASP.NET Core Data Protection before storage in `data/studio.db` and are never displayed after saving. Requests using shared credentials are explicitly read-only. Keep the `data/protection-keys` directory private and back it up with the database because stored tokens cannot be decrypted without those keys.

Access Management uses cumulative levels: No Access, Read Only, Create, Edit, Delete, and Full Access. Studio Configuration uses explicit Create, Edit, and Delete handlers, hides actions the current user cannot perform, and enforces the same checks in middleware. Jira Tickets and Weekly Updates share the **Studio Support** permission. Existing **Studio Jira Tickets** permission records are migrated automatically without changing their configured levels.

## Excel Contract

Workflows table columns:

- WorkflowKey
- WorkflowName
- Description
- Enabled

WorkflowSteps table columns:

- WorkflowKey
- StepKey
- StepName
- Description
- OwnerType
- Owner
- ExpectedDurationHours
- DependsOn
- ReminderAfterHours
- ReminderRepeatHours
- EscalationAfterHours
- EscalationOwner
- Required
- Enabled
- SortOrder

## Current POC Behavior

- Imports and validates workflow definitions from Excel
- Versions definitions using deterministic configuration hash
- Starts workflow instances using the latest version
- Snapshots all enabled steps into runtime instances
- Activates root steps automatically
- Activates dependent steps when dependencies are complete
- Enforces owner/admin completion rule
- Calculates due date from expected duration
- Logs notifications and audit events in SQLite
- Runs periodic reminder cycle via background service
- Keeps active instances on original version when new config versions are imported

## Notes

- Notification delivery is currently logged to DB and app logs via DatabaseNotificationService.
- SQLite DB is created on app startup using EnsureCreated.
- POC auth is simplified; owner is provided in My Tasks page for demonstration.
- Flow Designer vendors Drawflow and Bootstrap under their MIT licenses; license texts are kept beside the distributed assets.
