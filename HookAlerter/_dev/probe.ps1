# Sequenced probe: restart the level, then test whether the hook aims at the mouse,
# and whether it swings on its own. Saves windowdc frames for inspection.
param(
    [string]$ProcName = 'GOLD',
    [string]$OutDir   = "$PSScriptRoot\probe",
    [switch]$SkipRestart,
    [switch]$Burst
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\win32.ps1"

$proc = Get-Process -Name $ProcName -ErrorAction Stop | Select-Object -First 1
$h = $proc.MainWindowHandle
[WinCap]::MakeDpiAware()
Start-Sleep -Milliseconds 80

$r = New-Object WinCap+RECT; [WinCap]::GetWindowRect($h, [ref]$r) | Out-Null
function W2S([int]$wx, [int]$wy) { return @(($r.L + $wx), ($r.T + $wy)) }

[WinCap]::BringToFront($h)
Start-Sleep -Milliseconds 300
Write-Host "foreground=$([WinCap]::GetForegroundWindow() -eq $h) winOrigin=($($r.L),$($r.T))"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Save([string]$name) {
    $bmp = [WinCap]::FromWindowDC($h)
    $path = Join-Path $OutDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  saved $name"
}

if (-not $SkipRestart) {
    # "再玩一次" button sits at window-relative ~(1000,716) on the game-over dialog.
    $s = W2S 1000 716
    Write-Host "clicking replay at screen ($($s[0]),$($s[1]))"
    [WinCap]::Click($s[0], $s[1])
    Start-Sleep -Milliseconds 2000
    Save 'after_restart'
}

if ($Burst) {
    # Hold the mouse still well away from the window and watch for autonomous swinging.
    $s = W2S 250 950
    [WinCap]::SetCursorPos($s[0], $s[1]) | Out-Null
    Start-Sleep -Milliseconds 150
    for ($i = 0; $i -lt 40; $i++) {
        Save ('burst_{0:D3}' -f $i)
        Start-Sleep -Milliseconds 60
    }
} else {
    # Aim points in WINDOW coordinates (pivot is around 975,150).
    $aims = @(
        @{ n = 'idle_offwindow'; x = 120;  y = 1020 },
        @{ n = 'far_left';       x = 300;  y = 900  },
        @{ n = 'left';           x = 650;  y = 800  },
        @{ n = 'straight_down';  x = 975;  y = 1000 },
        @{ n = 'right';          x = 1300; y = 800  },
        @{ n = 'far_right';      x = 1700; y = 900  },
        @{ n = 'up_left';        x = 500;  y = 260  },
        @{ n = 'up_right';       x = 1450; y = 260  }
    )
    foreach ($a in $aims) {
        $s = W2S $a.x $a.y
        [WinCap]::SetCursorPos($s[0], $s[1]) | Out-Null
        Start-Sleep -Milliseconds 260
        Write-Host "aim $($a.n) -> window($($a.x),$($a.y))"
        Save $a.n
    }
}
Write-Host "done -> $OutDir"
