# Keeps an mlagents-learn run alive unattended.
#
# WHY. A trainer that dies at 03:00 costs every hour until somebody notices. The
# run is checkpointed (checkpoint_interval 500000) and mlagents-learn --resume
# picks up from the last checkpoint, so a restart is cheap and lossless in a way
# that "discover it in the morning" is not.
#
# It only ever RESTARTS. It never starts a run that was deliberately stopped:
# drop a file named STOP_TRAINING in the project root and the watchdog exits
# without touching anything, which is the intended way to end the night early.
#
# Shutdown order when you do stop by hand is trainer -> envs -> TensorBoard
# (CLAUDE.md section 4). Killing the trainer alone leaves six PoFootball.exe and
# six python workers holding ports 5010-5015, and the next run's handshake then
# hangs with no error.
#
#   powershell -File Tools\watch_training.ps1 -RunId football_base05 `
#       -Config Config\FootballBase05.yaml -Hours 8

param(
    [string]$RunId  = "football_base05",
    [string]$Config = "Config\FootballBase05.yaml",
    [int]   $NumEnvs = 6,
    [int]   $BasePort = 5010,
    [double]$Hours = 8.0,
    [int]   $CheckSeconds = 300
)

$ErrorActionPreference = "SilentlyContinue"
$root     = Split-Path -Parent $PSScriptRoot
$exe      = Join-Path $root ".venv\Scripts\mlagents-learn.exe"
$log      = Join-Path $root ("Logs\" + $RunId + ".log")
$errLog   = Join-Path $root ("Logs\" + $RunId + ".err.log")
$watchLog = Join-Path $root ("Logs\" + $RunId + "_watchdog.log")
$stopFile = Join-Path $root "STOP_TRAINING"
$deadline = (Get-Date).AddHours($Hours)

function Write-Watch([string]$message) {
    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $message
    Add-Content -Path $watchLog -Value $line
}

Write-Watch ("watchdog started; run-id=" + $RunId + " deadline=" + $deadline)

while ((Get-Date) -lt $deadline) {

    if (Test-Path $stopFile) {
        Write-Watch "STOP_TRAINING present - exiting without touching the run"
        break
    }

    $alive = @(Get-Process -Name "mlagents-learn" -ErrorAction SilentlyContinue).Count

    if ($alive -eq 0) {
        Write-Watch "trainer not running - clearing stragglers and resuming"

        # Anything left from the dead run still owns the ports the next one wants.
        Get-Process -Name "PoFootball" -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 5

        $arguments = @(
            $Config,
            ("--run-id=" + $RunId),
            "--resume",
            "--env=Builds\FootballEnv\PoFootball.exe",
            "--no-graphics",
            ("--base-port=" + $BasePort),
            ("--num-envs=" + $NumEnvs)
        )

        $process = Start-Process -FilePath $exe -ArgumentList $arguments `
            -WorkingDirectory $root `
            -RedirectStandardOutput $log -RedirectStandardError $errLog `
            -WindowStyle Hidden -PassThru

        Write-Watch ("resumed; trainer pid=" + $process.Id)

        # Give the handshake room before the next poll decides it is dead again.
        Start-Sleep -Seconds 60
    }

    Start-Sleep -Seconds $CheckSeconds
}

Write-Watch "watchdog finished; training left running"
