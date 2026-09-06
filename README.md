# Workflow System POC (Local, Configuration-Driven)

This solution is a local Windows-first POC for a generic workflow automation platform.

## Stack

- .NET 8
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

1. Update Excel path in src/TeamHub.Web/appsettings.json
2. Ensure Workflows.xlsx contains structured tables named Workflows and WorkflowSteps
3. Run:

```powershell
dotnet run --project src/TeamHub.Web/TeamHub.Web.csproj
```

4. Open: http://localhost:5051
5. Go to Configuration -> Sync Configuration
6. Start workflow instances from Start Workflow
7. Complete tasks from My Tasks

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

Administrators have a single **Configuration** navigation tab that links to the Team, Studio, and Workflow configuration pages. Team Configuration contains separate forms for team members and support specializations.

Team data is stored in `data/team.db`. The Team pages no longer read `TeamInfo.xlsx`, so the application is unaffected when that spreadsheet is open, locked, empty, or absent.

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
