<#
.SYNOPSIS
  Builds, tests and publishes the self-contained single-file Windows x64 executable to dist\BridgeTransfer.exe.
.DESCRIPTION
  1. Removes any previous dist\BridgeTransfer.exe so a failed run never leaves a stale executable.
  2. Builds and runs the tests (skip with -SkipTests).
  3. Publishes Release, win-x64, self-contained, single file, no trimming.
  4. Validates the output (exactly one executable, expected size, PE GUI subsystem, no stray files).
  5. Copies the executable to dist\ and writes dist\BridgeTransfer.exe.sha256.
#>
[CmdletBinding()]
param([switch]$SkipTests)
$Runtime = 'win-x64'
. (Join-Path $PSScriptRoot 'common.ps1')
$dist = Join-Path $RepoRoot 'dist'
$distExe = Join-Path $dist 'BridgeTransfer.exe'
$distSha = "$distExe.sha256"
$publishDir = Join-Path $RepoRoot "artifacts/publish/$Runtime"
try {
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    Remove-Item -Force -ErrorAction SilentlyContinue $distExe, $distSha
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

    & (Join-Path $PSScriptRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    if (-not $SkipTests) {
        & (Join-Path $PSScriptRoot 'test.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
    }

    # The project file pins RuntimeIdentifier=win-x64, SelfContained, PublishSingleFile,
    # IncludeNativeLibrariesForSelfExtract, PublishTrimmed=false and DebugType=embedded.
    # They are not repeated here so the committed lock files stay valid in locked mode.
    Invoke-Checked dotnet publish $AppProject -c Release --output $publishDir

    # ---- Validate the published output ----
    $files = Get-ChildItem -Path $publishDir -File
    $unexpected = $files | Where-Object { $_.Name -ne 'BridgeTransfer.exe' }
    if ($unexpected) { throw "Unexpected files in the publish output (not a single-file build?): $($unexpected.Name -join ', ')" }
    $exe = Join-Path $publishDir 'BridgeTransfer.exe'
    if (-not (Test-Path $exe)) { throw 'BridgeTransfer.exe was not produced.' }
    $size = (Get-Item $exe).Length
    if ($size -lt 60MB) { throw "BridgeTransfer.exe is only $size bytes; a self-contained WPF build should be much larger." }

    $bytes = [System.IO.File]::ReadAllBytes($exe)
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    $subsystem = [BitConverter]::ToUInt16($bytes, $pe + 24 + 68)
    if ($subsystem -ne 2) { throw "Executable subsystem is $subsystem; expected 2 (Windows GUI, no console window)." }
    $text = [System.Text.Encoding]::ASCII.GetString($bytes)
    foreach ($forbidden in @('b3BlbnNzaC1rZXktdjE', 'BT_IT_PASSWORD', 'BT_IT_KEY')) {
        if ($text.Contains($forbidden)) { throw "The executable contains '$forbidden'; refusing to package test credentials." }
    }

    Copy-Item $exe $distExe -Force
    $hash = (Get-FileHash -Algorithm SHA256 $distExe).Hash.ToLowerInvariant()
    "$hash  BridgeTransfer.exe" | Set-Content -NoNewline -Encoding ascii $distSha
    $version = (Select-Xml -Path (Join-Path $RepoRoot 'Directory.Build.props') -XPath '//Version').Node.InnerText
    Write-Host ''
    Write-Host "Published: $distExe" -ForegroundColor Green
    Write-Host "  Version: $version"
    Write-Host "  Size:    $size bytes"
    Write-Host "  SHA-256: $hash"
    exit 0
}
catch {
    Remove-Item -Force -ErrorAction SilentlyContinue $distExe, $distSha
    Write-Host "Publish failed: $_" -ForegroundColor Red
    exit 1
}
