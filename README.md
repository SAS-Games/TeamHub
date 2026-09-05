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
- src/TeamHub.Web: Local web app UI
- tests/TeamHub.Tests: Unit tests for engine and validation

## Run

1. Update Excel path in src/TeamHub.Web/appsettings.json
2. Ensure Workflows.xlsx contains structured tables named Workflows and WorkflowSteps
3. Run:

```powershell
dotnet run --project src/TeamHub.Web/TeamHub.Web.csproj
```

4. Open: http://localhost:5000
5. Go to Configuration -> Sync Configuration
6. Start workflow instances from Start Workflow
7. Complete tasks from My Tasks

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
