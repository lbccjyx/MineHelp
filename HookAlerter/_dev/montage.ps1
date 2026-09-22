# Tile N frames into one contact sheet (with labels) and/or report per-frame diff vs frame 0.
param(
    [Parameter(Mandatory=$true)][string]$Pattern,
    [string]$Crop,                       # "x,y,w,h" in source px
    [int]$Scale = 1,
    [int]$Cols = 5,
    [string]$Out,
    [switch]$Diff
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ('Mont' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;

public class Mont {
    public class Px : IDisposable {
        public Bitmap Bmp; public int W,H; public int[] A;
        BitmapData _bd; byte[] _buf;
        public Px(Bitmap b) {
            Bmp=b; W=b.Width; H=b.Height;
            _bd=b.LockBits(new Rectangle(0,0,W,H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            _buf=new byte[Math.Abs(_bd.Stride)*H];
            Marshal.Copy(_bd.Scan0,_buf,0,_buf.Length);
            A=new int[W*H];
            for(int y=0;y<H;y++) for(int x=0;x<W;x++){int o=y*_bd.Stride+x*4; A[y*W+x]=(int)((uint)(_buf[o+3]<<24|_buf[o+2]<<16|_buf[o+1]<<8|_buf[o]));}
        }
        public void Dispose(){ Bmp.UnlockBits(_bd); }
        public int At(int x,int y){return A[y*W+x];}
    }
    static int R(int c){return (c>>16)&0xFF;} static int G(int c){return (c>>8)&0xFF;} static int B(int c){return c&0xFF;}

    public static string DiffReport(string[] files) {
        var sb = new StringBuilder();
        Bitmap first = new Bitmap(files[0]);
        var p0 = new Px(first);
        sb.AppendLine(string.Format("frame0 {0}x{1}", p0.W, p0.H));
        for (int i=1;i<files.Length;i++) {
            using (var b = new Bitmap(files[i]))
            using (var p = new Px(b)) {
                int x0=p0.W,x1=-1,y0=p0.H,y1=-1,n=0;
                for(int y=0;y<p.H;y++) for(int x=0;x<p.W;x++){
                    int a=p0.At(x,y), c=p.At(x,y);
                    int d=Math.Abs(R(a)-R(c))+Math.Abs(G(a)-G(c))+Math.Abs(B(a)-B(c));
                    if(d>36){ n++; if(x<x0)x0=x; if(x>x1)x1=x; if(y<y0)y0=y; if(y>y1)y1=y; }
                }
                if (n==0) sb.AppendLine(string.Format("  #{0,-3} no change", i));
                else sb.AppendLine(string.Format("  #{0,-3} changed px={1,-8} bbox x={2}..{3} y={4}..{5} ({6}x{7})",
                        i, n, x0,x1,y0,y1, x1-x0, y1-y0));
            }
        }
        p0.Dispose(); first.Dispose();
        return sb.ToString();
    }

    public static Bitmap Sheet(string[] files, Rectangle crop, int scale, int cols) {
        int cw = crop.Width*scale, ch = crop.Height*scale;
        int rows = (int)Math.Ceiling((double)files.Length/cols);
        int lab = 22;
        var outb = new Bitmap(cols*cw, rows*(ch+lab), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(outb)) {
            g.Clear(Color.FromArgb(255,20,20,20));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using (var f = new Font("Consolas", 12)) using (var br = new SolidBrush(Color.Yellow)) {
                for (int i=0;i<files.Length;i++) {
                    int cx = (i%cols)*cw, cy = (i/cols)*(ch+lab);
                    using (var src = new Bitmap(files[i])) {
                        var r = crop; r.Intersect(new Rectangle(0,0,src.Width,src.Height));
                        g.DrawImage(src, new Rectangle(cx, cy+lab, cw, ch), r, GraphicsUnit.Pixel);
                    }
                    g.DrawString(System.IO.Path.GetFileNameWithoutExtension(files[i]), f, br, cx+3, cy+2);
                }
            }
        }
        return outb;
    }
}
'@ -ReferencedAssemblies System.Drawing
}

$files = @(Get-ChildItem -Path $Pattern -File | Sort-Object Name | ForEach-Object { $_.FullName })
if ($files.Count -eq 0) { throw "no files match $Pattern" }
Write-Host "matched $($files.Count) files"

if ($Diff) { Write-Host ([Mont]::DiffReport($files)) }

if ($Crop) {
    $p = $Crop.Split(',') | ForEach-Object { [int]$_ }
    $rect = New-Object System.Drawing.Rectangle $p[0],$p[1],$p[2],$p[3]
    $sheet = [Mont]::Sheet($files, $rect, $Scale, $Cols)
    if (-not $Out) { $Out = Join-Path (Split-Path $files[0]) "sheet.png" }
    $sheet.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "sheet -> $Out ($($sheet.Width)x$($sheet.Height))"
    $sheet.Dispose()
}
