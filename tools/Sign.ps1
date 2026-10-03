[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Thumbprint, [string]$TimestampServer)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $project 'bin/SafePaste.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build SafePaste.exe first.' }
$certificate = Get-ChildItem -LiteralPath ('Cert:\CurrentUser\My\' + $Thumbprint) -ErrorAction Stop
if (-not $certificate.HasPrivateKey) { throw 'Certificate has no private key.' }
$codeSigning = @($certificate.EnhancedKeyUsageList | Where-Object { $_.FriendlyName -eq 'Code Signing' })
if ($codeSigning.Count -eq 0) { throw 'Certificate is not valid for code signing.' }
$options = @{ FilePath = $exe; Certificate = $certificate; HashAlgorithm = 'SHA256' }
if ($TimestampServer) { $options.TimestampServer = $TimestampServer }
$result = Set-AuthenticodeSignature @options
if ($result.Status -ne 'Valid') { throw ('Signing failed: ' + $result.Status + ' ' + $result.StatusMessage) }
Write-Output ('Signed: ' + $exe)
