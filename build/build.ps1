<#
.SYNOPSIS
  Full quality gate and release build for AppDock.

.DESCRIPTION
  1. Builds everything with warnings as errors.
  2. Runs unit tests (and, with -Integration, the end-to-end tests that drive real windows).
  3. Publishes a small single-file AppDock.exe that uses the shared .NET 10 Desktop Runtime (if a machine lacks
     it, Windows offers Microsoft's download on first launch).
  4. Optionally Authenticode-signs the exe and the installer.
  5. Builds the per-user MSI installer.

  Output: artifacts\AppDock-<version>-x64.msi and artifacts\publish\AppDock.exe

.EXAMPLE
  .\build\build.ps1
  .\build\build.ps1 -Integration
  .\build\build.ps1 -Version 1.1.0 -CertificateThumbprint 0123ABCD...
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [switch]$Integration,
    [switch]$SkipTests,
    # Thumbprint of a code-signing certificate in the current user's or machine's certificate store.
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

function Step([string]$name) { Write-Host "`n=== $name ===" -ForegroundColor Cyan }
function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "AppDock $Version ($Configuration)"

Step 'Build'
Run dotnet @('build', (Join-Path $root 'AppDock.slnx'), '-c', $Configuration, "-p:Version=$Version", '-warnaserror', '--nologo')

if (-not $SkipTests) {
    Step 'Unit tests'
    Run dotnet @('test', (Join-Path $root 'tests\AppDock.Core.Tests'), '-c', $Configuration, '--no-build', '--nologo')

    if ($Integration) {
        Step 'Integration tests (uses the screen for ~20 s; do not touch the mouse)'
        $env:APPDOCK_INTEGRATION = '1'
        try { Run dotnet @('test', (Join-Path $root 'tests\AppDock.IntegrationTests'), '-c', $Configuration, '--no-build', '--nologo') }
        finally { Remove-Item Env:APPDOCK_INTEGRATION }
    }
}

Step 'Publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Run dotnet @('publish', (Join-Path $root 'src\AppDock\AppDock.csproj'), '-c', $Configuration, '-r', 'win-x64',
    '--self-contained', 'false', '-p:PublishSingleFile=true', '-p:DebugType=none', "-p:Version=$Version", '-o', $publish, '--nologo')
Copy-Item (Join-Path $root 'LICENSE.txt') $publish

function Sign([string]$file) {
    if (-not $CertificateThumbprint) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK to sign.' }
    Run $signtool.FullName @('sign', '/sha1', $CertificateThumbprint, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', $file)
}

if ($CertificateThumbprint) { Step 'Sign executable'; Sign (Join-Path $publish 'AppDock.exe') }
else { Write-Warning 'No -CertificateThumbprint: binaries are unsigned (SmartScreen will warn end users).' }

Step 'Installer'
$installerOut = Join-Path $artifacts 'installer'
# --no-incremental: WiX does not treat -p:Version as an input, so an incremental build could ship a stale MSI.
Run dotnet @('build', (Join-Path $root 'installer\AppDock.Installer.wixproj'), '--no-incremental', '-c', $Configuration,
    "-p:Version=$Version", "-p:PublishDir=$publish\", '-o', $installerOut, '--nologo')
$msi = Join-Path $artifacts "AppDock-$Version-x64.msi"
Copy-Item (Join-Path $installerOut 'AppDock.msi') $msi -Force
if ($CertificateThumbprint) { Step 'Sign installer'; Sign $msi }

Step 'Done'
Get-Item (Join-Path $publish 'AppDock.exe'), $msi | Format-Table Name, @{ n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
