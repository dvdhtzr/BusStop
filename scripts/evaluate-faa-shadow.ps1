# BusStop FAA moderator evaluation, read-only
[CmdletBinding()]
param(
    [string]$Audit = (Join-Path $HOME 'Documents\BusStop-Shadow-FAA-20261115-174513\shadow-32da92b9-bf9c-4216-8789-962ea6075fe9.json')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Audit -PathType Leaf)) { throw "Private FAA shadow audit not found: $Audit" }
dotnet run --project .\src\BusStop.Console --configuration Release -- --evaluate-shadow-audit $Audit
if ($LASTEXITCODE -ne 0) { throw 'Read-only FAA corpus evaluation failed.' }
