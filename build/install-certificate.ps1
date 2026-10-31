<#
.SYNOPSIS
  Trusts the AppDock self-signed code-signing certificate on this machine (run as administrator).

.EXAMPLE
  .\install-certificate.ps1 .\AppDock-signing.cer
#>
param([Parameter(Mandatory)][string]$CertificatePath)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script as administrator.'
}

# Root: the self-signed certificate is its own issuer. TrustedPublisher: Windows trusts what it signs.
Import-Certificate -FilePath $CertificatePath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $CertificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
Write-Host 'AppDock signing certificate installed.'
