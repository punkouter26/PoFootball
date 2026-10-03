# Tools\publish.ps1 - build the signed AAB and upload it to Play internal testing.
#
#   .\Tools\publish.ps1              # build + upload as DRAFT to internal track
#   .\Tools\publish.ps1 -DryRun      # full rehearsal, nothing lands in the console
#   .\Tools\publish.ps1 -SkipBuild   # upload the existing Builds\Android\PoFootball.aab
#
# bundleVersionCode is bumped by the build itself (Editor_BuildAndroidAAB
# .NextVersionCode). This script used to bump it as well, before invoking that
# build, so every publish advanced the code by two.
#
# Secrets come from the shared signing vault (CLAUDE.md, "Secrets live OUTSIDE the
# repo"), and the uploader runs in its own venv, never .venv, which carries the
# load-bearing ml-agents/torch pins. This script used to point at PoRacer's
# service-account key and at a venv outside this repository.
#
# The Unity editor must NOT have this project open, or the headless build fails on
# the project lock — the script checks and refuses up front.
param(
    [switch]$SkipBuild,
    [switch]$DryRun,
    [string]$Track = 'internal',
    [string]$Status = 'draft'
)
$ErrorActionPreference = 'Stop'
$App = 'PoFootball'
$BuildMethod = 'PoFootball.EditorTools.Editor_BuildAndroidAAB.Build'
$Proj = Split-Path $PSScriptRoot -Parent
$Python = Join-Path $Proj 'Tools\publish-venv\Scripts\python.exe'
$Creds = if ($env:POFOOTBALL_PLAY_CREDENTIALS) { $env:POFOOTBALL_PLAY_CREDENTIALS } `
         else { 'C:\Users\punko\OneDrive\VAULT\_CODE\pofootball-play-service-account.json' }
$Aab = Join-Path $Proj "Builds\Android\$App.aab"

if (-not (Test-Path $Python)) {
    Write-Error ("Publish venv not found at $Python. Create it with:`n" +
        "  py -3 -m venv Tools\publish-venv`n" +
        "  Tools\publish-venv\Scripts\python.exe -m pip install -r Tools\requirements-publish.txt")
}
if (-not (Test-Path $Creds)) { Write-Error "Service account key not found at $Creds (see the SETUP block in Tools\play_publish.py)" }

if (-not $SkipBuild) {
    # Fail fast if the project is open in the editor — the headless build would
    # only discover the project lock after ~30s of Unity startup.
    $open = Get-Process Unity -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -like "$App -*" }
    if ($open) {
        Write-Error "$App is open in the Unity editor (PID $($open[0].Id)). Close it and retry."
    }

    $ver = (Select-String -Path (Join-Path $Proj 'ProjectSettings\ProjectVersion.txt') `
            -Pattern 'm_EditorVersion: (.+)').Matches[0].Groups[1].Value.Trim()
    $unity = "C:\Program Files\Unity\Hub\Editor\$ver\Editor\Unity.exe"
    if (-not (Test-Path $unity)) {
        $latest = Get-ChildItem 'C:\Program Files\Unity\Hub\Editor' -Directory |
            Sort-Object Name | Select-Object -Last 1
        Write-Warning "Unity $ver not installed; falling back to $($latest.Name)"
        $unity = Join-Path $latest.FullName 'Editor\Unity.exe'
    }
    New-Item -ItemType Directory -Force (Join-Path $Proj 'Builds') | Out-Null
    $log = Join-Path $Proj 'Builds\publish-build.log'
    Write-Host "Building $App AAB headlessly (log: $log)..."
    $proc = Start-Process -FilePath $unity -PassThru -Wait -ArgumentList `
        '-batchmode', '-nographics', '-quit', '-projectPath', $Proj, `
        '-buildTarget', 'Android', '-executeMethod', $BuildMethod, '-logFile', $log
    $result = Select-String -Path $log -Pattern 'AAB BUILD RESULT:' | Select-Object -Last 1
    if ($result) { Write-Host $result.Line }
    if ($proc.ExitCode -ne 0 -or -not $result -or $result.Line -notmatch 'Succeeded') {
        Select-String -Path $log -Pattern 'error CS|Error building|BuildFailedException|Aborted|could not be found' |
            Select-Object -Last 5 | ForEach-Object { Write-Host "  $($_.Line)" }
        Write-Error "Build failed (Unity exit $($proc.ExitCode)). Full log: $log"
    }
}

if (-not (Test-Path $Aab)) { Write-Error "No AAB at $Aab - run without -SkipBuild first" }
$pyArgs = @((Join-Path $Proj 'Tools\play_publish.py'),
            '--aab', $Aab, '--credentials', $Creds, '--track', $Track, '--status', $Status)
if ($DryRun) { $pyArgs += '--dry-run' }
& $Python @pyArgs
exit $LASTEXITCODE
