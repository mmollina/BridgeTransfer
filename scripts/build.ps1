<#
.SYNOPSIS
  Restores (from the committed lock files) and builds the whole solution in Release.
#>
[CmdletBinding()]
param([string]$Configuration = 'Release')
. (Join-Path $PSScriptRoot 'common.ps1')
try {
    Invoke-Checked dotnet restore $Solution --locked-mode
    Invoke-Checked dotnet build $Solution -c $Configuration --no-restore
    Write-Host 'Build succeeded.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "Build failed: $_" -ForegroundColor Red
    exit 1
}
