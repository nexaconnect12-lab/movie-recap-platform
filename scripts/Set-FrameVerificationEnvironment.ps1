$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Set-AudioVerificationEnvironment.ps1"
$env:Frames__FfmpegPath = $env:TEST_FFMPEG
# Tests supply their own temporary artifact roots; this helper starts no hosts and applies no application migrations.
