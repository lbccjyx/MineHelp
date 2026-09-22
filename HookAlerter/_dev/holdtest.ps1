# Which input (if any) makes the idle hook change angle?
# Holds each candidate key in turn and measures how much the pivot area changes.
param(
    [string]$ProcName = 'GOLD',
    [int]$Frames = 8,
    [int]$IntervalMs = 120
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\win32.ps1"

if (-not ('DiffTool' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public class DiffTool {
    public static int[] Grab(Bitmap b) {
        var bd = b.LockBits(new Rectangle(0,0,b.Width,b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var buf = new byte[Math.Abs(bd.Stride)*b.Height];
        Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
        var a = new int[b.Width*b.Height];
        for (int y=0;y<b.Height;y++) for (int x=0;x<b.Width;x++) {
            int o = y*bd.Stride + x*4;
            a[y*b.Width+x] = (buf[o+3]<<24)|(buf[o+2]<<16)|(buf[o+1]<<8)|buf[o];
        }
        b.UnlockBits(bd);
        return a;
    }
    public static string Compare(int[] a, int[] b, int w, int h) {
        int x0=w,x1=-1,y0=h,y1=-1,n=0;
        for (int y=0;y<h;y++) for (int x=0;x<w;x++) {
            int p=a[y*w+x], q=b[y*w+x];
            int d=Math.Abs(((p>>16)&255)-((q>>16)&255))+Math.Abs(((p>>8)&255)-((q>>8)&255))+Math.Abs((p&255)-(q&255));
            if (d>36) { n++; if(x<x0)x0=x; if(x>x1)x1=x; if(y<y0)y0=y; if(y>y1)y1=y; }
        }
        if (n==0) return "no change";
        return string.Format("px={0,-7} bbox x={1}..{2} y={3}..{4} ({5}x{6})", n, x0,x1,y0,y1, x1-x0, y1-y0);
    }
}
'@ -ReferencedAssemblies System.Drawing
}

$proc = Get-Process -Name $ProcName -ErrorAction Stop | Select-Object -First 1
$h = $proc.MainWindowHandle
[WinCap]::MakeDpiAware(); Start-Sleep -Milliseconds 80
$r = New-Object WinCap+RECT; [WinCap]::GetWindowRect($h, [ref]$r) | Out-Null

function Px([int]$wx, [int]$wy) { $b=[WinCap]::FromWindowDC($h); $c=$b.GetPixel($wx,$wy); $b.Dispose(); return $c }
function InLevel {
    $a = Px 300 100; $b = Px 975 900
    return (($a.R -gt 200 -and $a.G -gt 160 -and $a.B -lt 130 -and ($a.R-$a.B) -gt 90) -and
            ($b.R -gt 60 -and $b.R -lt 215 -and $b.R -gt $b.G -and $b.G -ge $b.B -and ($b.R-$b.B) -gt 25 -and ($b.R-$b.B) -lt 150))
}
function ClickW([int]$wx,[int]$wy){ [WinCap]::Click(($r.L+$wx),($r.T+$wy)) }

[WinCap]::BringToFront($h); Start-Sleep -Milliseconds 300
for ($i=0; $i -lt 4; $i++) {
    if (InLevel) { break }
    ClickW 1000 716; Start-Sleep -Milliseconds 2500
    if (InLevel) { break }
    ClickW 1455 409; Start-Sleep -Milliseconds 2500
}
if (-not (InLevel)) { throw "not in a live level" }
Write-Host "in level`n"

# Keep the cursor far from the play field so it cannot interfere.
[WinCap]::SetCursorPos(($r.L + 30), ($r.T + 1060)) | Out-Null

$keys = @(
    @{ n='LEFT';  vk=0x25 }, @{ n='RIGHT'; vk=0x27 }, @{ n='UP'; vk=0x26 },
    @{ n='SPACE'; vk=0x20 }, @{ n='CTRL'; vk=0x11 }, @{ n='Z'; vk=0x5A },
    @{ n='A'; vk=0x41 }, @{ n='D'; vk=0x44 }, @{ n='W'; vk=0x57 }
)
foreach ($k in $keys) {
    if (-not (InLevel)) { Write-Host "level ended"; break }
    $bmp = [WinCap]::FromWindowDC($h); $first = [DiffTool]::Grab($bmp); $w=$bmp.Width; $ht=$bmp.Height; $bmp.Dispose()
    [WinCap]::KeyDown([byte]$k.vk)
    Start-Sleep -Milliseconds 60
    $worst = $null
    for ($i=0; $i -lt $Frames; $i++) {
        Start-Sleep -Milliseconds $IntervalMs
        $b2 = [WinCap]::FromWindowDC($h); $cur = [DiffTool]::Grab($b2); $b2.Dispose()
        $d = [DiffTool]::Compare($first, $cur, $w, $ht)
        if ($d -ne 'no change') { $worst = $d }
    }
    [WinCap]::KeyUp([byte]$k.vk)
    Write-Host ("{0,-6} : {1}" -f $k.n, ($(if ($worst) { $worst } else { 'no change' })))
    Start-Sleep -Milliseconds 400
}
Write-Host "done"
