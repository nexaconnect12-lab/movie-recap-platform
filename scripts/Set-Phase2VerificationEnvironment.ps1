# Dot-source this helper. It sets verification configuration; it starts no services and writes no credentials.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Set-LocalEnvironment.ps1"
$env:UseArtifactsOutput = 'true'
$env:ArtifactsPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/phase2-verification'))
if (-not $env:TEST_POSTGRES) { $env:TEST_POSTGRES = $env:ConnectionStrings__Platform }
if (-not $env:TEST_FFPROBE) {
    $probeBundle = Join-Path $PSScriptRoot '../artifacts/phase2-tools/ffmpeg'
    $probeExecutable = if (Test-Path -LiteralPath $probeBundle) {
        Get-ChildItem -LiteralPath $probeBundle -Recurse -Filter ffprobe.exe | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $probeExecutable) { throw 'Set TEST_FFPROBE to an existing real FFprobe executable.' }
    $env:TEST_FFPROBE = $probeExecutable
}
if (-not (Test-Path -LiteralPath $env:TEST_FFPROBE -PathType Leaf)) { throw 'TEST_FFPROBE must point to an existing real executable.' }
