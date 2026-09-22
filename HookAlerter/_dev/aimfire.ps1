# Determine how the launch angle is decided.
# Navigates to a live level, then fires the hook with the cursor at several known
# positions, saving a frame immediately after each shot.
param(
    [string]$ProcName = 'GOLD',
    [string]$OutDir   = "$PSScriptRoot\fire"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\win32.ps1"

$proc = Get-Process -Name $ProcName -ErrorAction Stop | Select-Object -First 1
$h = $proc.MainWindowHandle
[WinCap]::MakeDpiAware(); Start-Sleep -Milliseconds 80
$r = New-Object WinCap+RECT; [WinCap]::GetWindowRect($h, [ref]$r) | Out-Null
$winL = $r.L; $winT = $r.T
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Rgb([int]$wx, [int]$wy) {
    $b = [WinCap]::FromWindowDC($h)
    $c = $b.GetPixel($wx, $wy); $b.Dispose(); return $c
}
function IsYellow($c) { return ($c.R -gt 200 -and $c.G -gt 160 -and $c.B -lt 130 -and ($c.R - $c.B) -gt 90) }
function IsBrown($c)  { return ($c.R -gt 60 -and $c.R -lt 215 -and $c.R -gt $c.G -and $c.G -ge $c.B -and ($c.R-$c.B) -gt 25 -and ($c.R-$c.B) -lt 150) }

function InLevel {
    $a = Rgb 300 100     # HUD bar - yellow only while a level is running
    $b = Rgb 975 900     # deep play field - brown dirt
    return (IsYellow $a) -and (IsBrown $b)
}

function Save([string]$n) {
    $bmp = [WinCap]::FromWindowDC($h)
    $bmp.Save((Join-Path $OutDir "$n.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function ClickW([int]$wx, [int]$wy) { [WinCap]::Click(($winL + $wx), ($winT + $wy)) }
function MoveW([int]$wx, [int]$wy)  { [WinCap]::SetCursorPos(($winL + $wx), ($winT + $wy)) | Out-Null }

[WinCap]::BringToFront($h)
Start-Sleep -Milliseconds 300
Write-Host "foreground=$([WinCap]::GetForegroundWindow() -eq $h)"

for ($i = 0; $i -lt 4; $i++) {
    if (InLevel) { Write-Host "already in level"; break }
    Write-Host "attempt $i - not in level (HUD=$(Rgb 300 100) field=$(Rgb 975 900))"
    ClickW 1000 716; Start-Sleep -Milliseconds 2500
    if (InLevel) { Write-Host "level via replay"; break }
    ClickW 1455 409; Start-Sleep -Milliseconds 2500
    if (InLevel) { Write-Host "level via play-now"; break }
}
if (-not (InLevel)) { Save 'not_in_level'; throw "could not reach a live level - see $OutDir\not_in_level.png" }

Save 'level_start'

# Cursor targets in WINDOW coords, chosen to span a wide range of directions.
$targets = @(
    @{ n = 't_left';   x = 300;  y = 950 },
    @{ n = 't_right';  x = 1650; y = 950 },
    @{ n = 't_down';   x = 975;  y = 1005 }
)
foreach ($t in $targets) {
    MoveW $t.x $t.y
    Start-Sleep -Milliseconds 200
    [WinCap]::Key(0x28)          # VK_DOWN
    Start-Sleep -Milliseconds 130
    Save "$($t.n)_fire"
    Start-Sleep -Milliseconds 900
    Save "$($t.n)_run"
    Write-Host "fired toward window($($t.x),$($t.y)) -> $($t.n)"
    Start-Sleep -Milliseconds 4200   # let the hook retract
}
Write-Host "done -> $OutDir"


