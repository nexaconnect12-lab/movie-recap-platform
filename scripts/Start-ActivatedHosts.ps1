param([ValidateSet('Both','Api','Worker')][string]$HostKind = 'Both')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Set-ActivatedEnvironment.ps1"
$stateDirectory = Join-Path $activationWorkspace '.local/runtime'
$logDirectory = Join-Path $activationWorkspace '.local/logs'
foreach ($kind in @('Api','Worker')) {
    if ($HostKind -ne 'Both' -and $HostKind -ne $kind) { continue }
    $stateFile = Join-Path $stateDirectory "$($kind.ToLowerInvariant()).process.json"
    if (Test-Path -LiteralPath $stateFile) {
        $existing = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
        $process = Get-Process -Id $existing.id -ErrorAction SilentlyContinue
        if ($process -and $process.StartTime.ToUniversalTime() -eq ([DateTime]$existing.startedUtc).ToUniversalTime()) { throw "$kind is already tracked as running." }
    }
    $executable = if ($kind -eq 'Api') { $activation.api } else { $activation.worker }
    $stamp = [Guid]::NewGuid().ToString('N')
    $stdout = Join-Path $logDirectory "activated-$kind-$stamp.out.log"
    $stderr = Join-Path $logDirectory "activated-$kind-$stamp.err.log"
    $env:Logging__FilePath = Join-Path $logDirectory "activated-$kind-.json"
    $process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    Start-Sleep -Milliseconds 300
    $process.Refresh()
    if ($process.HasExited) { throw "$kind exited during startup; inspect its local logs." }
    [ordered]@{id=$process.Id; startedUtc=$process.StartTime.ToUniversalTime().ToString('o'); executable=$executable; stdout=$stdout; stderr=$stderr} | ConvertTo-Json | Set-Content -LiteralPath $stateFile
    Write-Output "$kind started: PID $($process.Id)"
}
