<#
.SYNOPSIS
  Finds the global development code-signing certificate on this PC, creating it if needed. Outputs its thumbprint.

.DESCRIPTION
  One self-signed certificate shared by all projects, so run this from any of them. It lives with its private key in
  your user certificate store (Cert:\CurrentUser\My). The key cannot be exported and is not backed up; on a new PC a
  new certificate is created and has to be trusted again.

  The public part is exported to artifacts\signing\Saraf-code-signing.cer together with install-certificate.ps1,
  which you run on machines that should trust the signature.
#>
param(
    [string]$Subject = 'CN=Saraf Code Signing',
    [int]$Years = 5
)

$ErrorActionPreference = 'Stop'
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

$out = Join-Path $PSScriptRoot '..\artifacts\signing'
New-Item -ItemType Directory -Force $out | Out-Null
$cer = Join-Path $out 'Saraf-code-signing.cer'

if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears($Years) `
        -KeyExportPolicy NonExportable
    Write-Host "Created $Subject ($($cert.Thumbprint))"
}

Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'install-certificate.ps1') $out -Force
$cert.Thumbprint
