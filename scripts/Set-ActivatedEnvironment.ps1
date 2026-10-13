# Dot-source after activation has written the nonsecret local profile.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Set-LocalEnvironment.ps1"
$activationWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$activationProfile = Join-Path $activationWorkspace '.local/runtime/active.json'
if (-not (Test-Path -LiteralPath $activationProfile)) { throw 'No activated local release profile exists.' }
$activation = Get-Content -LiteralPath $activationProfile -Raw | ConvertFrom-Json
foreach ($entry in @($activation.api, $activation.worker, $activation.mediaRoot, $activation.artifactRoot, $activation.ffprobe, $activation.ffmpeg, $activation.python)) {
    $resolved = [IO.Path]::GetFullPath($entry)
    if (-not $resolved.StartsWith($activationWorkspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Activation paths must remain inside this workspace.' }
    if (-not (Test-Path -LiteralPath $resolved)) { throw 'An activation path is unavailable.' }
    $ancestor = Get-Item -LiteralPath $resolved
    while ($ancestor) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Activation paths cannot contain reparse points.' }
        $ancestor = if ($ancestor -is [IO.FileInfo]) { $ancestor.Directory } else { $ancestor.Parent }
    }
}
$env:Media__Folders__rehearsal = $activation.mediaRoot
$env:Media__FfprobePath = $activation.ffprobe
$env:Media__StabilityMilliseconds = '2000'
$env:Audio__Root = $activation.artifactRoot
$env:Frames__Root = $activation.artifactRoot
$env:Audio__FfmpegPath = $activation.ffmpeg
$env:Frames__FfmpegPath = $activation.ffmpeg
$env:Scenes__PythonPath = $activation.python
$env:Discovery__Enabled = 'false'
$env:Worker__PollSeconds = '1'
$env:Worker__LeaseSeconds = '15'
