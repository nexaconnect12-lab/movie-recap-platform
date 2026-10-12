$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Set-SceneVerificationEnvironment.ps1"
if (-not $env:TEST_FFMPEG) {
    $env:TEST_FFMPEG = Join-Path (Split-Path $env:TEST_FFPROBE) 'ffmpeg.exe'
}
if (-not (Test-Path -LiteralPath $env:TEST_FFMPEG -PathType Leaf)) { throw 'Set TEST_FFMPEG to an existing real FFmpeg executable.' }
# Tests override artifact roots with their own temporary directories; no application artifact folder is created here.
$env:Audio__FfmpegPath = $env:TEST_FFMPEG
