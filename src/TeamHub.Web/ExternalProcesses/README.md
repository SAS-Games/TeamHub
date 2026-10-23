# External process integrations

This directory is the standard TeamHub boundary for independently developed
tools such as Worklog Analytics and Milestone Review Report generation.
TeamHub owns authorization, input selection, credential resolution and the web
experience. The external project owns its processing logic and can continue to
run and be developed without referencing TeamHub assemblies.

Process-specific adapters are grouped under `WorklogAnalytics` and
`MilestoneReviewReport`. Shared runner, credential and configuration
infrastructure remains in this directory.

## Required integration pattern

1. Register a stable key in `ExternalProcessKeys` and configure it under
   `ExternalProcesses:Processes`.
2. Create a small TeamHub adapter that builds a versioned input contract and
   validates the returned output contract.
3. Launch only through `IExternalProcessRunner`. Do not construct
   `ProcessStartInfo` in feature code.
4. Resolve optional credentials through
   `IExternalProcessCredentialResolver`. Never put bearer tokens in arguments,
   standard input, standard output, responses or logs.
5. Authorize again in the server handler or adapter. Hiding a button is not an
   authorization boundary.

Executable paths, working directories, entry points and fixed arguments are
deployment-controlled configuration because changing them is equivalent to
granting code-execution permission. The runner uses `ArgumentList`, disables
shell execution, redirects output, applies time and output limits, and kills the
whole child process tree after a timeout or request cancellation.

## Configuration

Each configured process supports:

- `Enabled`
- `RootPath`
- `ExecutablePath` (absolute or relative to `RootPath`)
- `EntryPointPath` (optional)
- `EntryPointBase`: `RootPath` or `ContentRoot`
- `Arguments`: fixed deployment arguments; `{RootPath}`, `{ContentRoot}` and
  `{EntryPoint}` are expanded by the runner
- `TimeoutSeconds`
- `MaximumOutputCharacters`
- `Credential`

Credential modes are `None`, `RequestingUser`, `ConfiguredUser` and
`DefaultShared`. `ConfiguredUserId` is required for `ConfiguredUser`.
`AllowDefaultFallbackForPrivileged` applies only to `RequestingUser` mode.
Tokens are read from TeamHub's protected Atlassian credential store and added
only to the configured child-process environment variable.

## Data contracts

Large or sensitive feature data should be serialized as JSON to standard input,
not command-line arguments. External tools return one JSON document through
standard output and use standard error only for diagnostic failures.

`schemaVersion` belongs to the adapter contract and is not an administrator
setting. `correlationId` is generated for every run and allows TeamHub and the
external tool to trace a request and distinguish retries.

The Milestone Review Report adapter sends schema version 1 with review details,
workflow identity and feedback. It expects:

```json
{
  "success": true,
  "reportId": "optional external identifier",
  "reportUrl": "optional HTTPS or TeamHub-relative URL",
  "message": "Report generated successfully."
}
```

Worklog Analytics also uses the shared runner. Its existing CLI date arguments,
JSON output and privileged default-credential fallback remain feature-specific
adapter behavior. The legacy `WorklogAnalytics` configuration section is still
honored as an override during migration, but new deployments should use
`ExternalProcesses:Processes:WorklogAnalytics`.
