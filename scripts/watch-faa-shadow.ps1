[CmdletBinding()]
param(
    [string]$Document = (Join-Path $HOME 'Downloads\FAA_Ground_Stop_Nov_13_2026_BusStop_Sample.txt'),
    [string]$ParticipantModel = 'qwen3.5:9b',
    [string]$ModeratorModel = 'qwen3.5:9b',
    [int]$Opportunities = 90,
    [int]$ParticipantCalls = 180,
    [int]$EndAfterPasses = 9,
    [int]$ModeratorCalls = 96,
    [int]$SampleEvery = 3
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath $Document -PathType Leaf)) {
    throw "FAA source file not found: $Document. Pass -Document with the actual .txt path."
}
$out = Join-Path $HOME ('Documents\BusStop-Shadow-FAA-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $out | Out-Null
Push-Location $root
try {
    Write-Host 'Building the local M1 shadow runner in Release mode...'
    dotnet build .\BusStop.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed; no model run started.' }

    Write-Host ''
    Write-Host 'LIVE BUS STOP — Arthur, Maya, Daniel + invisible shadow moderator'
    Write-Host 'Shadow observations NEVER change the characters or stop the conversation.'
    Write-Host "Run artifacts: $out"
    Write-Host ''

    dotnet run --project .\src\BusStop.Console --configuration Release --no-build -- `
        --model ollama --ollama-model $ParticipantModel --no-think `
        --ollama-timeout 360 `
        --discuss-document $Document --out $out `
        --max-opportunities $Opportunities --max-model-calls $ParticipantCalls `
        --end-after-passes $EndAfterPasses `
        --shadow-moderator --moderator-model $ModeratorModel `
        --moderator-budget $ModeratorCalls `
        --moderator-sample-every $SampleEvery --moderator-timeout 180

    if ($LASTEXITCODE -ne 0) { throw 'FAA shadow run failed; inspect console output.' }

    Write-Host ''
    Write-Host 'Run complete. Private moderator reviews are in shadow-<session>.json.'
    Write-Host "Transcript: $out\transcript.md"
    Write-Host "Diagnostics: $out\diagnostics.json"
    Write-Host "Audit directory: $out"
    Write-Host 'Note: private SQLite/database and shadow JSON may contain the full source or private candidate text.'
}
finally {
    Pop-Location
}
