# Unattended desktop: give the VM a real, always-active Windows desktop so
# Claude Desktop's computer use has something to see and click — with nobody
# connected.
#
# WHY THIS IS NEEDED. Computer use captures the screen of an ACTIVE interactive
# session. A cloud VM has no physical console, so by default there is no such
# session until someone logs in — and the moment you disconnect RDP the session
# goes to "Disconnected", where screen capture returns black and input goes
# nowhere. That is the classic reason GUI automation "works over RDP and breaks
# the second you close the window".
#
# Three fixes, applied here:
#   1. Auto-logon, so an interactive console session exists from boot onward.
#   2. No lock screen / no screensaver / no display sleep, so it stays capturable.
#   3. A disconnect hook: when you leave over RDP, redirect the session back to
#      the console (tscon /dest:console) instead of letting it go Disconnected.
#
# RUN THIS ONCE, over RDP, as Administrator. It asks for the Windows password
# so nothing has to store it in Terraform, metadata, or this repo.
#
# SECURITY, plainly: auto-logon means the VM boots to an unlocked desktop, and
# tscon leaves the session unlocked after you disconnect. On a single-purpose
# game/build VM whose only ingress is RDP+Parsec scoped to your own IP, that is
# a reasonable trade. Do not apply this pattern to a machine that holds
# anything you care about. Undo with:  .\unattended-desktop.ps1 -Disable

param(
    [switch]$Disable,
    [string]$User = "cs2"
)

$ErrorActionPreference = "Stop"
function Step($m) { Write-Host "=== $m" }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this in an elevated PowerShell (Run as Administrator)."
}

$winlogon = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
$sysPol   = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
$persPol  = "HKCU:\Software\Policies\Microsoft\Windows\Control Panel\Desktop"

if ($Disable) {
    Step "Disabling auto-logon"
    Set-ItemProperty -Path $winlogon -Name AutoAdminLogon -Value "0" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $winlogon -Name DefaultPassword -ErrorAction SilentlyContinue
    Step "Re-enabling the lock screen"
    Remove-ItemProperty -Path $sysPol -Name DisableLockWorkstation -ErrorAction SilentlyContinue
    schtasks /Delete /TN "cs2-console-reclaim" /F 2>$null | Out-Null
    Step "Done. Reboot to return to normal login behaviour."
    return
}

# ---------------------------------------------------------------------------
Step "Auto-logon"
# Sysinternals Autologon stores the password as an LSA secret rather than in
# plaintext under Winlogon\DefaultPassword, which is why we prefer it.
$autologon = "C:\cs2\Autologon.exe"
if (-not (Test-Path $autologon)) {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "https://download.sysinternals.com/files/AutoLogon.zip" -OutFile "C:\cs2\AutoLogon.zip"
        Expand-Archive -Path "C:\cs2\AutoLogon.zip" -DestinationPath "C:\cs2\autologon-tmp" -Force
        $exe = Get-ChildItem "C:\cs2\autologon-tmp" -Filter "Autologon*.exe" |
               Where-Object { $_.Name -notmatch "64a" } | Select-Object -First 1
        if ($exe) { Copy-Item $exe.FullName $autologon -Force }
        Remove-Item "C:\cs2\AutoLogon.zip","C:\cs2\autologon-tmp" -Recurse -Force -ErrorAction SilentlyContinue
    } catch { Write-Warning "Autologon download failed: $($_.Exception.Message)" }
}

$sec = Read-Host -AsSecureString "Windows password for '$User' (from: cs2 rdp)"
$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
             [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec))

