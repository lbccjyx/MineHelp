param(
    [Parameter(Mandatory=$true)][string]$Path,
    [string]$Crop,                 # "x,y,w,h" in source pixels
    [int]$Scale = 1,
    [string]$Out,
    [switch]$Stats,                # dominant colours + play-field bbox
    [int]$Top = 24
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ('ImgTool' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;

public class ImgTool {
    // ---- fast 32bpp access ------------------------------------------------
    public class Px : IDisposable {
        public Bitmap Bmp; public int W, H; public int[] A;
        BitmapData _bd; IntPtr _p; byte[] _buf;
        public Px(Bitmap b) {
            Bmp = b; W = b.Width; H = b.Height;
            _bd = b.LockBits(new Rectangle(0,0,W,H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            _p = _bd.Scan0;
            _buf = new byte[Math.Abs(_bd.Stride) * H];
            Marshal.Copy(_p, _buf, 0, _buf.Length);
            A = new int[W*H];
            for (int y=0;y<H;y++) for (int x=0;x<W;x++) {
                int o = y*_bd.Stride + x*4;
                A[y*W+x] = (_buf[o+3]<<24) | (_buf[o+2]<<16) | (_buf[o+1]<<8) | _buf[o];
            }
        }
        public void Dispose(){ Bmp.UnlockBits(_bd); }
        public int At(int x,int y){ return A[y*W+x]; }
        public static int R(int c){return (c>>16)&0xFF;} public static int G(int c){return (c>>8)&0xFF;} public static int B(int c){return c&0xFF;}
        public static double Lum(int c){ return 0.299*R(c)+0.587*G(c)+0.114*B(c); }
        public static double Sat(int c){ int r=R(c),g=G(c),b=B(c); int mx=Math.Max(r,Math.Max(g,b)), mn=Math.Min(r,Math.Min(g,b)); return mx==0?0:(double)(mx-mn)/mx; }
    }

    public static Bitmap Load(string p){ using(var f=new Bitmap(p)) { return new Bitmap(f); } }

    public static Bitmap CropScale(Bitmap src, Rectangle r, int scale) {
        var outp = new Bitmap(r.Width*scale, r.Height*scale, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(outp)) {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode  = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(src, new Rectangle(0,0,outp.Width,outp.Height), r, GraphicsUnit.Pixel);
        }
        return outp;
    }

    // ---- dominant colours -------------------------------------------------
    public static string Dominant(Bitmap b, int top) {
        using (var px = new Px(b)) {
            var hist = new Dictionary<int,int>();
            for (int y=0;y<px.H;y++) for (int x=0;x<px.W;x++) {
                int c = px.At(x,y);
                if (((c>>24)&0xFF) < 128) continue;
                int q = ((Px.R(c)>>3)<<10) | ((Px.G(c)>>3)<<5) | (Px.B(c)>>3); // 5 bits/channel
                int v; hist.TryGetValue(q, out v); hist[q] = v+1;
            }
            var list = new List<KeyValuePair<int,int>>(hist);
            list.Sort((a,bb)=>bb.Value.CompareTo(a.Value));
            var sb = new StringBuilder();
            int total = px.W*px.H;
            for (int i=0;i<Math.Min(top,list.Count);i++) {
                int q = list[i].Key;
                int r = ((q>>10)&31)*8+4, g = ((q>>5)&31)*8+4, bl = (q&31)*8+4;
                sb.AppendLine(string.Format("  #{0:X2}{1:X2}{2:X2}  {3,8}  {4,5:F2}%", r,g,bl, list[i].Value, 100.0*list[i].Value/total));
            }
            return sb.ToString();
        }
    }

    // ---- play field: biggest run of "dirt brown" ---------------------------
    public static string FieldBox(Bitmap b) {
        using (var px = new Px(b)) {
            int x0=px.W, x1=-1, y0=px.H, y1=-1;
            for (int y=0;y<px.H;y+=2) for (int x=0;x<px.W;x+=2) {
                int c = px.At(x,y);
                int r=Px.R(c), g=Px.G(c), bl=Px.B(c);
                bool dirt = r>60 && r<200 && r>g+8 && g>=bl && (r-bl)<110 && Px.Lum(c)>60;
                if (dirt) { if(x<x0)x0=x; if(x>x1)x1=x; if(y<y0)y0=y; if(y>y1)y1=y; }
            }
            return string.Format("fieldish bbox x={0}..{1} y={2}..{3} ({4}x{5})", x0,x1,y0,y1,x1-x0,y1-y0);
        }
    }

    // ---- black letterbox / edges -----------------------------------------
    public static string ColumnProfile(Bitmap b) {
        using (var px = new Px(b)) {
            var sb = new StringBuilder();
            int step = Math.Max(1, px.H/12);
            sb.Append("  col  ");
            for (int x=0;x<px.W;x+=px.W/40) sb.Append(string.Format("{0,5}", x));
            sb.AppendLine();
            for (int y=0;y<px.H;y+=step) {
                sb.Append(string.Format("{0,5}  ", y));
                for (int x=0;x<px.W;x+=px.W/40) {
                    int c = px.At(x,y);
                    sb.Append(string.Format("{0,5}", Px.Lum(c).ToString("F0")));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }

    // ---- gold target mask bbox list (connected components) ----------------
    public static string GoldBlobs(Bitmap b, int minPx) {
        using (var px = new Px(b)) {
            int W=px.W,H=px.H;
            var m = new bool[W*H];
            for (int i=0;i<W*H;i++) {
                int c = px.A[i];
                if (((c>>24)&0xFF)<128) continue;
                int r=Px.R(c), g=Px.G(c), bl=Px.B(c);
                // saturated yellow / gold
                if (r>180 && g>140 && bl<120 && r-bl>90 && r>=g) m[i]=true;
            }
            var seen = new bool[W*H];
            var comps = new List<int[]>();
            var st = new Stack<int>();
            for (int i=0;i<W*H;i++) {
                if (!m[i] || seen[i]) continue;
                int n=0, ax0=W,ax1=-1,ay0=H,ay1=-1; st.Clear(); st.Push(i); seen[i]=true;
                while(st.Count>0){
                    int p=st.Pop(); int x=p%W, y=p/W; n++;
                    if(x<ax0)ax0=x; if(x>ax1)ax1=x; if(y<ay0)ay0=y; if(y>ay1)ay1=y;
                    for(int d=0;d<4;d++){
                        int nx=x+(d==0?1:d==1?-1:0), ny=y+(d==2?1:d==3?-1:0);
                        if(nx<0||ny<0||nx>=W||ny>=H) continue;
                        int q=ny*W+nx;
                        if(m[q]&&!seen[q]){seen[q]=true; st.Push(q);}
                    }
                }
                if(n>=minPx) comps.Add(new int[]{ax0,ay0,ax1,ay1,n});
            }
            comps.Sort((a,bb)=>bb[4].CompareTo(a[4]));
            var sb=new StringBuilder();
            sb.AppendLine("gold blobs (x0,y0,x1,y1,px,cx,cy):");
            foreach(var c in comps) sb.AppendLine(string.Format("  {0,5},{1,5} - {2,5},{3,5}  px={4,7}  c=({5},{6})",
                c[0],c[1],c[2],c[3],c[4],(c[0]+c[2])/2,(c[1]+c[3])/2));
            return sb.ToString();
        }
    }
}
'@ -ReferencedAssemblies System.Drawing
}

$src = [ImgTool]::Load($Path)
Write-Host "input: $Path  $($src.Width)x$($src.Height)"

if ($Stats) {
    Write-Host "`n=== dominant colours ==="; Write-Host ([ImgTool]::Dominant($src, $Top))
    Write-Host "=== field box ===";       Write-Host ([ImgTool]::FieldBox($src))
    Write-Host "=== gold blobs ===";      Write-Host ([ImgTool]::GoldBlobs($src, 80))
    Write-Host "=== luminance profile ==="; Write-Host ([ImgTool]::ColumnProfile($src))
}

if ($Crop) {
    $p = $Crop.Split(',') | ForEach-Object { [int]$_ }
    $r = New-Object System.Drawing.Rectangle $p[0],$p[1],$p[2],$p[3]
    $r.Intersect((New-Object System.Drawing.Rectangle 0,0,$src.Width,$src.Height))
    $dst = if ($Out) { $Out } else { [IO.Path]::ChangeExtension($Path, $null) + "_crop.png" }
    $sc = [ImgTool]::CropScale($src, $r, $Scale)
    $sc.Save($dst, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "crop $($r.X),$($r.Y) $($r.Width)x$($r.Height) x$Scale -> $dst ($($sc.Width)x$($sc.Height))"
    $sc.Dispose()
}
$src.Dispose()
