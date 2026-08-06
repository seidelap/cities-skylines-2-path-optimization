# CS2 workstation bootstrap. Runs on EVERY boot, so everything here is idempotent.
# Terraform templates this file; avoid PowerShell's $${...} form anywhere below,
# because Terraform would try to interpolate it.

$ErrorActionPreference = "Continue"
New-Item -ItemType Directory -Force -Path "C:\cs2" | Out-Null
Start-Transcript -Path "C:\cs2\startup.log" -Append | Out-Null
function Step($m) { Write-Host "=== $m" ; "$(Get-Date -Format o)  $m" | Add-Content "C:\cs2\startup-steps.log" }

# ---------------------------------------------------------------------------
Step "Game disk"
# Attached as device 'cs2data'. Format on first boot only; never touch it after.
$raw = Get-Disk | Where-Object { $_.PartitionStyle -eq 'RAW' -and $_.Number -ne 0 }
if ($raw) {
    Step "Initializing new game disk (first boot)"
    $raw | Initialize-Disk -PartitionStyle GPT -PassThru |
        New-Partition -DriveLetter D -UseMaximumSize |
        Format-Volume -FileSystem NTFS -NewFileSystemLabel "cs2data" -Confirm:$false
} elseif (-not (Test-Path "D:\")) {
    # Restored-from-snapshot disks already have a partition; just make sure it is mounted.
    $part = Get-Partition | Where-Object { $_.DiskNumber -ne 0 -and -not $_.DriveLetter } | Select-Object -First 1
    if ($part) { $part | Set-Partition -NewDriveLetter D }
}
New-Item -ItemType Directory -Force -Path "D:\setup","D:\src","D:\exports" | Out-Null

# ---------------------------------------------------------------------------
Step "Interactive-desktop settings"
# Windows Server ships with audio disabled and aggressive display sleep; both
# break game streaming in ways that look like Parsec bugs.
Set-Service -Name Audiosrv -StartupType Automatic -ErrorAction SilentlyContinue
Start-Service -Name Audiosrv -ErrorAction SilentlyContinue
powercfg /setactive SCHEME_MIN 2>$null
powercfg /change monitor-timeout-ac 0 2>$null
powercfg /change standby-timeout-ac 0 2>$null
# Steam and CS2 both dislike IE Enhanced Security prompts during install.
Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Active Setup\Installed Components\{A509B1A7-37EF-4b3f-8CFC-4F3A74704073}" -Name IsInstalled -Value 0 -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
Step "GPU driver"
$smi = "C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
$haveDriver = (Test-Path $smi) -or ((Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue) -ne $null)
if (-not $haveDriver) {
    # NOTE: GCP publishes NVIDIA RTX Virtual Workstation (GRID) drivers to a public
    # bucket, but the exact path/version moves. This is best-effort: if it fails,
    # the log tells you to install manually, and everything else still comes up.
    try {
        Step "Attempting GRID driver download from the GCP public bucket"
        $listing = Invoke-WebRequest -UseBasicParsing -Uri "https://storage.googleapis.com/storage/v1/b/nvidia-drivers-us-public/o?prefix=GRID/&maxResults=2000"
        $items = ($listing.Content | ConvertFrom-Json).items
        $exe = $items | Where-Object { $_.name -match 'winserver.*\.exe$' } |
               Sort-Object name -Descending | Select-Object -First 1
        if ($exe) {
            $dst = "D:\setup\grid-driver.exe"
            Step "Downloading $($exe.name)"
            Invoke-WebRequest -UseBasicParsing -Uri "https://storage.googleapis.com/nvidia-drivers-us-public/$($exe.name)" -OutFile $dst
            Start-Process -FilePath $dst -ArgumentList "-s","-noreboot" -Wait
            Step "Driver installer finished"
        } else {
            Step "WARNING: no matching driver found in bucket listing — install manually"
        }
    } catch {
        Step "WARNING: driver install failed ($($_.Exception.Message)). Install the NVIDIA RTX Virtual Workstation driver manually, then reboot."
    }
} else {
    Step "GPU driver already present"
}

# ---------------------------------------------------------------------------
Step "Toolchain"
if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {
    Set-ExecutionPolicy Bypass -Scope Process -Force
    [System.Net.ServicePointManager]::SecurityProtocol = 3072
    Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
    $env:PATH += ";C:\ProgramData\chocolatey\bin"
}
foreach ($pkg in @("git","dotnet-sdk","7zip")) {
    if (-not (choco list --local-only --exact $pkg --limit-output)) {
        Step "Installing $pkg"
        choco install $pkg -y --no-progress | Out-Null
    }
}

# ---------------------------------------------------------------------------
Step "Steam + Parsec"
# Both need ONE interactive login the first time (Steam Guard, Parsec account).
# After that they persist on the game disk and survive VM rebuilds.
if (-not (Test-Path "D:\Steam\steam.exe") -and -not (Test-Path "C:\Program Files (x86)\Steam\steam.exe")) {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe" -OutFile "D:\setup\SteamSetup.exe"
        Start-Process "D:\setup\SteamSetup.exe" -ArgumentList "/S" -Wait
        Step "Steam installed — sign in over RDP once, then set the library folder to D:\Steam"
    } catch { Step "WARNING: Steam download failed: $($_.Exception.Message)" }
}
if (-not (Test-Path "C:\Program Files\Parsec\parsecd.exe")) {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "https://builds.parsec.app/package/parsec-windows.exe" -OutFile "D:\setup\parsec.exe"
        Start-Process "D:\setup\parsec.exe" -ArgumentList "/S" -Wait
        Step "Parsec installed — sign in over RDP once and enable 'Host' + start on boot"
    } catch { Step "WARNING: Parsec download failed: $($_.Exception.Message)" }
}

