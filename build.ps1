param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
& dotnet build (Join-Path $PSScriptRoot 'BooBoopControl.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Hardware library build failed.' }
