// HookAlerter - Gold Miner: Classic Edition
// Turns the hook red ~LeadMs before the swing lines up with a valuable target.
//
// Capture: GetWindowDC + BitBlt on the game window (works even when the game is
// not the foreground window). No injection, no process memory access.
//
// Build (no SDK needed):
//   csc.exe /target:exe /out:HookAlerter.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll HookAlerter.cs
//
// Self-test against a saved frame:
//   HookAlerter.exe --selftest frame.png

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;


namespace HookAlerter
{
    internal static partial class Program
    {
        static void DoRecord(WindowCapture cap, Vision v, double seconds)
        {
            string dir = Path.Combine(ToolDir(), "diag");
            try { Directory.CreateDirectory(dir); } catch { }
            try { foreach (string old in Directory.GetFiles(dir, "*.png")) File.Delete(old); } catch { }

            StringBuilder rep = new StringBuilder();
            rep.AppendLine("HookAlerter frame recording");
            Frame prev = null;
            int saved = 0, idx = 0;
            DateTime t0 = DateTime.Now;
            v.Field = Rectangle.Empty;

            while ((DateTime.Now - t0).TotalSeconds < seconds)
            {
                Frame f = cap.Grab();
                if (f == null) { Thread.Sleep(30); continue; }
                if (v.Field.Width == 0)
                {
                    v.Field = v.DetectField(f);
                    v.LearnObjects(f);
                    v.ArtPivot();
                    rep.AppendLine("field=" + v.Field + "  client=" + f.W + "x" + f.H +
                                   string.Format(CultureInfo.InvariantCulture, "  artPivot=({0:F0},{1:F0}) r={2:F0}",
                                   v.PivotX, v.PivotY, v.BaseR));
                }
                bool changed = prev == null || Vision.FrameChanged(f, prev, 11, 40);
                if (changed)
                {
                    int n = 0, x0 = f.W, x1 = -1, y0 = f.H, y1 = -1;
                    if (prev != null)
                    {
                        for (int y = 0; y < f.H; y++)
                            for (int x = 0; x < f.W; x++)
                            {
                                int p = f.P[y * f.W + x], q = prev.P[y * f.W + x];
                                int dd = Math.Abs(Cls.R(p) - Cls.R(q)) + Math.Abs(Cls.G(p) - Cls.G(q)) + Math.Abs(Cls.B(p) - Cls.B(q));
                                if (dd > 45) { n++; if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
                            }
                    }
                    rep.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "#{0} t={1:F2}s changedpx={2} bbox=({3},{4})-({5},{6})", idx, (DateTime.Now - t0).TotalSeconds, n, x0, y0, x1, y1));
                    if (prev != null)
                        foreach (Vision.Blob b in v.MovingBlobs(f, prev, 20, 500))
                            rep.AppendLine(string.Format(CultureInfo.InvariantCulture,
                                "     mover ({0},{1})-({2},{3}) n={4} gray={5:F2} hookLike={6}",
                                b.X0, b.Y0, b.X1, b.Y1, b.N, b.GrayFrac, b.HookLike));

                    // what the tracker itself concluded for this frame
                    v.Prev = prev;
                    v.Cur = f;
                    if (prev != null && v.TrackHook(f))
                    {
                        double hr;
                        int kind = v.RayHit(v.Angle, out hr);
                        rep.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "     TRACK angle={0:F1}deg omega={1:F2} r={2:F0} deployed={3} ray->{4}@{5:F0} alert={6}",
                            v.Angle * 180 / Math.PI, v.Omega, v.HookR, v.HookDeployed,
                            Vision.ClassName(kind), hr,
                            (kind == Vision.Bag || kind == Vision.Diamond || kind == Vision.GoldT) && !v.HookDeployed));
                    }
                    else if (prev != null)
                    {
                        rep.AppendLine("     TRACK lost the hook this frame");
                    }
                    if (saved < 40)
                    {
                        try { SaveFramePng(f, Path.Combine(dir, string.Format("f{0:D2}.png", idx))); saved++; }
                        catch { }
                    }
                    idx++;
                }
                prev = f;
                Thread.Sleep(45);
            }
            rep.AppendLine("frames analysed=" + idx + " saved=" + saved);
            try { File.WriteAllText(Path.Combine(dir, "report.txt"), rep.ToString()); } catch { }
            Console.WriteLine("[rec] " + saved + " frames + report.txt -> " + dir);
        }

        // ---- --autoplay <rounds> -------------------------------------------------
        // Drives the game itself so real accuracy can be measured. The tool cannot see the hook
        // move unless the game has focus, so this brings the game forward, parks the real cursor on
        // a chosen object, waits for the swing to reach that line, fires, and records the direction
        // the hook actually took against the direction that was aimed at.
        static int AutoPlay(int rounds)
        {
            IntPtr hwnd = FindGameWindow();
            for (int i = 0; hwnd == IntPtr.Zero && i < 60; i++) { Thread.Sleep(500); hwnd = FindGameWindow(); }
            if (hwnd == IntPtr.Zero) { Console.WriteLine("[auto] GOLD window not found"); return 1; }

            Nat.SetForegroundWindow(hwnd);
            Thread.Sleep(700);

            WindowCapture cap = new WindowCapture(hwnd);
            Cfg cfg = new Cfg();
            Vision v = new Vision(cfg);
            cfg.SettleMs = 500;

            DateTime t0 = DateTime.Now;
            while ((DateTime.Now - t0).TotalSeconds < 90)
            {
                Frame f0 = cap.Grab();
                if (f0 == null) { Thread.Sleep(250); continue; }
                if (v.Field.Width == 0) v.Field = v.DetectField(f0);
                if (v.Field.Width == 0) { Thread.Sleep(250); continue; }
                v.LearnObjects(f0);
                if (v.ObjectFrac > 0.12) { Thread.Sleep(300); continue; }
                v.Cur = f0;
                break;
            }
            if (v.Field.Width == 0) { Console.WriteLine("[auto] never reached a level"); return 1; }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[auto] level field={0} objects={1:F1}%", v.Field, v.ObjectFrac * 100));

            if (!v.Calibrate(cap, 8, -1)) { Console.WriteLine("[auto] calibration failed: " + v.CalibMessage); return 1; }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[auto] pivot=({0:F1},{1:F1}) r={2:F1} art=({3:F0},{4:F0})",
                v.PivotX, v.PivotY, v.BaseR, (v.Field.Left + v.Field.Right) / 2.0,
                v.Field.Top - v.Field.Height * 0.049));

            List<double> errs = new List<double>();
            for (int shot = 0; shot < rounds; shot++)
            {
                // ---- wait until the hook is back and settled ----
                DateTime wait0 = DateTime.Now;
                while ((DateTime.Now - wait0).TotalSeconds < 12)
                {
                    Frame fw = cap.Grab(); if (fw == null) { Thread.Sleep(30); continue; }
                    v.Prev = v.Cur; v.Cur = fw;
                    v.TrackHook(fw);
                    if (!v.HookDeployed && v.HookR > 0
                        && (DateTime.Now - v.LastReturn).TotalSeconds > 0.7) break;
                    Thread.Sleep(20);
                }

                // ---- pick a target ----
                Frame f = cap.Grab(); if (f == null) { Thread.Sleep(100); continue; }
                v.Prev = v.Cur; v.Cur = f;
                if (!v.TrackHook(f)) { Thread.Sleep(100); continue; }
                if (v.Field.Width > 0) v.LearnObjects(f);

                double bestAim = 0, bestCx = 0, bestCy = 0; int bestKind = Vision.None; double bestScore = -1;
                for (double a = -1.3; a <= 1.3; a += 0.02)
                {
                    double hr; int k = v.RayHit(a, out hr);
                    if (k != Vision.GoldT && k != Vision.Bag && k != Vision.Diamond) continue;
                    if (hr < 200) continue;
                    if (v.ObjectArea((int)(v.PivotX + Math.Sin(a) * hr), (int)(v.PivotY + Math.Cos(a) * hr), 40000) <= 0) continue;
                    // prefer something a good way off the current angle so the swing has room
                    double sep = Math.Abs(a - v.Angle);
                    if (sep < 0.45) continue;
                    double score = (k == Vision.GoldT ? 1000 : 2000) + hr * 0.01 - sep;
                    if (score > bestScore)
                    {
                        bestScore = score; bestAim = Math.Atan2(v.LastObjCx - v.PivotX, v.LastObjCy - v.PivotY);
                        bestCx = v.LastObjCx; bestCy = v.LastObjCy; bestKind = k;
                    }
                }
                if (bestKind == Vision.None) { Thread.Sleep(200); shot--; continue; }

                Nat.SetCursorPos(cap.ClientScreenRect.X + (int)bestCx, cap.ClientScreenRect.Y + (int)bestCy);
                Thread.Sleep(60);

                // the aim is the direction to the POINTER, exactly as in the live loop
                Nat.POINT mp; double aim = bestAim;
                if (Nat.GetCursorPos(out mp))
                    aim = Math.Atan2((mp.X - cap.ClientScreenRect.X) - v.PivotX,
                                     (mp.Y - cap.ClientScreenRect.Y) - v.PivotY);

                // ---- run the real firing rule until it triggers ----
                double prevA = v.Angle; double gameStep = 0.14;
                double fireAngle = double.NaN; DateTime fireT = DateTime.Now;
                while ((DateTime.Now - fireT).TotalSeconds < 6)
                {
                    Frame ff = cap.Grab(); if (ff == null) { Thread.Sleep(15); continue; }
                    v.Prev = v.Cur; v.Cur = ff;
                    if (!v.TrackHook(ff)) { Thread.Sleep(15); continue; }
                    double moved = Math.Abs(v.Angle - prevA);
                    if (moved > gameStep) gameStep = moved; else gameStep *= 0.995;
                    double half = Math.Max(0.010, gameStep * 0.5);
                    double d1 = prevA - aim, d2 = v.Angle - aim;
                    bool crossed = (d1 <= 0 && d2 >= 0) || (d1 >= 0 && d2 <= 0);
                    bool nearest = Math.Abs(v.Angle - aim) <= half;
                    prevA = v.Angle;
                    if (crossed || nearest) { fireAngle = v.Angle; break; }
                    Thread.Sleep(10);
                }
                if (double.IsNaN(fireAngle)) { Console.WriteLine("[auto] no crossing within 6s"); continue; }

                Nat.Key(0x28);
                double err = (fireAngle - aim) * 180 / Math.PI;
                errs.Add(err);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[auto] shot{0,2}  aim={1,7:F2}  fired={2,7:F2}  err={3,6:F2}deg  kind={4}  r={5:F0}  step={6:F2}deg",
                    shot, aim * 180 / Math.PI, fireAngle * 180 / Math.PI, err,
                    Vision.ClassName(bestKind), Math.Sqrt((bestCx - v.PivotX) * (bestCx - v.PivotX) + (bestCy - v.PivotY) * (bestCy - v.PivotY)),
                    gameStep * 180 / Math.PI));
                Thread.Sleep(900);
            }

            if (errs.Count > 0)
            {
                double sum = 0, mx = 0; foreach (double e in errs) { sum += e; if (Math.Abs(e) > Math.Abs(mx)) mx = e; }
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[auto] SUMMARY n={0}  mean={1:F2}deg  max|err|={2:F2}deg", errs.Count, sum / errs.Count, mx));
            }
            return 0;
        }

        // Smoke-test hooks: --liverun <seconds> [desktopShot.png] runs the real loop then exits.
        static double RunSeconds = 0;
        static string RunShot = null;

        static int Analyze(string pngPath, double px, double py, double radius, string outPng)
        {
            Bitmap src = new Bitmap(pngPath);
            Frame f = new Frame(src.Width, src.Height);
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                {
                    Color c = src.GetPixel(x, y);
                    f.P[y * src.Width + x] = (c.R << 16) | (c.G << 8) | c.B;
                }
            src.Dispose();

            Cfg cfg = new Cfg();
            Vision v = new Vision(cfg);
            v.Field = v.DetectField(f);
            v.Cur = f;
            v.LearnObjects(f);
            v.ArtPivot();
            Console.WriteLine("field " + v.Field + "   letterbox-ok=" + (v.Field.Left > 4 || v.Field.Right < f.W - 5));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "artPivot ({0:F0},{1:F0}) r={2:F0}", v.PivotX, v.PivotY, v.BaseR));
            if (px > 0 && py > 0 && radius > 0)
            {
                v.PivotX = px; v.PivotY = py; v.BaseR = radius; v.HookR = radius;
            }
            v.HookR = v.BaseR;
            v.Angle = 0; v.HaveAngle = true;
            Console.WriteLine("pivot (" + v.PivotX + "," + v.PivotY + ") R=" + v.BaseR);
            double ropeA, ropeL;
            if (v.FindRope(out ropeA, out ropeL))
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    ">>> ROPE angle={0:F1}deg len={1:F0}px  -> hook at ({2:F0},{3:F0})",
                    ropeA * 180 / Math.PI, ropeL,
                    v.PivotX + Math.Sin(ropeA) * ropeL, v.PivotY + Math.Cos(ropeA) * ropeL));
            else
                Console.WriteLine(">>> ROPE not found");
            Console.WriteLine("angleDeg  class        hitR");
            for (int deg = -80; deg <= 80; deg += 5)
            {
                double hitR;
                int k = v.RayHit(deg * Math.PI / 180.0, out hitR);
                int area = 0;
                if (hitR > 40)
                {
                    double rr = deg * Math.PI / 180.0;
                    area = v.ObjectArea((int)(v.PivotX + Math.Sin(rr) * hitR),
                                        (int)(v.PivotY + Math.Cos(rr) * hitR), 40000);
                }
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,6}    {1,-18} {2,7:F0}   area={3,6}  (min {4})",
                    deg, Vision.ClassName(k), hitR, area, v.MinGoldArea()));
            }
            if (outPng != null)
            {
                v.C.DebugDraw = true;
                using (Bitmap b = new Bitmap(f.W, f.H, PixelFormat.Format32bppArgb))
                {
                    for (int y = 0; y < f.H; y++)
                        for (int x = 0; x < f.W; x++)
                            b.SetPixel(x, y, Color.FromArgb(255, (f.P[y * f.W + x] >> 16) & 255, (f.P[y * f.W + x] >> 8) & 255, f.P[y * f.W + x] & 255));
                    using (Graphics g = Graphics.FromImage(b)) v.DrawOverlay(g, f.W, f.H, false, "analyze");
                    b.Save(outPng, ImageFormat.Png);
                }
                // and a plain view of what the segmenter considers an object
                using (Bitmap m = new Bitmap(f.W, f.H, PixelFormat.Format32bppArgb))
                {
                    for (int y = 0; y < f.H; y++)
                        for (int x = 0; x < f.W; x++)
                        {
                            int c = f.P[y * f.W + x];
                            bool o = v.IsObject(x, y);
                            m.SetPixel(x, y, o
                                ? Color.FromArgb(255, 255, 0, 255)
                                : Color.FromArgb(255, (c >> 16) & 255, (c >> 8) & 255, c & 255));
                        }
                    using (Graphics g = Graphics.FromImage(m))
                    using (Pen p = new Pen(Color.Lime, 3))
                        g.DrawRectangle(p, v.Field);
                    m.Save(outPng.Replace(".png", "_mask.png"), ImageFormat.Png);
                }
                Console.WriteLine("annotated -> " + outPng + "  and  " + outPng.Replace(".png", "_mask.png"));
            }
            return 0;
        }

        // ---- is the hook aimed by the mouse? ---------------------------------
        // Moves the real cursor across the play area and reports where the grey hook blob sits,
        // so we can tell whether its angle tracks the pointer.
        static int AimTest()
        {
            Nat.SetProcessDPIAware();
            IntPtr hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) { Console.WriteLine("game window not found"); return 1; }
            WindowCapture cap = new WindowCapture(hwnd);
            Vision v = new Vision(new Cfg());
            Frame f0 = cap.Grab();
            v.Field = v.DetectField(f0);
            v.Cur = f0;
            v.ArtPivot();
            Rectangle cr = cap.ClientScreenRect;
            Console.WriteLine("field=" + v.Field + "  client=" + cr.Width + "x" + cr.Height);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "artPivot=({0:F0},{1:F0}) artR={2:F0}", v.PivotX, v.PivotY, v.BaseR));
            Console.WriteLine("cursorX -> hook blobs in the top band (bbox, n, centroid, angle)");

            // Does anything animate at all? HUD (timer) and play area reported separately.
            Console.WriteLine("--- animation check (3s) ---");
            Frame pa = cap.Grab();
            DateTime t0 = DateTime.Now;
            int afr = 0;
            while ((DateTime.Now - t0).TotalSeconds < 3.0)
            {
                Frame pb = cap.Grab();
                if (pb == null) { Thread.Sleep(50); continue; }
                int total = 0, hud = 0, play = 0, ax0 = pb.W, ax1 = -1, ay0 = pb.H, ay1 = -1;
                for (int y = 0; y < pb.H; y++)
                    for (int x = 0; x < pb.W; x++)
                    {
                        int p = pb.P[y * pb.W + x], q = pa.P[y * pb.W + x];
                        int d = Math.Abs(Cls.R(p) - Cls.R(q)) + Math.Abs(Cls.G(p) - Cls.G(q)) + Math.Abs(Cls.B(p) - Cls.B(q));
                        if (d > 45)
                        {
                            total++;
                            if (y < v.Field.Top) hud++; else play++;
                            if (x < ax0) ax0 = x; if (x > ax1) ax1 = x;
                            if (y < ay0) ay0 = y; if (y > ay1) ay1 = y;
                        }
                    }
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "frame {0}: changed total={1} hud={2} play={3} bbox=({4},{5})-({6},{7})",
                    afr, total, hud, play, ax0, ay0, ax1, ay1));
                pa = pb; afr++;
                Thread.Sleep(120);
            }
            Console.WriteLine("--- pointer sweep ---");

            double[] fracs = { -0.85, -0.55, -0.25, 0.0, 0.25, 0.55, 0.85 };
            foreach (double fr in fracs)
            {
                int sx = cr.X + (int)(cr.Width * (0.5 + fr * 0.45));
                int sy = cr.Y + (int)(cr.Height * 0.78);
                // move in small steps so the game sees a real stream of mouse-move messages
                for (int k = 0; k < 12; k++)
                {
                    Nat.SetCursorPos(sx - (12 - k) * 3, sy);
                    Thread.Sleep(15);
                }
                Nat.SetCursorPos(sx, sy);
                Thread.Sleep(450);

                Frame f = cap.Grab();
                List<Vision.Blob> blobs = v.GrayBlobs(f, 100, v.Field.Top - 200);
                Console.WriteLine("cursor x=" + sx + " (frac " + fr.ToString("F2", CultureInfo.InvariantCulture) + ")");
                int shown = 0;
                foreach (Vision.Blob b in blobs)
                {
                    if (b.Cy > v.Field.Top + 160) continue;         // only the hook band
                    if (b.X1 - b.X0 > 200 || b.Y1 - b.Y0 > 200) continue;
                    double dx = b.Cx - v.PivotX, dy = b.Cy - v.PivotY;
                    double ang = Math.Atan2(dx, dy) * 180 / Math.PI;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "    ({0},{1})-({2},{3}) n={4} c=({5:F0},{6:F0}) gray={7:F2}  angle={8:F1}deg  r={9:F0}",
                        b.X0, b.Y0, b.X1, b.Y1, b.N, b.Cx, b.Cy, b.GrayFrac, ang, Math.Sqrt(dx * dx + dy * dy)));
                    if (++shown >= 4) break;
                }
                if (shown == 0) Console.WriteLine("    (no grey blob in the hook band)");
            }
            return 0;
        }

        // ---- live capture smoke test -----------------------------------------
        static int CaptureTest(string outPng)
        {
            Nat.SetProcessDPIAware();
            IntPtr hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) { Console.WriteLine("game window not found"); return 1; }
            WindowCapture cap = new WindowCapture(hwnd);
            Console.WriteLine("client " + cap.ClientScreenRect.Width + "x" + cap.ClientScreenRect.Height +
                              " at (" + cap.ClientScreenRect.X + "," + cap.ClientScreenRect.Y + ")");
            Frame f = cap.Grab();
            if (f == null) { Console.WriteLine("capture returned null"); return 1; }
            Console.WriteLine("captured " + f.W + "x" + f.H);
            Dictionary<int, int> hist = new Dictionary<int, int>();
            for (int i = 0; i < f.P.Length; i += 7)
            {
                int c = f.P[i] & 0xF8F8F8;
                if (hist.ContainsKey(c)) hist[c]++; else hist[c] = 1;
            }
            Console.WriteLine("distinct quantised colours: " + hist.Count);
            Console.WriteLine(string.Format("pixels: (100,100)={0:X6} (1000,500)={1:X6} (500,200)={2:X6} (1500,900)={3:X6}",
                f.P[100 * f.W + 100], f.P[500 * f.W + 1000], f.P[200 * f.W + 500], f.P[900 * f.W + 1500]));
            Vision v = new Vision(new Cfg());
            v.Field = v.DetectField(f);
            v.Cur = f;
            Console.WriteLine("field " + v.Field);
            if (outPng != null)
            {
                using (Bitmap b = new Bitmap(f.W, f.H, PixelFormat.Format32bppArgb))
                {
                    for (int y = 0; y < f.H; y++)
                        for (int x = 0; x < f.W; x++)
                            b.SetPixel(x, y, Color.FromArgb(255, (f.P[y * f.W + x] >> 16) & 255, (f.P[y * f.W + x] >> 8) & 255, f.P[y * f.W + x] & 255));
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        using (Pen pen = new Pen(Color.Cyan, 3)) g.DrawRectangle(pen, v.Field);
                        using (Pen pen = new Pen(Color.Magenta, 2))
                            foreach (Vision.Blob bl in v.GrayBlobs(f, 400))
                                g.DrawRectangle(pen, bl.X0, bl.Y0, bl.X1 - bl.X0, bl.Y1 - bl.Y0);
                    }
                    b.Save(outPng, ImageFormat.Png);
                    Console.WriteLine("saved -> " + outPng);
                }
            }
            return 0;
        }

        // ---- live tracker trace (no overlay) ---------------------------------
        static int TraceTest(double seconds)
        {
            Nat.SetProcessDPIAware();
            IntPtr hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero) { Console.WriteLine("game window not found"); return 1; }
            WindowCapture cap = new WindowCapture(hwnd);
            Console.WriteLine("client " + cap.ClientScreenRect.Width + "x" + cap.ClientScreenRect.Height);
            Vision v = new Vision(new Cfg());
            Frame prev = null;
            List<double> xs = new List<double>(), ys = new List<double>();
            DateTime t0 = DateTime.Now;
            int shown = 0;
            while ((DateTime.Now - t0).TotalSeconds < seconds)
            {
                Frame f = cap.Grab();
                if (f == null) { Thread.Sleep(30); continue; }
                if (v.Field.Width == 0)
                {
                    v.Field = v.DetectField(f);
                    Console.WriteLine("field " + v.Field);
                }
                if (prev != null)
                {
                    bool ch = Vision.FrameChanged(f, prev, 11, 40);
                    if (ch)
                    {
                        List<Vision.Blob> mv = v.MovingBlobs(f, prev, 40, 190);
                        if (shown < 12)
                        {
                            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                "t={0,5:F2}s blobs={1}", (DateTime.Now - t0).TotalSeconds, mv.Count));
                            foreach (Vision.Blob b in mv)
                                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "    bbox=({0},{1})-({2},{3}) size={4} c=({5:F0},{6:F0})",
                                    b.X0, b.Y0, b.X1, b.Y1, b.N, b.Cx, b.Cy));
                            shown++;
                        }
                        foreach (Vision.Blob b in mv) { xs.Add(b.Cx); ys.Add(b.Cy); }
                    }
                }
                prev = f;
                Thread.Sleep(25);
            }
            Console.WriteLine("moving-blob samples: " + xs.Count);
            if (xs.Count >= 12)
            {
                double cx, cy, r;
                if (Vision.CircleFit(xs, ys, out cx, out cy, out r))
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "circle fit: pivot=({0:F1},{1:F1}) r={2:F1}", cx, cy, r));
                else Console.WriteLine("circle fit failed");
            }
            return 0;
        }

        // ---- offline validation against a saved PNG --------------------------
        static int SelfTest(string pngPath)
        {
            Bitmap src = new Bitmap(pngPath);
            Frame f = new Frame(src.Width, src.Height);
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                {
                    Color c = src.GetPixel(x, y);
                    f.P[y * src.Width + x] = (c.R << 16) | (c.G << 8) | c.B;
                }
            src.Dispose();

            Cfg cfg = new Cfg();
            Vision v = new Vision(cfg);
            v.Field = v.DetectField(f);
            v.Cur = f;
            Console.WriteLine("image " + f.W + "x" + f.H);
            Console.WriteLine("field " + v.Field);
            Console.WriteLine("pivot " + v.PivotX + "," + v.PivotY + " baseR " + v.BaseR);

            List<Vision.Blob> blobs = v.GrayBlobs(f, 150);
            Console.WriteLine("grey blobs: " + blobs.Count);
            foreach (Vision.Blob b in blobs)
                Console.WriteLine(string.Format("  ({0},{1})-({2},{3}) n={4} c=({5:F0},{6:F0})",
                    b.X0, b.Y0, b.X1, b.Y1, b.N, b.Cx, b.Cy));
            return 0;
        }
    }
}
