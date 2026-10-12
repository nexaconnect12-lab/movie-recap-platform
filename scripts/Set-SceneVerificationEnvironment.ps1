# Dot-source this after preparing the workspace-local Python environment described in docs/api/scene-analysis.md.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Set-Phase2VerificationEnvironment.ps1"
if (-not $env:TEST_SCENE_PYTHON) {
    $scenePython = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/phase2-tools/scene-venv/Scripts/python.exe'))
    if (-not (Test-Path -LiteralPath $scenePython -PathType Leaf)) { throw 'Set TEST_SCENE_PYTHON to a real Python environment with the pinned scene dependencies.' }
    $env:TEST_SCENE_PYTHON = $scenePython
}
if (-not (Test-Path -LiteralPath $env:TEST_SCENE_PYTHON -PathType Leaf)) { throw 'TEST_SCENE_PYTHON must point to an existing real Python executable.' }
$env:Scenes__PythonPath = $env:TEST_SCENE_PYTHON
