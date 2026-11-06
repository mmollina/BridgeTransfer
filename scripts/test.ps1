<#
.SYNOPSIS
  Runs the unit tests, and the SFTP integration tests when a fixture is available.
.DESCRIPTION
  Integration tests need an isolated OpenSSH SFTP server. On a disposable Linux machine or
  container run tests/fixture/start-sftp-fixture.sh (as root), or point BT_IT_FIXTURE at an
  env file with the same keys. Without a fixture the integration tests are reported as skipped.
  Set BT_IT_LARGE=1 to include the file-larger-than-4-GB test.
#>
[CmdletBinding()]
param([string]$Configuration = 'Release', [switch]$UnitOnly)
. (Join-Path $PSScriptRoot 'common.ps1')
try {
    $results = Join-Path $RepoRoot 'artifacts/test-results'
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    Invoke-Checked dotnet test (Join-Path $RepoRoot 'tests/BridgeTransfer.UnitTests/BridgeTransfer.UnitTests.csproj') -c $Configuration `
        --logger "trx;LogFileName=unit.trx" --results-directory $results
    if (-not $UnitOnly) {
        Invoke-Checked dotnet test (Join-Path $RepoRoot 'tests/BridgeTransfer.IntegrationTests/BridgeTransfer.IntegrationTests.csproj') -c $Configuration `
            --logger "trx;LogFileName=integration.trx" --logger "console;verbosity=normal" --results-directory $results
    }
    Write-Host 'Tests passed.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "Tests failed: $_" -ForegroundColor Red
    exit 1
}
