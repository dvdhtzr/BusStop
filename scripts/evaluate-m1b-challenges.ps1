[CmdletBinding()]
param(
    [switch]$Live,
    [ValidateRange(1,36)][int]$Limit = 8,
    [ValidateRange(1,72)][int]$Budget = 72,
    [string]$Model = 'qwen3.5:9b',
    [ValidateRange(1,3600)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host 'M1b: verifying Release solution and model-free tests...'
    dotnet build .\BusStop.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'M1b Release build RED.' }
    dotnet test .\tests\BusStop.Tests\BusStop.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'M1b unit tests RED.' }
    dotnet run --project .\src\BusStop.Console --configuration Release --no-build --no-launch-profile -- --m1b-challenge-check
    if ($LASTEXITCODE -ne 0) { throw 'M1b corpus validation RED.' }
    if (-not $Live) {
        Write-Host 'GREEN: 36-case synthetic corpus verified without Ollama.'
        Write-Host 'For local model smoke: .\scripts\evaluate-m1b-challenges.ps1 -Live -Limit 8'
        return
    }
    $out = Join-Path $HOME ('Documents\BusStop-M1b-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    Write-Host "Running local M1b: $Limit cases, budget $Budget. Private report: $out"
    dotnet run --project .\src\BusStop.Console --configuration Release --no-build --no-launch-profile -- --m1b-challenge-run --moderator-model $Model --moderator-timeout $TimeoutSeconds --challenge-limit $Limit --challenge-budget $Budget --out $out
    if ($LASTEXITCODE -ne 0) { throw 'M1b local challenge runner failed.' }
}
finally {
    Pop-Location
}
