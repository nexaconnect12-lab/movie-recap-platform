[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { throw 'This helper uses Windows DPAPI; use environment variables on other systems.' }
$localDirectory=Join-Path $PSScriptRoot '../.local'
$localPath=Join-Path $localDirectory 'credentials.xml'
if(Test-Path -LiteralPath $localPath) { Write-Host 'Existing local credentials retained.'; return }
New-Item -ItemType Directory -Path $localDirectory -Force | Out-Null
$values=@{
    Password=ConvertTo-SecureString ([Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))) -AsPlainText -Force
    ApiKey=ConvertTo-SecureString ([Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) -AsPlainText -Force
}
$values | Export-Clixml -LiteralPath $localPath
Write-Host 'Generated local credentials encrypted for this Windows user. Do not regenerate while retaining the database volume.'

