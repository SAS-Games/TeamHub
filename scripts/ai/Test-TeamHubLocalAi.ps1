#Requires -Version 5.1

[CmdletBinding()]
param(
    [string] $Endpoint = 'http://127.0.0.1:8080',
    [string] $Model = 'qwen3:8b'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$uri = $null
if (-not [Uri]::TryCreate($Endpoint.Trim(), [UriKind]::Absolute, [ref] $uri) -or
    $uri.Scheme -notin @('http', 'https')) {
    throw 'Endpoint must be an absolute HTTP or HTTPS URL.'
}
if (-not $uri.IsLoopback) {
    throw 'This local AI validation script accepts loopback endpoints only.'
}
if ([string]::IsNullOrWhiteSpace($Model)) {
    throw 'Model is required.'
}

$baseAddress = $uri.AbsoluteUri.TrimEnd('/')
$modelsEndpoint = if ($baseAddress.EndsWith('/v1', [StringComparison]::OrdinalIgnoreCase)) {
    "$baseAddress/models"
}
else {
    "$baseAddress/v1/models"
}

Write-Host "Checking $modelsEndpoint ..."
$response = Invoke-RestMethod -Uri $modelsEndpoint -Method Get -TimeoutSec 15
$models = @($response.data | ForEach-Object { $_.id })
if ($models.Count -eq 0) {
    throw 'The runtime responded but did not return any models.'
}
if ($models -notcontains $Model.Trim()) {
    throw "The runtime is available, but model '$($Model.Trim())' was not found. Available models: $($models -join ', ')"
}

Write-Host "Local AI runtime and model '$($Model.Trim())' are available." -ForegroundColor Green
