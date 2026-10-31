<#
.SYNOPSIS
  Creates a self-signed code-signing certificate for AppDock (run once on the build PC).

.DESCRIPTION
  The certificate and its private key go into your user certificate store (Cert:\CurrentUser\My).
  The public part is exported to artifacts\signing\AppDock-signing.cer together with install-certificate.ps1,
  which you run on target machines so they trust AppDock's signature. Never share or commit the private key.
#>
param(
    [string]$Subject = 'CN=Saraf AppDock Code Signing',
    [int]$Years = 5
)

$ErrorActionPreference = 'Stop'
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -KeyAlgorithm RSA -KeyLength 3072 `
    -HashAlgorithm SHA256 -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears($Years) `
    -KeyExportPolicy NonExportable

$out = Join-Path $PSScriptRoot '..\artifacts\signing'
New-Item -ItemType Directory -Force $out | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $out 'AppDock-signing.cer') | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'install-certificate.ps1') $out -Force

# Trust it on this PC too, so signatures verify here.
Import-Certificate -FilePath (Join-Path $out 'AppDock-signing.cer') -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null

Write-Host "Created $Subject"
Write-Host "Thumbprint: $($cert.Thumbprint)"
Write-Host "Build signed:   .\build\build.ps1 -CertificateThumbprint $($cert.Thumbprint)"
Write-Host "Target machines: copy $out and run install-certificate.ps1 as administrator."