# ---------------------------------------------------------------------------
Step "Repo"
$repo = "D:\src\cs2path"
if (Test-Path "$repo\.git") {
    git -C $repo fetch origin ${repo_branch} 2>&1 | Out-Null
} else {
    git clone --branch ${repo_branch} ${repo_url} $repo 2>&1 | Out-Null
}

# Helper: build the mod against the installed game and deploy it.
@'
param([string]$GameDir = "")
# Locate the game's Managed assemblies. The mod project references these; they are
# NOT redistributable, which is exactly why this build happens here and not in CI.
if (-not $GameDir) {
  foreach ($c in @("D:\Steam\steamapps\common\Cities Skylines II",
                   "C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II")) {
    if (Test-Path $c) { $GameDir = $c; break }
  }
}
if (-not $GameDir) { Write-Error "Cities: Skylines II not found — pass -GameDir"; exit 1 }
$managed = Join-Path $GameDir "Cities2_Data\Managed"
if (-not (Test-Path $managed)) { Write-Error "Managed dir not found under $GameDir"; exit 1 }
Write-Host "Game assemblies: $managed"

$toolpath = [System.Environment]::GetEnvironmentVariable("CSII_TOOLPATH", "User")
if (-not $toolpath -or -not (Test-Path (Join-Path $toolpath "Mod.props"))) {
  Write-Error @"
The official CS2 modding toolchain is not installed.
Launch the game once and enable it under Options -> Modding; it sets the
CSII_TOOLPATH user environment variable that this build imports.
(Current CSII_TOOLPATH: '$toolpath')
"@
  exit 1
}
Write-Host "Toolchain: $toolpath"

$repo = "D:\src\cs2path"
dotnet build "$repo\src\CS2Path.Mod\CS2Path.Mod.csproj" -c Release -p:InGame=true
if ($LASTEXITCODE -ne 0) { Write-Error "build failed"; exit 1 }

$mods = Join-Path $env:LOCALAPPDATA "Colossal Order\Cities Skylines II\Mods\CS2Path"
New-Item -ItemType Directory -Force -Path $mods | Out-Null
Copy-Item "$repo\src\CS2Path.Mod\bin\Release\netstandard2.1\CS2Path.*.dll" $mods -Force
Write-Host "Deployed to $mods"
'@ | Set-Content "C:\cs2\build-mod.ps1"

# Helper: ship a city export to GCS so it can be pulled to your Mac (or fetched
# by a signed URL from anywhere).
@'
param([Parameter(Mandatory=$true)][string]$Path)
$bucket = (Invoke-RestMethod -Headers @{"Metadata-Flavor"="Google"} `
  -Uri "http://metadata.google.internal/computeMetadata/v1/instance/attributes/export-bucket")
$name = Split-Path $Path -Leaf
& gsutil cp $Path "gs://$bucket/$name"
Write-Host "Uploaded gs://$bucket/$name"
Write-Host "Pull it locally with:  cs2 pull $name"
'@ | Set-Content "C:\cs2\upload-export.ps1"

# ---------------------------------------------------------------------------
Step "Claude Desktop"
# Computer use on Windows is Desktop-app only — the CLI's computer-use MCP
# server is macOS-only, so `claude` in a terminal here can drive Bash but not
# the screen. Installing the Desktop app is what makes GUI steps (loading a
# save, reading the profiler) automatable on this box.
$claudeExe = "$env:LOCALAPPDATA\Programs\Claude\Claude.exe"
if (-not (Test-Path $claudeExe) -and -not (Test-Path "$env:ProgramFiles\Claude\Claude.exe")) {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "https://claude.ai/api/desktop/win32/x64/setup/latest/redirect" -OutFile "D:\setup\claude-setup.exe"
        Start-Process "D:\setup\claude-setup.exe" -ArgumentList "/S" -Wait
        Step "Claude Desktop installed — sign in over RDP once, then enable Computer use in Settings"
    } catch { Step "WARNING: Claude Desktop download failed: $($_.Exception.Message)" }
}
# Git for Windows is a prerequisite for the Desktop app's Code tab.
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    try { winget install --id Git.Git -e --silent --accept-source-agreements --accept-package-agreements 2>$null | Out-Null } catch { }
}

# ---------------------------------------------------------------------------
Step "Unattended-desktop helper"
# Not applied automatically: it configures auto-logon and needs the Windows
# password, so it is a deliberate one-time step you run over RDP.
@'
${unattended_desktop_ps1}
'@ | Set-Content "C:\cs2\unattended-desktop.ps1"

# ---------------------------------------------------------------------------
Step "Idle guard"
@'
${idle_guard_ps1}
'@ | Set-Content "C:\cs2\idle-guard.ps1"

$taskName = "cs2-idle-guard"
schtasks /Query /TN $taskName > $null 2>&1
if ($LASTEXITCODE -ne 0) {
    $action  = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\cs2\idle-guard.ps1 -IdleMinutes ${idle_shutdown_minutes} -MaxHours ${max_session_hours} -CheckMinutes 5"
    schtasks /Create /TN $taskName /TR $action /SC MINUTE /MO 5 /RU SYSTEM /RL HIGHEST /F | Out-Null
    Step "Idle guard scheduled (idle ${idle_shutdown_minutes}m, hard cap ${max_session_hours}h)"
}

Step "Bootstrap complete"
Stop-Transcript | Out-Null
