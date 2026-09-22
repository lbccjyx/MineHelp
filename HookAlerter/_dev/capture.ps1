# Dev helper: capture the Gold Miner window with several methods so we can pick one that works.
param(
    [string]$ProcName = 'GOLD',
    [string]$OutDir   = "$PSScriptRoot\frames",
    [int]$Count       = 1,
    [int]$IntervalMs  = 200,
    [switch]$BringToFront,
    [int]$ClickX = -1,
    [int]$ClickY = -1,
    [switch]$UsePostMessage,
    [string[]]$Methods = @('screen', 'windowdc', 'printwindow'),
    [int]$KeyVk = -1,
    [int]$MoveX = -1,
    [int]$MoveY = -1
)


. "$PSScriptRoot\win32.ps1"

$proc = Get-Process -Name $ProcName -ErrorAction Stop | Select-Object -First 1
$h = $proc.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { throw "no main window for $ProcName" }

[WinCap]::MakeDpiAware()
Start-Sleep -Milliseconds 100
Write-Host "DPI-aware screen size: $([WinCap]::ScreenSize())"

$prevFg = [WinCap]::GetForegroundWindow()
if ($BringToFront) { [WinCap]::BringToFront($h); Start-Sleep -Milliseconds 250 }

if ($ClickX -ge 0 -and $ClickY -ge 0) {
    if ($UsePostMessage) {
        $cp = [WinCap]::ScreenToClientPt($h, $ClickX, $ClickY)
        Write-Host "posting click -> client ($($cp.X),$($cp.Y))"
        [WinCap]::PostClick($h, $cp.X, $cp.Y)
    } else {
        Write-Host "clicking screen ($ClickX,$ClickY)"
        [WinCap]::Click($ClickX, $ClickY)
    }
    Start-Sleep -Milliseconds 200
}

if ($KeyVk -ge 0) {
    Write-Host "sending vk=$KeyVk"
    [WinCap]::Key([byte]$KeyVk)
}

if ($MoveX -ge 0 -and $MoveY -ge 0) {
    Write-Host "moving cursor to ($MoveX,$MoveY)"
    [WinCap]::SetCursorPos($MoveX, $MoveY) | Out-Null
    Start-Sleep -Milliseconds 120
}

$r = New-Object WinCap+RECT; [WinCap]::GetWindowRect($h, [ref]$r) | Out-Null
$c = New-Object WinCap+RECT; [WinCap]::GetClientRect($h, [ref]$c) | Out-Null
$pt = New-Object WinCap+POINT; [WinCap]::ClientToScreen($h, [ref]$pt) | Out-Null
Write-Host "HWND=$h rect=($($r.L),$($r.T))-$($r.R),$($r.B) size=$($r.R-$r.L)x$($r.B-$r.T) client=$($c.R)x$($c.B) origin=($($pt.X),$($pt.Y))"
Write-Host "clientOffsetInWindow=($($pt.X-$r.L),$($pt.Y-$r.T))  isForeground=$([WinCap]::GetForegroundWindow() -eq $h)"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

for ($i = 0; $i -lt $Count; $i++) {
    $tag = '{0:D3}' -f $i
    foreach ($m in $Methods) {
        $bmp = switch ($m) {
            'screen'      { [WinCap]::FromScreen($h) }
            'windowdc'    { [WinCap]::FromWindowDC($h) }
            'printwindow' { [WinCap]::FromPrintWindow($h, 0) }
        }
        $desc = [WinCap]::Describe($bmp)
        $path = Join-Path $OutDir "$tag`_$m.png"
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Host "  $tag $m -> $desc"
    }
    if ($i -lt $Count - 1) { Start-Sleep -Milliseconds $IntervalMs }
}

$desk = [WinCap]::FullDesktop()
$desk.Save((Join-Path $OutDir 'desktop.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "desktop -> $([WinCap]::Describe($desk)) size=$($desk.Width)x$($desk.Height)"
$desk.Dispose()

if ($BringToFront -and $prevFg -ne [IntPtr]::Zero) { [WinCap]::SetForegroundWindow($prevFg) | Out-Null }