if (Test-Path $autologon) {
    & $autologon $User $env:COMPUTERNAME $plain /accepteula | Out-Null
    Step "Auto-logon configured (password held as an LSA secret)"
} else {
    # Fallback: registry auto-logon. Works, but the password is readable by any
    # administrator on the box. Flagged loudly on purpose.
    Set-ItemProperty -Path $winlogon -Name AutoAdminLogon -Value "1"
    Set-ItemProperty -Path $winlogon -Name DefaultUserName -Value $User
    Set-ItemProperty -Path $winlogon -Name DefaultPassword -Value $plain
    Write-Warning "Sysinternals Autologon unavailable — fell back to REGISTRY auto-logon."
    Write-Warning "The password is stored in cleartext at HKLM\...\Winlogon\DefaultPassword."
}
$plain = $null
[GC]::Collect()

# ---------------------------------------------------------------------------
Step "Keep the desktop capturable"
# No lock screen: a locked session screenshots as the lock screen, not the app.
New-Item -Path $sysPol -Force | Out-Null
Set-ItemProperty -Path $sysPol -Name DisableLockWorkstation -Value 1 -Type DWord
# No screensaver, no timeouts.
New-Item -Path $persPol -Force | Out-Null
Set-ItemProperty -Path $persPol -Name ScreenSaveActive -Value "0" -Type String
Set-ItemProperty -Path $persPol -Name ScreenSaverIsSecure -Value "0" -Type String
powercfg /change monitor-timeout-ac 0 2>$null
powercfg /change standby-timeout-ac 0 2>$null
# Windows Server's "shutdown reason" prompt can steal focus on a fresh desktop.
Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Reliability" `
    -Name ShutdownReasonOn -Value 0 -Type DWord -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
Step "Console reclaim on RDP disconnect"
# When an RDP session disconnects it becomes "Disconnected" and stops being
# capturable. Redirecting it to the console keeps full graphical context, so
# computer use survives you closing the RDP window.
@'
# Reclaim any Disconnected session back to the console so the desktop stays
# active and capturable. Safe to run repeatedly; does nothing when all sessions
# are already Active.
$lines = (query session 2>$null)
if (-not $lines) { exit 0 }
foreach ($l in $lines) {
    if ($l -match '^\s*\S*\s+\S+\s+(\d+)\s+Disc') {
        $id = $Matches[1]
        if ($id -ne '0') {
            Start-Process -FilePath tscon.exe -ArgumentList "$id","/dest:console" -NoNewWindow -Wait -ErrorAction SilentlyContinue
        }
    }
}
'@ | Set-Content "C:\cs2\console-reclaim.ps1"

schtasks /Query /TN "cs2-console-reclaim" > $null 2>&1
if ($LASTEXITCODE -ne 0) {
    schtasks /Create /TN "cs2-console-reclaim" `
        /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\cs2\console-reclaim.ps1" `
        /SC MINUTE /MO 1 /RU SYSTEM /RL HIGHEST /F | Out-Null
    Step "Console-reclaim task scheduled (every minute)"
}

# ---------------------------------------------------------------------------
Step "Claude Desktop autostart"
$claude = "$env:LOCALAPPDATA\Programs\Claude\Claude.exe"
if (-not (Test-Path $claude)) { $claude = "$env:ProgramFiles\Claude\Claude.exe" }
if (Test-Path $claude) {
    $startup = [Environment]::GetFolderPath('Startup')
    $sc = (New-Object -ComObject WScript.Shell).CreateShortcut("$startup\Claude.lnk")
    $sc.TargetPath = $claude
    $sc.Save()
    Step "Claude Desktop will start with the session"
} else {
    Write-Warning "Claude Desktop not found — install it, then re-run this script."
}

Write-Host ""
Write-Host "Unattended desktop ready. Reboot, then verify WITHOUT connecting:"
Write-Host "  gcloud compute instances get-serial-port-output cs2-workstation --zone us-central1-a"
Write-Host "  (or reconnect and check: query session   -> console should be Active)"
Write-Host ""
Write-Host "Still to do by hand, once (2FA, GUI-only):"
Write-Host "  1. Sign into Claude Desktop"
Write-Host "  2. Settings > General > Desktop app > enable Computer use"
Write-Host "  3. Sign into Steam + Parsec"
