# Measure play-field geometry and locate structural objects (rocks/hook) in a captured frame.
param(
    [Parameter(Mandatory=$true)][string]$Path,
    [int]$Top = 0,
    [int]$Bottom = 0
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('Meas' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;

public class Meas {
    public static int[] _a; public static int _w, _h;
    public static void Load(Bitmap b) {
        _w=b.Width; _h=b.Height;
        var bd=b.LockBits(new Rectangle(0,0,_w,_h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var buf=new byte[Math.Abs(bd.Stride)*_h];
        Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
        _a=new int[_w*_h];
        for(int y=0;y<_h;y++) for(int x=0;x<_w;x++){
            int o=y*bd.Stride+x*4;
            _a[y*_w+x]=(buf[o+2]<<16)|(buf[o+1]<<8)|buf[o];
        }
        b.UnlockBits(bd);
    }
    static int R(int c){return (c>>16)&255;} static int G(int c){return (c>>8)&255;} static int B(int c){return c&255;}
    static double Lum(int c){return 0.299*R(c)+0.587*G(c)+0.114*B(c);}

    public static bool Dirt(int c){int r=R(c),g=G(c),b=B(c); return r>85&&r<225&&r>g+18&&g>b+4&&(r-b)>45&&(r-b)<195;}
    public static bool Gold(int c){int r=R(c),g=G(c),b=B(c); return r>225&&g>170&&b<70;}
    public static bool Gray(int c){int r=R(c),g=G(c),b=B(c);
        int d1=Math.Abs(r-g),d2=Math.Abs(g-b),d3=Math.Abs(r-b);
        double l=Lum(c); return d1<16&&d2<16&&d3<16&&l>=55&&l<=220;}
    public static bool White(int c){return Lum(c)>225 && Math.Abs(R(c)-B(c))<40;}

    public static string Field() {
        var colCnt=new int[_w]; var rowCnt=new int[_h];
        for(int y=0;y<_h;y++) for(int x=0;x<_w;x++) if(Dirt(_a[y*_w+x])){colCnt[x]++; rowCnt[y]++;}
        int bestX0=0,bestX1=0,best=0,cur0=-1;
        for(int x=0;x<_w;x++){
            bool ok = colCnt[x] > _h*0.45;
            if(ok){ if(cur0<0) cur0=x; if(x-cur0>best){best=x-cur0; bestX0=cur0; bestX1=x;} }
            else cur0=-1;
        }
        int bestY0=0,bestY1=0,best2=0,cur1=-1;
        for(int y=0;y<_h;y++){
            bool ok = rowCnt[y] > (_w*0.45);
            if(ok){ if(cur1<0) cur1=y; if(y-cur1>best2){best2=y-cur1; bestY0=cur1; bestY1=y;} }
            else cur1=-1;
        }
        return string.Format("field x={0}..{1} (w={2})  y={3}..{4} (h={5})  centreX={6}",
            bestX0,bestX1,bestX1-bestX0,bestY0,bestY1,bestY1-bestY0,(bestX0+bestX1)/2);
    }

    // connected components over a predicate, restricted to a window
    public static string Blobs(string which, int y0, int y1, int x0, int x1, int minPx, int topN) {
        int W=x1-x0, H=y1-y0;
        var m=new bool[W*H];
        for(int y=0;y<H;y++) for(int x=0;x<W;x++){
            int c=_a[(y+y0)*_w+(x+x0)];
            m[y*W+x] = which=="gray" ? Gray(c) : (which=="gold" ? Gold(c) : White(c));
        }
        var seen=new bool[W*H]; var res=new List<int[]>(); var st=new Stack<int>();
        for(int i=0;i<W*H;i++){
            if(!m[i]||seen[i]) continue;
            int n=0,ax0=W,ax1=-1,ay0=H,ay1=-1; long sr=0,sg=0,sb=0;
            st.Clear(); st.Push(i); seen[i]=true;
            while(st.Count>0){
                int p=st.Pop(); int x=p%W,y=p/W; n++;
                if(x<ax0)ax0=x; if(x>ax1)ax1=x; if(y<ay0)ay0=y; if(y>ay1)ay1=y;
                int cc=_a[(y+y0)*_w+(x+x0)]; sr+=R(cc); sg+=G(cc); sb+=B(cc);
                for(int d=0;d<4;d++){
                    int nx=x+(d==0?1:d==1?-1:0), ny=y+(d==2?1:d==3?-1:0);
                    if(nx<0||ny<0||nx>=W||ny>=H) continue;
                    int q=ny*W+nx; if(m[q]&&!seen[q]){seen[q]=true; st.Push(q);}
                }
            }
            if(n>=minPx) res.Add(new int[]{ax0+x0,ay0+y0,ax1+x0,ay1+y0,n,(int)(sr/n),(int)(sg/n),(int)(sb/n)});
        }
        res.Sort((p,q)=>q[4].CompareTo(p[4]));
        var sb2=new StringBuilder();
        sb2.AppendLine(which+" blobs (x0,y0,x1,y1,px,avgRGB) in ["+x0+","+y0+"-"+x1+","+y1+"]:");
        for(int i=0;i<Math.Min(topN,res.Count);i++){
            var r=res[i];
            sb2.AppendLine(string.Format("  ({0,4},{1,4})-({2,4},{3,4}) px={4,7} avg=#{5:X2}{6:X2}{7:X2} c=({8},{9})",
                r[0],r[1],r[2],r[3],r[4],r[5],r[6],r[7],(r[0]+r[2])/2,(r[1]+r[3])/2));
        }
        return sb2.ToString();
    }
}
'@ -ReferencedAssemblies System.Drawing
}

$resolved = (Resolve-Path -LiteralPath $Path).Path
$bmp = New-Object System.Drawing.Bitmap $resolved
[Meas]::Load($bmp)
Write-Output "image $($bmp.Width)x$($bmp.Height)"
Write-Output ([Meas]::Field())
Write-Output ([Meas]::Blobs('gray', 0, $bmp.Height, 0, $bmp.Width, 60, 18))
Write-Output ([Meas]::Blobs('gold', 0, $bmp.Height, 0, $bmp.Width, 150, 12))
$bmp.Dispose()
