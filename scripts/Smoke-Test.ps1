# Run with API and worker running, from the repository root.
$ErrorActionPreference='Stop'
. "$PSScriptRoot/Set-LocalEnvironment.ps1"
$base='http://127.0.0.1:5080'
$headers=@{'X-Api-Key'=$env:Security__ApiKey}
if ((Invoke-WebRequest "$base/health/ready").StatusCode -ne 200) { throw 'API is not ready.' }
$body=@{kind='foundation.database-check';idempotencyKey=('smoke-'+[Guid]::NewGuid().ToString('N'));maxAttempts=3}|ConvertTo-Json
$job=Invoke-RestMethod "$base/api/jobs/" -Method Post -Headers $headers -ContentType application/json -Body $body
$deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
do {
    Start-Sleep -Milliseconds 500
    $job=Invoke-RestMethod "$base/api/jobs/$($job.id)" -Headers $headers
} while($job.status -notin @('Succeeded','Failed','Cancelled') -and [DateTimeOffset]::UtcNow -lt $deadline)
if($job.status -ne 'Succeeded') { throw "Job $($job.id) ended with $($job.status)." }
$audit=Invoke-RestMethod "$base/api/jobs/$($job.id)/audit" -Headers $headers
$job | Select-Object id,status,attempts,progress
$audit | Select-Object action,status,attempt

