#Requires -RunAsAdministrator
param([string]$Name='MovieRecap.Processing')
$ErrorActionPreference='Stop'
$service=Get-Service -Name $Name -ErrorAction SilentlyContinue
if (-not $service) { return }
if($service.Status -ne 'Stopped') { Stop-Service -Name $Name; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(45)) }
& sc.exe delete $Name
if($LASTEXITCODE -ne 0) { throw 'Service deletion failed.' }
# Published binaries and data are retained.

