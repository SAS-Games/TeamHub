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
- src/TeamHub.FlowDesigner.Standalone: Optional standalone development host
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

All file paths in `src/TeamHub.Web/appsettings.json` use portable `config/...` paths. Paths resolve to the nearest `config` directory at or above the application's content root, so local runs use the repository-level `config` directory directly. Absolute overrides are still supported. This resolution is shared by Home, workflow imports, milestones, and Studio Jira settings.

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
- Diagram data is isolated in `data/flowdesigner.db`; the existing workflow database and workflow engine are unchanged.
- The host port is `5051` for HTTP (`7288` for the HTTPS launch profile).

For isolated Flow Designer development, run:

```powershell
dotnet run --project src/TeamHub.FlowDesigner.Standalone/TeamHub.FlowDesigner.Standalone.csproj
```

The standalone development host uses `http://localhost:5168`.

## Administration

Administrators have a single **Configuration** navigation tab that links to the Team, Studio, and Workflow configuration pages. Team Configuration contains forms for team members, support specializations, achievements, and text appearance. Each person can be assigned to either the Team Members or Management section. The Text Appearance panel controls bold and italic styling per column for Team Directory, Achievements, and Responsibility Matrix.

Team data is stored in `data/team.db`. The Team pages no longer read `TeamInfo.xlsx`, so the application is unaffected when that spreadsheet is open, locked, empty, or absent.

Studio Configuration stores each studio's time zone and an ordered list of internal contacts with Name and Role fields. The Studio dashboard shows a live local clock and the internal contacts in a responsive right-hand sidebar. Existing studio records default to UTC until another time zone is selected.

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
