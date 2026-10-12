# Dot-source this in every local API/worker/test terminal. DPAPI secrets stay out of source.
$ErrorActionPreference = 'Stop'
$localPath=Join-Path $PSScriptRoot '../.local/credentials.xml'
if (-not (Test-Path -LiteralPath $localPath)) { throw 'Run scripts/Initialize-Local.ps1 first.' }
$localSecrets=Import-Clixml -LiteralPath $localPath
$env:POSTGRES_PASSWORD=[System.Net.NetworkCredential]::new('', $localSecrets.Password).Password
$env:Security__ApiKey=[System.Net.NetworkCredential]::new('', $localSecrets.ApiKey).Password
$env:ConnectionStrings__Platform = "Host=localhost;Port=54329;Database=recap;Username=recap;Password=$env:POSTGRES_PASSWORD"
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5080'
$env:Logging__FilePath=Join-Path $PSScriptRoot '../.local/logs/worker-.json'

