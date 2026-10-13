param([ValidateSet('Both','Api','Worker')][string]$HostKind = 'Both')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
foreach ($kind in @('Api','Worker')) {
    if ($HostKind -ne 'Both' -and $HostKind -ne $kind) { continue }
    $stateFile = Join-Path $workspace ".local/runtime/$($kind.ToLowerInvariant()).process.json"
    if (-not (Test-Path -LiteralPath $stateFile)) { continue }
    $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    $expected = [IO.Path]::GetFullPath($state.executable)
    $allowed = [IO.Path]::GetFullPath((Join-Path $workspace '.local/runtime/releases')) + [IO.Path]::DirectorySeparatorChar
    if (-not $expected.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to stop a process outside activated releases.' }
    $process = Get-Process -Id $state.id -ErrorAction SilentlyContinue
    if (-not $process) { continue }
    if ($process.Path -ne $expected -or $process.StartTime.ToUniversalTime() -ne ([DateTime]$state.startedUtc).ToUniversalTime()) { throw 'Tracked PID has changed identity; refusing to stop it.' }
    Stop-Process -Id $process.Id
    $process.WaitForExit(10000) | Out-Null
    if (-not $process.HasExited) { throw 'Host did not stop.' }
    Write-Output "$kind stopped: PID $($process.Id)"
}
