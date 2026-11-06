# Shared helpers for the build scripts. Paths are resolved from this file's location,
# so the scripts work from any current directory.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:Solution = Join-Path $RepoRoot 'BridgeTransfer.sln'
$script:AppProject = Join-Path $RepoRoot 'src/BridgeTransfer.App/BridgeTransfer.App.csproj'

# A plain (non-advanced) function, so arguments such as -p:Name=Value pass through untouched.
function Invoke-Checked {
    $file = $args[0]
    $rest = @($args | Select-Object -Skip 1)
    Write-Host "> $file $($rest -join ' ')" -ForegroundColor DarkGray
    & $file @rest
    if ($LASTEXITCODE -ne 0) {
        throw "$file exited with code $LASTEXITCODE"
    }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
