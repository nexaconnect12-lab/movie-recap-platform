#Requires -RunAsAdministrator
param([string]$Name='MovieRecap.Processing')
$ErrorActionPreference='Stop'
Start-Service -Name $Name
Get-Service -Name $Name

