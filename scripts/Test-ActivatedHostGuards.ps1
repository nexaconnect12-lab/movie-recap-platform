# Operational checks against the running local hosts. No process is stopped.
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$passed = 0
function MustReject([scriptblock]$action, [string]$expected) {
    $rejected = $false
    try { & $action | Out-Null }
    catch { if ($_.Exception.Message -notlike "*$expected*") { throw }; $rejected=$true }
    if (-not $rejected) { throw 'Expected identity guard rejection.' }
}
foreach ($kind in @('Api','Worker')) {
    $path = Join-Path $workspace ".local/runtime/$($kind.ToLowerInvariant()).process.json"
    $bytes = [IO.File]::ReadAllBytes($path)
    $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $process = Get-Process -Id $state.id
    if ($process.Path -ne $state.executable) { throw 'Host not available for guard tests.' }
    MustReject { & "$PSScriptRoot/Start-ActivatedHosts.ps1" -HostKind $kind } 'already tracked as running'
    $passed++
    try {
        $state.startedUtc = ([DateTime]$state.startedUtc).AddSeconds(-1).ToString('o')
        $state | ConvertTo-Json | Set-Content -LiteralPath $path
        MustReject { & "$PSScriptRoot/Stop-ActivatedHosts.ps1" -HostKind $kind } 'changed identity'
        $passed++
        [IO.File]::WriteAllBytes($path, $bytes)
        $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $state.executable = Join-Path $workspace '.local/runtime/releases/unowned/Unexpected.exe'
        $state | ConvertTo-Json | Set-Content -LiteralPath $path
        MustReject { & "$PSScriptRoot/Stop-ActivatedHosts.ps1" -HostKind $kind } 'changed identity'
        $passed++
    }
    finally { [IO.File]::WriteAllBytes($path, $bytes) }
    if ((Get-Process -Id $process.Id).StartTime -ne $process.StartTime) { throw 'Host identity changed during guard tests.' }
}
Write-Output "Host guard checks: $passed passed, 0 failed; original tracked hosts remain running."
