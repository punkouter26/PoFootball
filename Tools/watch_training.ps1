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

# Last "Step: N" the trainer logged, or $null when it has not logged one yet.
# mlagents writes one line per behaviour per summary, so the max across them all is
# the run's real progress — taking the last line alone would oscillate between
# behaviours that are at different steps and read as a stall.
function Get-LatestStep([string]$path) {
    if (-not (Test-Path $path)) {
        return $null
    }

    $steps = Select-String -Path $path -Pattern 'Step:\s*(\d+)' -AllMatches |
        ForEach-Object { $_.Matches } |
        ForEach-Object { [int64]$_.Groups[1].Value }

    if (-not $steps) {
        return $null
    }

    return ($steps | Measure-Object -Maximum).Maximum
}

$lastStep = $null

Write-Watch ("watchdog started; run-id=" + $RunId + " deadline=" + $deadline)

while ((Get-Date) -lt $deadline) {

    if (Test-Path $stopFile) {
        Write-Watch "STOP_TRAINING present - exiting without touching the run"
        break
    }

    $alive = @(Get-Process -Name "mlagents-learn" -ErrorAction SilentlyContinue).Count
    $envCount = @(Get-Process -Name "PoFootball" -ErrorAction SilentlyContinue).Count
    $step = Get-LatestStep $log

    # WHY THREE CHECKS AND NOT JUST "IS THE TRAINER ALIVE".
    #
    # football_base09 died without this noticing. Its ENV WORKERS crashed —
    # BrokenPipeError [WinError 109] on one pipe, then EOFError on the rest — while
    # mlagents-learn itself stayed up. A trainer with dead workers is not a trainer:
    # it collects no experience and writes no checkpoints, but it is still a running
    # process, so a liveness check on the process alone reported everything fine for
    # as long as it was left alone. The run ended with Quarterback at ~100k steps
    # against Offense's ~1M, and nothing said so until someone read the log.
    #
    # So: the trainer has to be up, it has to still own as many envs as it was
    # started with, and the step count has to be moving. Any one of those failing is
    # a dead run that needs resuming from its last checkpoint.
    $reason = $null

    if ($alive -eq 0) {
        $reason = "trainer not running"
    }
    elseif ($envCount -lt $NumEnvs) {
        $reason = "only $envCount of $NumEnvs envs alive - workers died under a live trainer"
    }
    elseif ($null -ne $lastStep -and $null -ne $step -and $step -le $lastStep) {
        $reason = "step stuck at $step since last poll - trainer alive but not learning"
    }

    $lastStep = $step

    if ($null -ne $reason) {
        Write-Watch ("restarting: " + $reason)

        # Order matters: trainer, then envs (CLAUDE.md section 4). Killing the envs
        # first makes a live trainer log a fresh wall of pipe errors on the way down.
        Get-Process -Name "mlagents-learn" -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue

        # Anything left from the dead run still owns the ports the next one wants.
        Get-Process -Name "PoFootball" -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue

        # AND THE ORPHANED PYTHON WORKERS. subprocess_env_manager spawns one child
        # python per env; they outlive a force-killed trainer, keep holding the base
        # ports, and make the next --force wipe silently no-op because they still
        # have the results directory open. Only the ones under this venv are touched.
        Get-CimInstance Win32_Process -Filter "Name = 'python.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine -like "*$root*" } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

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
