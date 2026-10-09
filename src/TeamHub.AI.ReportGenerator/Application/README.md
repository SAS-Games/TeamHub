# Application

`WeeklyWorkSummaryWorkflow` processes approved HPGDS Support and Notes source
rows independently through `IAiModelService`. It validates structured output
and protected identifiers, bounds concurrent requests, records completed-run
audit metadata and returns a draft for review. There are no automatic retries
or source-text fallbacks. The host selects and projects the source data before
calling this workflow.
