# Dependency injection

Call `AddTeamHubAi(configuration)` for the provider-neutral runtime, register the normal Flow Designer, and then call `AddAiFlowDesigner(configuration)`. The workflow is disabled by default under `AI:FlowDesigner:Enabled`. Team Hub enables it in `appsettings.Development.json` for local testing while leaving the production default off.