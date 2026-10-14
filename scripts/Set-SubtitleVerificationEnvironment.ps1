# Dot-source for isolated verification. Starts no hosts and applies no live migrations.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Set-FrameVerificationEnvironment.ps1"
$env:Subtitles__FfmpegPath = $env:TEST_FFMPEG
# Tests own their temporary roots; the activated release profile remains untouched.
