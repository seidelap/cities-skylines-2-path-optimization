# Idle guard: the difference between "a few dollars a month" and a surprise bill.
#
# Two independent protections, because the second one is what actually saves you:
#   1. Idle detection  - no Parsec session and a quiet GPU for N consecutive checks.
#   2. Hard session cap - shut down after H hours of uptime regardless of activity.
#      This is the backstop for the case where idle detection is fooled (a stuck
#      process, a paused game, a driver pinning the GPU).
#
# Installed by startup.ps1 as a scheduled task running every 5 minutes.

param(
    [int]$IdleMinutes  = 20,
    [int]$MaxHours     = 6,
    [int]$CheckMinutes = 5
)

$stateFile = "C:\cs2\idle-state.json"
$logFile   = "C:\cs2\idle-guard.log"
New-Item -ItemType Directory -Force -Path "C:\cs2" | Out-Null

function Write-Log($msg) {
    "$(Get-Date -Format o)  $msg" | Add-Content -Path $logFile
}

function Get-UptimeHours {
    $os = Get-CimInstance Win32_OperatingSystem
    return ((Get-Date) - $os.LastBootUpTime).TotalHours
}

function Test-ParsecActive {
    # A live stream shows up as an established remote connection owned by parsecd.
    $p = Get-Process parsecd -ErrorAction SilentlyContinue
    if (-not $p) { return $false }
    try {
        $conns = Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
                 Where-Object { $_.OwningProcess -in $p.Id -and $_.RemoteAddress -notmatch '^(127\.|::1|0\.0\.0\.0)' }
        if ($conns) { return $true }
        $udp = Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
               Where-Object { $_.OwningProcess -in $p.Id -and $_.LocalPort -ge 8000 -and $_.LocalPort -le 8040 }
        # A bound UDP port alone does not prove a session, so pair it with GPU load.
        if ($udp -and (Get-GpuUtil) -ge 10) { return $true }
    } catch { }
    return $false
}

function Get-GpuUtil {
    $smi = "C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
    if (-not (Test-Path $smi)) { $smi = "nvidia-smi.exe" }
    try {
        $out = & $smi --query-gpu=utilization.gpu --format=csv,noheader,nounits 2>$null
        if ($LASTEXITCODE -eq 0 -and $out) { return [int](($out -split "`n")[0].Trim()) }
    } catch { }
    return -1   # unknown: treated as "not busy" but never as "busy"
}

function Test-InteractiveSession {
    # An RDP session counts as activity: you are probably mid-setup.
    try {
        $q = quser 2>$null
        if ($q -and ($q | Select-String -Pattern 'Active')) { return $true }
    } catch { }
    return $false
}

# ---------------------------------------------------------------------------
$uptime = Get-UptimeHours
if ($MaxHours -gt 0 -and $uptime -ge $MaxHours) {
    Write-Log "HARD CAP: uptime $([math]::Round($uptime,2))h >= ${MaxHours}h. Shutting down."
    Stop-Computer -Force
    exit 0
}

if ($IdleMinutes -le 0) { exit 0 }

$gpu      = Get-GpuUtil
$parsec   = Test-ParsecActive
$session  = Test-InteractiveSession
$busy     = $parsec -or $session -or ($gpu -ge 25)

$state = @{ idleChecks = 0 }
if (Test-Path $stateFile) {
    try { $state = Get-Content $stateFile -Raw | ConvertFrom-Json } catch { }
}
$idleChecks = [int]$state.idleChecks

if ($busy) {
    if ($idleChecks -ne 0) { Write-Log "active (parsec=$parsec session=$session gpu=$gpu%): resetting idle counter" }
    $idleChecks = 0
} else {
    $idleChecks++
    Write-Log "idle #$idleChecks (parsec=$parsec session=$session gpu=$gpu%)"
}

$needed = [math]::Ceiling($IdleMinutes / [double]$CheckMinutes)
if ($idleChecks -ge $needed) {
    Write-Log "IDLE: $idleChecks checks (>= $needed). Shutting down."
    @{ idleChecks = 0 } | ConvertTo-Json | Set-Content $stateFile
    Stop-Computer -Force
    exit 0
}

@{ idleChecks = $idleChecks } | ConvertTo-Json | Set-Content $stateFile
