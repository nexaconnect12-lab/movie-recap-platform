#Requires -RunAsAdministrator
param([string]$Name='MovieRecap.Processing')
$ErrorActionPreference='Stop'
Stop-Service -Name $Name
(Get-Service -Name $Name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(45))

