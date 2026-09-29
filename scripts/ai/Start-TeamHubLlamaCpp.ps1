#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ServerPath,

    [Parameter(Mandatory)]
    [string] $ModelPath,

    [string] $ModelAlias = 'qwen3:8b',

    [ValidateRange(1, 65535)]
    [int] $Port = 8080,

    [ValidateRange(512, 1048576)]
    [int] $ContextSize = 16384,

    [ValidateRange(0, 999)]
    [int] $GpuLayers = 0,

    [string] $ExpectedServerSha256 = '',

    [string] $ExpectedModelSha256 = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Resolve-RequiredFile([string] $path, [string] $description) {
    $resolved = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($path))
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$description was not found: $resolved"
    }
    return $resolved
}

function Assert-Sha256([string] $path, [string] $expected, [string] $description) {
    if ([string]::IsNullOrWhiteSpace($expected)) {
        return
    }

    $normalized = $expected.Trim().Replace(' ', '').ToUpperInvariant()
    if ($normalized -notmatch '^[A-F0-9]{64}$') {
        throw "The expected SHA-256 for $description must contain exactly 64 hexadecimal characters."
    }

    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $normalized) {
        throw "$description failed SHA-256 verification. Expected $normalized but found $actual."
    }
}

if ([string]::IsNullOrWhiteSpace($ModelAlias)) {
    throw 'ModelAlias is required and must match AI:Providers:LlamaCpp:Model.'
}

$resolvedServer = Resolve-RequiredFile $ServerPath 'llama-server executable'
$resolvedModel = Resolve-RequiredFile $ModelPath 'GGUF model'
if ([IO.Path]::GetExtension($resolvedModel) -ne '.gguf') {
    throw "The llama.cpp model must be a .gguf file: $resolvedModel"
}

Assert-Sha256 $resolvedServer $ExpectedServerSha256 'llama-server executable'
Assert-Sha256 $resolvedModel $ExpectedModelSha256 'GGUF model'

$serverArguments = @(
    '--model', $resolvedModel,
    '--alias', $ModelAlias.Trim(),
    '--host', '127.0.0.1',
    '--port', $Port.ToString(),
    '--ctx-size', $ContextSize.ToString()
)
if ($GpuLayers -gt 0) {
    $serverArguments += @('--n-gpu-layers', $GpuLayers.ToString())
}

Write-Host ''
Write-Host 'Starting the Team Hub llama.cpp runtime' -ForegroundColor Green
Write-Host "Executable: $resolvedServer"
Write-Host "Model: $resolvedModel"
Write-Host "Model alias: $($ModelAlias.Trim())"
Write-Host "Endpoint: http://127.0.0.1:$Port"
Write-Host ''
Write-Host 'Keep this window open. Press Ctrl+C to stop the runtime.' -ForegroundColor Yellow

& $resolvedServer @serverArguments
if ($LASTEXITCODE -ne 0) {
    throw "llama-server exited with code $LASTEXITCODE."
}
