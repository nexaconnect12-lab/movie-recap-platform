# Creates owned fixture imports/artifacts and intentionally crashes ONLY the tracked worker.
# Leaves the primary preparation approved and all live history intact. Requires an activated release.
param([switch]$SkipLifecycle)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Set-ActivatedEnvironment.ps1"
$run = 'rehearsal-' + [Guid]::NewGuid().ToString('N')
$receiptRoot = Join-Path $activationWorkspace ".local/runtime/receipts/$run"
New-Item -ItemType Directory -Path $receiptRoot | Out-Null
$headers = @{'X-Api-Key'=$env:Security__ApiKey}
$base = 'http://127.0.0.1:5080'
function Api([string]$method, [string]$path, $body = $null) {
    $arguments = @{Method=$method; Uri="$base$path"; Headers=$headers; TimeoutSec=120}
    if ($null -ne $body) { $arguments.Body=$body | ConvertTo-Json -Depth 20; $arguments.ContentType='application/json' }
    Invoke-RestMethod @arguments
}
function Check([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function WaitJob([string]$id, [string]$expected='Succeeded') {
    $until = [DateTime]::UtcNow.AddSeconds(120)
    do {
        $job = Api GET "/api/jobs/$id"
        if ($job.status -eq $expected) { return $job }
        if ($job.status -in @('Failed','Cancelled','Succeeded') -and $job.status -ne $expected) { throw "Job ended $($job.status): $($job.errorCode)" }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out waiting for job $id to become $expected."
}
function ValidatePreparation([string]$tag) {
    $overview = Api GET "/api/media/imports/$importId/preparation"
    Check $overview.snapshot.isReady 'Preparation must be ready.'
    Api POST "/api/media/imports/$importId/preparation/validations" @{idempotencyKey="$run-$tag"; snapshotHash=$overview.snapshot.snapshotHash; requestedBy='activation-operator'; maxAttempts=3}
}
function ApprovePreparation {
    $overview = Api GET "/api/media/imports/$importId/preparation"
    Api POST "/api/media/imports/$importId/preparation/decision" @{version=$overview.review.version; snapshotHash=$overview.snapshot.snapshotHash; decision='Approved'; requestedBy='activation-operator'; notes='Owned fixture: chosen blue video, 880 Hz audio, three frame samples and integrity report verified.'} | Out-Null
    $current = Api GET "/api/media/imports/$importId/preparation"
    Check ($current.effectiveStatus -eq 'Approved') 'Approval must be current.'
    $current
}
function Prepare([string]$tag) {
    $streams = Api GET "/api/media/imports/$importId/streams"
    $scene = Api POST "/api/media/imports/$importId/scene-analyses" @{idempotencyKey="$run-$tag-scenes"; selectionVersion=$streams.selection.version; requestedBy='activation-operator'; threshold=27; minimumSceneFrames=2; maxAttempts=3}
    WaitJob $scene.job.id | Out-Null
    $scene = Api GET "/api/video/scene-analyses/$($scene.analysis.id)"
    Check (@($scene.scenes).Count -eq 1) 'The selected constant-blue video must have one scene.'
    $audio = Api POST "/api/media/imports/$importId/audio-extractions" @{idempotencyKey="$run-$tag-audio"; selectionVersion=$streams.selection.version; requestedBy='activation-operator'; maxAttempts=3}
    WaitJob $audio.job.id | Out-Null
    $audio = Api GET "/api/video/audio-extractions/$($audio.extraction.id)"
    $frames = Api POST "/api/video/scene-analyses/$($scene.analysis.id)/frame-samplings" @{idempotencyKey="$run-$tag-frames"; requestedBy='activation-operator'; samplesPerScene=3; maxAttempts=3}
    WaitJob $frames.job.id | Out-Null
    $frames = Api GET "/api/video/frame-samplings/$($frames.sampling.id)"
    Check (@($frames.samples).Count -eq 3) 'Expected three decoded frame samples.'
    $wav = Join-Path $receiptRoot "$tag.wav"
    Invoke-WebRequest -Uri "$base/api/video/audio-extractions/$($audio.extraction.id)/artifact" -Headers $headers -OutFile $wav | Out-Null
    $images = @()
    foreach ($sample in $frames.samples) {
        $path = Join-Path $receiptRoot "$tag-$($sample.ordinal).png"
        Invoke-WebRequest -Uri "$base/api/video/frame-samplings/$($frames.sampling.id)/samples/$($sample.ordinal)" -Headers $headers -OutFile $path | Out-Null
        $images += $path
    }
    $inspection = & $activation.python "$PSScriptRoot/Verify-RehearsalArtifacts.py" $wav @images
    if ($LASTEXITCODE -ne 0) { throw 'Independent audio/image verification failed.' }
    $validation = ValidatePreparation "$tag-check"
    WaitJob $validation.job.id | Out-Null
    $report = Api GET "/api/video/preparation-validations/$($validation.validation.id)"
    Check ($report.validation.passed -eq $true) 'Integrity report must pass.'
    $approved = ApprovePreparation
    [ordered]@{scene=$scene; audio=$audio; frames=$frames; inspection=($inspection | ConvertFrom-Json); validation=$report; approved=$approved}
}
$fixture = Join-Path $activationWorkspace 'tests/Fixtures/owned-multistream.mkv'
$fixtureBefore = @{hash=(Get-FileHash -LiteralPath $fixture).Hash; writeUtc=(Get-Item -LiteralPath $fixture).LastWriteTimeUtc}
$relative = "$run.mkv"
$source = Join-Path $activation.mediaRoot $relative
Check (-not (Test-Path -LiteralPath $source)) 'Refusing to overwrite media.'
Copy-Item -LiteralPath $fixture -Destination $source
$sourceBefore = @{hash=(Get-FileHash -LiteralPath $source).Hash; writeUtc=(Get-Item -LiteralPath $source).LastWriteTimeUtc}
$request = @{idempotencyKey="$run-import"; folder='rehearsal'; relativePath=$relative; source='Repository-owned synthetic FFmpeg fixture; no third-party footage'; requestedBy='activation-operator'; maxAttempts=3; rights=@{owner='Movie Recap Platform fixture authors'; authorization='Authorized'; licenseEvidence='tests/Fixtures/README.md: locally generated lavfi colors and sine waves'; permittedUses='Local technical acceptance tests'; permittedPlatforms='Local development only'; attribution='Repository-owned synthetic test fixture'; expiresAt=$null}}
$import = Api POST '/api/media/imports' $request
$importId = $import.import.id
WaitJob $import.job.id | Out-Null
$import = Api GET "/api/media/imports/$importId"
Check ($import.asset.sha256 -eq $sourceBefore.hash.ToLowerInvariant()) 'Imported source SHA-256 mismatch.'
$streams = Api GET "/api/media/imports/$importId/streams"
Check (@($streams.streams).Count -eq 6) 'Expected the complete six-stream inventory.'
Api PUT "/api/media/imports/$importId/stream-selection" @{version=$streams.selection.version; videoIndex=1; audioIndex=3; subtitleIndex=5; requestedBy='activation-operator'} | Out-Null
Write-Output "Imported owned fixture $importId; selected video 1, audio 3, subtitle 5."
$initial = Prepare 'initial'
$lifecycle = [ordered]@{skipped=[bool]$SkipLifecycle}
if (-not $SkipLifecycle) {
    $cancel = ValidatePreparation 'cancel'
    WaitJob $cancel.job.id 'Running' | Out-Null
    $cancelled = $false
    for ($attempt=0; $attempt -lt 5; $attempt++) {
        $job = Api GET "/api/jobs/$($cancel.job.id)"
        try { Api POST "/api/jobs/$($job.id)/cancel" @{version=$job.version} | Out-Null; $cancelled=$true; break }
        catch { if ([int]$_.Exception.Response.StatusCode -ne 409) { throw } }
    }
    Check $cancelled 'Cancellation command failed.'
    WaitJob $cancel.job.id 'Cancelled' | Out-Null
    Start-Sleep -Seconds 3
    $cancelReport = Api GET "/api/video/preparation-validations/$($cancel.validation.id)"
    Check ($null -eq $cancelReport.validation.completedAt) 'Cancelled validation published a report.'
    $recovery = ValidatePreparation 'recovery'
    WaitJob $recovery.job.id 'Running' | Out-Null
    & "$PSScriptRoot/Stop-ActivatedHosts.ps1" -HostKind Worker
    & "$PSScriptRoot/Start-ActivatedHosts.ps1" -HostKind Worker
    $recoveredJob = WaitJob $recovery.job.id
    $recovered = Api GET "/api/video/preparation-validations/$($recovery.validation.id)"
    $audit = Api GET "/api/jobs/$($recovery.job.id)/audit"
    Check ($recoveredJob.attempts -ge 2 -and $recovered.validation.passed -eq $true) 'Lease recovery did not complete a real second attempt.'
    Check (@($audit | Where-Object action -eq 'lease-recovered').Count -gt 0) 'Missing lease recovery audit.'
    $stale = Api GET "/api/media/imports/$importId/preparation"
    Check ($stale.effectiveStatus -eq 'Stale') 'New completed validation must stale prior approval.'
    ApprovePreparation | Out-Null
    $lifecycle.cancelled=$cancelReport
    $lifecycle.recovered=$recovered
    $lifecycle.recoveryAudit=$audit
    Write-Output 'Running cancellation and crash/lease recovery verified.'
}
$streams = Api GET "/api/media/imports/$importId/streams"
Api PUT "/api/media/imports/$importId/stream-selection" @{version=$streams.selection.version; videoIndex=0; audioIndex=3; subtitleIndex=5; requestedBy='activation-operator'} | Out-Null
$stale = Api GET "/api/media/imports/$importId/preparation"
Check ($stale.effectiveStatus -eq 'Stale') 'Changed selection must stale prior approval.'
$historyDownload = Join-Path $receiptRoot 'historical.wav'
Invoke-WebRequest -Uri "$base/api/video/audio-extractions/$($initial.audio.extraction.id)/artifact" -Headers $headers -OutFile $historyDownload | Out-Null
Check ((Get-FileHash -LiteralPath $historyDownload).Hash -eq (Get-FileHash -LiteralPath (Join-Path $receiptRoot 'initial.wav')).Hash) 'Historical artifact bytes changed.'
$streams = Api GET "/api/media/imports/$importId/streams"
Api PUT "/api/media/imports/$importId/stream-selection" @{version=$streams.selection.version; videoIndex=1; audioIndex=3; subtitleIndex=5; requestedBy='activation-operator'} | Out-Null
$final = Prepare 'final'
$request.idempotencyKey="$run-duplicate"
$duplicate = Api POST '/api/media/imports' $request
WaitJob $duplicate.job.id | Out-Null
$duplicate = Api GET "/api/media/imports/$($duplicate.import.id)"
Check ($duplicate.import.isDuplicate -and $duplicate.asset.id -eq $import.asset.id) 'Duplicate import must reuse the SHA-256 asset.'
Check ((Get-FileHash -LiteralPath $source).Hash -eq $sourceBefore.hash -and (Get-Item -LiteralPath $source).LastWriteTimeUtc -eq $sourceBefore.writeUtc) 'Source media was modified.'
Check ((Get-FileHash -LiteralPath $fixture).Hash -eq $fixtureBefore.hash -and (Get-Item -LiteralPath $fixture).LastWriteTimeUtc -eq $fixtureBefore.writeUtc) 'Original fixture was modified.'
$reviewAudit = Api GET "/api/media/imports/$importId/preparation/audit"
[ordered]@{run=$run; completedUtc=[DateTime]::UtcNow.ToString('o'); import=$import; initial=$initial; lifecycle=$lifecycle; selectionStale=$stale; final=$final; duplicate=$duplicate; sourcePreserved=$true; fixturePreserved=$true; sourceSha256=$sourceBefore.hash; reviewAudit=$reviewAudit} | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $receiptRoot 'acceptance.json')
Write-Output "Acceptance passed; current preparation Approved. Receipt: $receiptRoot/acceptance.json"
