# Dependency injection

Call `AddTeamHubAi(configuration)` for the provider-neutral runtime, register the normal Flow Designer, and then call `AddAiFlowDesigner(configuration)`. The workflow is disabled by default under `AI:FlowDesigner:Enabled`. Administrators can enable it at runtime from **Configuration > AI Services**; `appsettings.json` supplies the bootstrap value until database-backed settings are saved.
