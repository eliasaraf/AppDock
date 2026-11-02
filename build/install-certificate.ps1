<#
.SYNOPSIS
  Trusts the self-signed development code-signing certificate on this machine (run as administrator).
  Everything signed with it, in any project, is then trusted here.

.EXAMPLE
  .\install-certificate.ps1 .\Saraf-code-signing.cer
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
Write-Host 'Code-signing certificate installed.'
