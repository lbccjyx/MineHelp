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
    internal partial class Vision
    {
        public readonly Cfg C;
        public Frame Cur, Prev;
        public Rectangle Field;
        public double PivotX, PivotY, BaseR;
        public double Angle;                 // radians, 0 = straight down, + = clockwise (to the right)
        public double Omega;                 // rad/s
        public bool HaveAngle;
        public bool HookDeployed;
        public List<Rectangle> StaticRocks = new List<Rectangle>();
        public List<double[]> CalSamples = new List<double[]>();
        public List<Blob> LastCandidates = new List<Blob>();
        public int NoHookFrames;

        // Calibration can go wrong in ways that are only visible in a picture, so always leave one
        // behind: field box, every collected sample, and the fitted pivot / arc.
        public void SaveCalibDump(Frame f, string path)
        {
            if (f == null) return;
            string[] tries;
            if (path != null) tries = new string[] { path };
            else
            {
                string exeDir = null;
                try { exeDir = Path.GetDirectoryName(Application.ExecutablePath); } catch { }
                tries = new string[] { Path.Combine(exeDir, "calib_last.png"),
                                       Path.Combine(Environment.CurrentDirectory, "calib_last.png") };
            }
            foreach (string target in tries)
            {
                if (SaveCalibDumpTo(f, target)) { Log.WriteLine("[cal] debug image -> " + target); return; }
            }
            Log.WriteLine("[cal] could not write the calibration debug image");
        }

        bool SaveCalibDumpTo(Frame f, string path)
        {
            try
            {
                using (Bitmap b = new Bitmap(f.W, f.H, PixelFormat.Format32bppArgb))
                {
                    for (int y = 0; y < f.H; y++)
                        for (int x = 0; x < f.W; x++)
                        {
                            int c = f.P[y * f.W + x];
                            b.SetPixel(x, y, Color.FromArgb(255, (c >> 16) & 255, (c >> 8) & 255, c & 255));
                        }
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        using (Pen p = new Pen(Color.Cyan, 3)) g.DrawRectangle(p, Field);
                        using (SolidBrush sb = new SolidBrush(Color.FromArgb(255, 255, 0, 255)))
                            foreach (double[] s in CalSamples)
                                g.FillRectangle(sb, (float)s[0] - 3, (float)s[1] - 3, 7, 7);
                        // every mover from the last analysed frame, so a rejected candidate is
                        // visible too: orange = rejected (not enough grey), magenta = accepted
                        using (Font fo = new Font("Consolas", 11f, FontStyle.Bold))
                        {
                            foreach (Blob cb in LastCandidates)
                            {
                                bool ok = cb.HookLike;
                                using (Pen p = new Pen(ok ? Color.Magenta : Color.Orange, 2))
                                    g.DrawRectangle(p, cb.X0, cb.Y0, cb.X1 - cb.X0, cb.Y1 - cb.Y0);
                                using (SolidBrush sb = new SolidBrush(ok ? Color.Magenta : Color.Orange))
                                    g.DrawString(string.Format(CultureInfo.InvariantCulture,
                                        "{0:F2} n={1}", cb.GrayFrac, cb.N), fo, sb, cb.X0, cb.Y0 - 16);
                            }
                        }
                        using (Pen p = new Pen(Color.Lime, 3))
                        {
                            g.DrawEllipse(p, (float)(PivotX - BaseR), (float)(PivotY - BaseR), (float)(BaseR * 2), (float)(BaseR * 2));
                            g.DrawLine(p, (float)PivotX - 16, (float)PivotY, (float)PivotX + 16, (float)PivotY);
                            g.DrawLine(p, (float)PivotX, (float)PivotY - 16, (float)PivotX, (float)PivotY + 16);
                        }
                        using (Font fo = new Font("Consolas", 16f, FontStyle.Bold))
                        using (SolidBrush sb = new SolidBrush(Color.Lime))
                            g.DrawString(string.Format(CultureInfo.InvariantCulture,
                                "samples={0}  pivot=({1:F0},{2:F0})  r={3:F0}  field={4}",
                                CalSamples.Count, PivotX, PivotY, BaseR, Field), fo, sb, 12, 10);
                    }
                    b.Save(path, ImageFormat.Png);
                }
                return true;
            }
            catch { return false; }
        }
        public double MinAngle = -1.6, MaxAngle = 1.6;

        public Vision(Cfg c) { C = c; }
        public TextWriter Log = Console.Out;

        // ---- field detection -------------------------------------------------
        // The game repaints the dirt per level (brown on one, sand-yellow on the next), so the
        // field must be found from geometry that never changes: the black letterbox bars down the
        // sides, and the fact that the yellow HUD occupies a fixed strip at the top.
        public Rectangle DetectField(Frame f)
        {
            int midTop = f.H / 3, midBot = f.H * 9 / 10;

            bool[] colDark = new bool[f.W];
            for (int x = 0; x < f.W; x++)
            {
                int dark = 0, tot = 0;
                for (int y = midTop; y < midBot; y += 3)
                {
                    tot++;
                    if (Cls.Lum(f.P[y * f.W + x]) < 45) dark++;
                }
                colDark[x] = tot > 0 && dark > tot * 0.9;
            }
            int L = 0, R = f.W - 1;
            while (L < f.W - 1 && colDark[L]) L++;
            while (R > L + 8 && colDark[R]) R--;

            int T = (int)(f.H * 0.16);                     // the HUD strip
            int B = f.H - 1;
            for (int y = f.H - 1; y > T; y--)              // bottom letterbox, if any
            {
                int dark = 0, tot = 0;
                for (int x = L; x <= R; x += 4) { tot++; if (Cls.Lum(f.P[y * f.W + x]) < 45) dark++; }
                if (tot == 0 || dark < tot * 0.9) { B = y; break; }
            }
            return new Rectangle(L, T, Math.Max(0, R - L), Math.Max(0, B - T));
        }

        // ---- per-level object segmentation -----------------------------------
        // Colour thresholds break as soon as the level repaints the dirt (brown -> sand-yellow),
        // and in the sand theme the gold is almost the same colour as the dirt. What never changes
        // is that every grabbable object is drawn with a dark outline. So: flood the dirt inwards
        // from the field border through everything that is NOT a dark outline. Whatever the flood
        // cannot reach is an object, whatever palette the level uses.
        public bool[] ObjectMask;
        public bool[] OutlineMask;
        public double ObjectFrac;      // fraction of the play area that is object
        int _ow;

        public void LearnObjects(Frame f)
        {
            int W = f.W, H = f.H;
            _ow = W;
            ObjectMask = null;
            if (Field.Width <= 8 || Field.Height <= 8) return;

            // Inset so the miner's ledge along the top of the field and the 1px field border are
            // not mistaken for grabbable objects. The ledge is a wide horizontal band, so this has
            // to clear it completely - leftovers sit right in front of the target and block the ray.
            int x0 = Math.Max(0, Field.Left + 8), x1 = Math.Min(W - 1, Field.Right - 8);
            int y0 = Math.Max(0, Field.Top + 62), y1 = Math.Min(H - 1, Field.Bottom);
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            if (bw < 16 || bh < 16) return;

            // luminance of the play area
            int[] lum = new int[bw * bh];
            for (int y = 0; y < bh; y++)
            {
                int src = (y + y0) * W + x0;
                for (int x = 0; x < bw; x++) lum[y * bw + x] = Cls.Lum(f.P[src + x]);
            }

            // Box-blur it. An outline is where the luminance is well BELOW its local average -
            // that works whether the dirt is dark brown (lum ~90) or pale sand (lum ~230),
            // which a fixed darkness threshold cannot.
            const int R = 24;
            int[] tmp = new int[bw * bh], blur = new int[bw * bh];
            int win = 2 * R + 1;
            for (int y = 0; y < bh; y++)
            {
                int sum = 0;
                for (int k = -R; k <= R; k++) sum += lum[y * bw + Math.Min(bw - 1, Math.Max(0, k))];
                for (int x = 0; x < bw; x++)
                {
                    tmp[y * bw + x] = sum / win;
                    sum += lum[y * bw + Math.Min(bw - 1, x + R + 1)] - lum[y * bw + Math.Max(0, x - R)];
                }
            }
            for (int x = 0; x < bw; x++)
            {
                int sum = 0;
                for (int k = -R; k <= R; k++) sum += tmp[Math.Min(bh - 1, Math.Max(0, k)) * bw + x];
                for (int y = 0; y < bh; y++)
                {
                    blur[y * bw + x] = sum / win;
                    sum += tmp[Math.Min(bh - 1, y + R + 1) * bw + x] - tmp[Math.Max(0, y - R) * bw + x];
                }
            }

            bool[] outline = new bool[W * H];
            for (int y = 0; y < bh; y++)
            {
                int dst = (y + y0) * W + x0;
                for (int x = 0; x < bw; x++)
                    if (lum[y * bw + x] < blur[y * bw + x] - 30) outline[dst + x] = true;
            }

            // Thicken the outline by 2px. Where an object's edge has low contrast against the dirt
            // the detector leaves a gap, and the flood then leaks INTO the object and marks its
            // interior as background - which is exactly how a big nugget became invisible.
            bool[] tmpM = new bool[W * H], fat = new bool[W * H];
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (outline[y * W + x])
                        for (int d = -2; d <= 2; d++)
                        {
                            int xx = x + d;
                            if (xx >= x0 && xx <= x1) tmpM[y * W + xx] = true;
                        }
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (tmpM[y * W + x])
                        for (int d = -2; d <= 2; d++)
                        {
                            int yy = y + d;
                            if (yy >= y0 && yy <= y1) fat[yy * W + x] = true;
                        }
            outline = fat;
            OutlineMask = outline;

            // ---- object mask: local contrast, NOT connectivity --------------------
            // The previous version flood-filled the dirt inwards and called whatever it could not
            // reach an object. That needs every object's outline to be perfectly closed, and one
            // faint edge (a nugget's bottom against a pale band) let the flood pour inside and made
            // a big nugget invisible. Marking objects directly from local contrast has no such
            // failure mode.
            //
            // Background is estimated as the per-cell MEDIAN colour on a coarse grid. A 48px cell
            // is mostly dirt even when an object overlaps it, so the median is the dirt - and being
            // two-dimensional it follows the wavy colour bands that defeated a per-row median.
            const int CELL = 48;
            int gc = (bw + CELL - 1) / CELL, gr = (bh + CELL - 1) / CELL;
            int[] bgCell = new int[gc * gr];
            int[] hr = new int[256], hg = new int[256], hb = new int[256];
            for (int cy = 0; cy < gr; cy++)
                for (int cx = 0; cx < gc; cx++)
                {
                    Array.Clear(hr, 0, 256); Array.Clear(hg, 0, 256); Array.Clear(hb, 0, 256);
                    int ax = Math.Min(x1, x0 + cx * CELL + CELL - 1);
                    int ay = Math.Min(y1, y0 + cy * CELL + CELL - 1);
                    int n = 0;
                    for (int y = y0 + cy * CELL; y <= ay; y++)
                        for (int x = x0 + cx * CELL; x <= ax; x++)
                        {
                            int c = f.P[y * W + x];
                            hr[Cls.R(c)]++; hg[Cls.G(c)]++; hb[Cls.B(c)]++; n++;
                        }
                    bgCell[cy * gc + cx] = n < 64 ? -1 : (Median(hr, n) << 16) | (Median(hg, n) << 8) | Median(hb, n);
                }

            ObjectMask = new bool[W * H];
            int objPx = 0;
            for (int y = y0; y <= y1; y++)
            {
                int off = y * W;
                int cy = (y - y0) / CELL;
                for (int x = x0; x <= x1; x++)
                {
                    int p = off + x;
                    bool obj = outline[p];
                    if (!obj)
                    {
                        int bg = bgCell[cy * gc + (x - x0) / CELL];
                        if (bg >= 0)
                        {
                            int c = f.P[p];
                            int d = Math.Abs(Cls.R(c) - Cls.R(bg)) + Math.Abs(Cls.G(c) - Cls.G(bg)) + Math.Abs(Cls.B(c) - Cls.B(bg));
                            if (d > 110) obj = true;
                        }
                    }
                    if (obj) { ObjectMask[p] = true; objPx++; }
                }
            }
            // Only log when the figure actually moves: the calibration retry loop re-learns every
            // few hundred milliseconds and used to fill the log with identical lines (118KB of
            // noise for nine shots).
            if (Math.Abs(objPx - _lastLoggedObjPx) > 2000)
            {
                _lastLoggedObjPx = objPx;
                Log.WriteLine("[obj] object pixels = " + objPx + " (" +
                              (100.0 * objPx / Math.Max(1, bw * bh)).ToString("F1") + "%)");
            }
            ObjectFrac = (double)objPx / Math.Max(1, bw * bh);
        }

        int _lastLoggedObjPx = -1;

        static int Median(int[] hist, int n)
        {
            int half = n / 2, acc = 0;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= half) return i; }
            return 0;
        }

        public bool IsObject(int x, int y)
        {
            if (ObjectMask == null) return false;
            if (x < 0 || y < 0 || y >= ObjectMask.Length / Math.Max(1, _ow) || x >= _ow) return false;
            return ObjectMask[y * _ow + x];
        }

        // ---- grey blobs inside the field ------------------------------------
        public class Blob
        {
            public int X0, Y0, X1, Y1, N;
            public double Cx, Cy;
            public double GrayFrac;          // how much of the blob is neutral grey
            public int CX { get { return (X0 + X1) / 2; } }
            public int CY { get { return (Y0 + Y1) / 2; } }
            // The hook is a compact grey claw. The cable is also grey but is a long thin strip, and
            // the tiny fragments of the miner's cranking arm are small - both must be rejected or
            // the tracker locks onto a stationary cable and reports a frozen angle.
            public bool HookLike
            {
                get
                {
                    if (N < 110 || GrayFrac < 0.30) return false;
                    int w = X1 - X0 + 1, h = Y1 - Y0 + 1;
                    int mn = Math.Min(w, h), mx = Math.Max(w, h);
                    if (mn < 10) return false;          // a few px wide -> cable
                    if (mx > mn * 3) return false;      // very elongated -> cable
                    return true;
                }
            }
        }

        public List<Blob> GrayBlobs(Frame f, int minPx) { return GrayBlobs(f, minPx, Field.Top); }

        public List<Blob> GrayBlobs(Frame f, int minPx, int yFrom)
        {
            int W = f.W, H = f.H;
            bool[] m = new bool[W * H];
            for (int y = Math.Max(0, yFrom); y <= Field.Bottom && y < H; y++)
                for (int x = Field.Left; x <= Field.Right && x < W; x++)
                    if (Cls.Gray(f.P[y * W + x])) m[y * W + x] = true;

            bool[] seen = new bool[W * H];
            List<Blob> res = new List<Blob>();
            int[] stack = new int[W * H];
            for (int i = 0; i < W * H; i++)
            {
                if (!m[i] || seen[i]) continue;
                int sp = 0; stack[sp++] = i; seen[i] = true;
                Blob b = new Blob();
                b.X0 = W; b.Y0 = H; b.X1 = -1; b.Y1 = -1;
                long sx = 0, sy = 0;
                while (sp > 0)
                {
                    int p = stack[--sp];
                    int x = p % W, y = p / W;
                    b.N++;
                    if (x < b.X0) b.X0 = x; if (x > b.X1) b.X1 = x;
                    if (y < b.Y0) b.Y0 = y; if (y > b.Y1) b.Y1 = y;
                    sx += x; sy += y;
                    if (x > 0 && m[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack[sp++] = p - 1; }
                    if (x < W - 1 && m[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack[sp++] = p + 1; }
                    if (y > 0 && m[p - W] && !seen[p - W]) { seen[p - W] = true; stack[sp++] = p - W; }
                    if (y < H - 1 && m[p + W] && !seen[p + W]) { seen[p + W] = true; stack[sp++] = p + W; }
                }
                if (b.N >= minPx) { b.Cx = (double)sx / b.N; b.Cy = (double)sy / b.N; res.Add(b); }
            }
            res.Sort(delegate (Blob a, Blob b) { return b.N.CompareTo(a.N); });
            return res;
        }

        // Small connected components of "pixels that changed since the previous frame".
        // The hook is the only small, fast-moving object near the top of the field.
        public List<Blob> MovingBlobs(Frame cur, Frame prev, int minPx, int maxDim)
        {
            int W = cur.W, H = cur.H;
            bool[] m = new bool[W * H];
            // The resting hook only ever swings in a shallow band just under the miner, so keep
            // the search tight. This also excludes the HUD (timer / money text) and deep-field motion.
            int y0 = Math.Max(0, Field.Top - 150);
            int y1 = Math.Min(H - 1, Field.Top + 170);
            int centre = (Field.Left + Field.Right) / 2;
            int x0 = Math.Max(0, Math.Max(Field.Left, centre - 380));
            int x1 = Math.Min(W - 1, Math.Min(Field.Right, centre + 380));
            for (int y = y0; y <= y1; y++)
            {
                int off = y * W;
                for (int x = x0; x <= x1; x++)
                {
                    int p = off + x;
                    int a = cur.P[p], b = prev.P[p];
                    if (a == b) continue;
                    int d = Math.Abs(Cls.R(a) - Cls.R(b)) + Math.Abs(Cls.G(a) - Cls.G(b)) + Math.Abs(Cls.B(a) - Cls.B(b));
                    if (d > 45) m[p] = true;
                }
            }
            bool[] seen = new bool[W * H];
            List<Blob> res = new List<Blob>();
            int[] stack = new int[W * H];
            for (int i = 0; i < W * H; i++)
            {
                if (!m[i] || seen[i]) continue;
                int sp = 0; stack[sp++] = i; seen[i] = true;
                Blob b = new Blob();
                b.X0 = W; b.Y0 = H; b.X1 = -1; b.Y1 = -1;
                long sx = 0, sy = 0;
                while (sp > 0)
                {
                    int p = stack[--sp];
                    int x = p % W, y = p / W;
                    b.N++;
                    if (x < b.X0) b.X0 = x; if (x > b.X1) b.X1 = x;
                    if (y < b.Y0) b.Y0 = y; if (y > b.Y1) b.Y1 = y;
                    sx += x; sy += y;
                    if (x > 0 && m[p - 1] && !seen[p - 1]) { seen[p - 1] = true; stack[sp++] = p - 1; }
                    if (x < W - 1 && m[p + 1] && !seen[p + 1]) { seen[p + 1] = true; stack[sp++] = p + 1; }
                    if (y > 0 && m[p - W] && !seen[p - W]) { seen[p - W] = true; stack[sp++] = p - W; }
                    if (y < H - 1 && m[p + W] && !seen[p + W]) { seen[p + W] = true; stack[sp++] = p + W; }
                }
                int bw = b.X1 - b.X0 + 1, bh = b.Y1 - b.Y0 + 1;
                if (b.N >= minPx && bw <= maxDim && bh <= maxDim)
                {
                    b.Cx = (double)sx / b.N; b.Cy = (double)sy / b.N;
                    int grey = 0, tot = 0;
                    for (int y = b.Y0; y <= b.Y1; y++)
                        for (int x = b.X0; x <= b.X1; x++)
                        {
                            tot++;
                            if (Cls.Gray(cur.P[y * W + x])) grey++;
                        }
                    b.GrayFrac = tot > 0 ? (double)grey / tot : 0;
                    res.Add(b);
                }
            }
            return res;
        }

        public string CalibMessage = "";

        // A real level shows a wide dirt field. Menus, the game-over dialog and the high-score
        // name-entry screen do not, so we can tell the player "you are not in a level" instead of
        // silently collecting nothing for six seconds.
        bool FieldLooksLikeALevel(Frame f)
        {
            if (!FieldGeometrySane(f)) return false;
            // A mining level is mostly empty dirt. Measured across the session logs: accepted level
            // frames sit at 4.4-13.5%, while rejected menu/shop frames sit at 15.4-28.2%. The old
            // 0.12 threshold fell INSIDE the level range - session17 shows a 13.5% frame refused and
            // then the in-level pivot (960,118) seeded one cycle later at 6.7%, so the level's own
            // object fraction oscillates across the line within seconds. 0.145 sits in the real gap
            // between the two populations instead of cutting through one of them.
            if (ObjectFrac > 0.145) return false;
            return true;
        }

        /// <summary>The geometry half of the level test, with no dependence on how many objects
        /// the frame happens to contain. Used to decide whether Field can be trusted as the source
        /// of the pivot.</summary>
        bool FieldGeometrySane(Frame f)
        {
            // A level is letterboxed by black bars down the sides; menus and dialogs are not.
            bool letterbox = Field.Left > 4 || Field.Right < f.W - 5;
            return letterbox && Field.Width > 0.55 * f.W && Field.Height > 0.5 * f.H;
        }

        /// <summary>Whether the CURRENT Field (whatever frame produced it) passes the geometry test.
        /// Used by the run loop after a failed calibration, where no frame handle is available.</summary>
        public bool FieldGeometrySaneNow()
        {
            if (Cur == null) return false;
            return FieldGeometrySane(Cur);
        }

        // ---- calibration: watch the hook swing, fit the arc -------------------
        public bool Calibrate(ICapture cap, int seconds, int radiusHint)
        {
            Field = Rectangle.Empty;                 // re-detect: the window/level may have changed
            CalSamples.Clear();

            // Bail out immediately (rather than burning six seconds) when no level is on screen.
            Frame probe = null;
            for (int i = 0; i < 5 && probe == null; i++) { probe = cap.Grab(); if (probe == null) Thread.Sleep(60); }
            if (probe == null) { CalibMessage = "抓不到游戏画面"; return false; }
            Field = DetectField(probe);
            LearnObjects(probe);
            if (!FieldLooksLikeALevel(probe))
            {
                CalibMessage = "当前不在挖掘关卡里（菜单/商店/结算/输入名字界面），点“下一关”进关卡后按 F8";
                Log.WriteLine("[cal] not in a level: field=" + Field + " client=" + probe.W + "x" + probe.H +
                              " objects=" + (ObjectFrac * 100).ToString("F1") + "%");
                SaveCalibDump(probe, null);
                return false;
            }

            // The hook search box is built around PivotX/PivotY, which are still 0 on a first run.
            // Without this the box lands at the far-left edge of the screen and calibration can
            // never collect a single sample - a deadlock the saved ini used to mask.
            ArtPivot();
            Angle = Math.PI / 2; HaveAngle = false; HookR = BaseR;
            Log.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[cal] seed pivot=({0:F0},{1:F0}) r={2:F0}", PivotX, PivotY, BaseR));
            Log.WriteLine("[cal] watching for hook motion ... (start a level if you have not)");
            List<double> xs = new List<double>(), ys = new List<double>();
            Frame prev = null;
            DateTime t0 = DateTime.Now;
            int frames = 0;
            int changedFrames = 0;

            while ((DateTime.Now - t0).TotalSeconds < seconds)
            {
                Frame f = cap.Grab();
                if (f == null) { Thread.Sleep(40); continue; }
                frames++;
                if (Field.Width == 0) Field = DetectField(f);
                if (prev != null)
                {
                    if (!FrameChanged(f, prev, 11, 40))
                    {
                        prev = f;
                        Thread.Sleep(30);
                        continue;                       // identical frame: game is paused
                    }
                    changedFrames++;
                    Cur = f;
                    double chx, chy; int chn;
                    if (HookPixels(f, prev, HookBox(), out chx, out chy, out chn))
                    {
                        xs.Add(chx); ys.Add(chy);
                        CalSamples.Add(new double[] { chx, chy });
                        if (xs.Count >= 22) break;    // 22 samples is plenty at ~0.3px spread
                    }
                    // No signal at all after two seconds means this is not a good moment; give up
                    // now and retry cheaply instead of grinding out the whole window.
                    else if (xs.Count < 3 && (DateTime.Now - t0).TotalSeconds > 2.0)
                    {
                        Log.WriteLine("[cal] no hook signal after 2s - retrying");
                        break;
                    }
                }
                prev = f;
                Thread.Sleep(10);
            }
            Log.WriteLine("[cal] frames=" + frames + " changed=" + changedFrames + " hook samples=" + xs.Count);

            if (xs.Count < 12)
            {
                if (changedFrames < 3)
                {
                    CalibMessage = "游戏画面完全静止（游戏没在前台）→ 用鼠标点一下游戏画面，再按 F8";
                    Log.WriteLine("[cal] FAILED: no frame changed at all - the game is paused/unfocused.");
                }
                else
                {
                    CalibMessage = "在关卡里但没看到钩子摆动，按 F8 重试（或点“录制诊断 8 秒”发我 diag）";
                    Log.WriteLine("[cal] FAILED: frames changed but no hook-like mover found.");
                }
                SaveCalibDump(prev, null);
                return false;
            }

            // A stationary target (e.g. the cable) fits a circle perfectly and produces a
            // beautiful but meaningless pivot, so require the samples to actually sweep an arc.
            double mnx = double.MaxValue, mxx = double.MinValue, mny = double.MaxValue, mxy = double.MinValue;
            for (int i = 0; i < xs.Count; i++)
            {
                if (xs[i] < mnx) mnx = xs[i]; if (xs[i] > mxx) mxx = xs[i];
                if (ys[i] < mny) mny = ys[i]; if (ys[i] > mxy) mxy = ys[i];
            }
            double sweep = Math.Max(mxx - mnx, mxy - mny);
            Log.WriteLine(string.Format(CultureInfo.InvariantCulture, "[cal] sample sweep = {0:F1}px", sweep));
            if (sweep < 18)
            {
                CalibMessage = "抓到的目标没有在摆动（可能认成了缆绳）→ 等钩子正常摆动时按 F8";
                Log.WriteLine("[cal] FAILED: samples are stationary - not a swinging hook.");
                SaveCalibDump(prev, null);
                return false;
            }

            double cx, cy, r, err;
            bool fitted = FitPivot(xs, ys, out cx, out cy, out r, out err);
            double fitX = cx, fitY = cy, fitR = r;

            // Safety net. The pivot position is now a MEASURED constant - the rope was traced in two
            // separate frames and it hangs from (field centre, Field.Top - 0.049 * Field.Height).
            //
            // These bounds are deliberately far tighter than "the fit looks plausible". On a shallow
            // swing arc the circle centre and the radius are nearly COLLINEAR parameters: a fit that
            // moves the centre 20px up and the radius from 52 to 63 predicts an arc that is almost
            // indistinguishable from the measured one. On the real 22-sample set the residual spread
            // around (955,98) r=63 was 1.97px and around (957,118) r=52 was 1.71px - a 0.26px
            // difference is being asked to choose between two hypotheses 20px apart, and it cannot.
            // A previous window of +-25px let exactly such a fit through: the live log read
            //   [cal] fit pivot=(955,98) r=63 spread=0.3 ok=True
            //   [cal] art pivot=(957,118) r=52   -> using FIT
            // and every subsequent tracked angle carried a 5-11 degree bias through the mid-swing
            // (a hook at (987,152) reads 30.65deg around the fit pivot and 41.42deg around the
            // measured one). The fit may REFINE the measured pivot, never relocate it.
            // The pivot POSITION is never taken from the fit. Not even when the fit lands 5px away:
            // close to the pivot 5px of offset is worth 6 degrees of angle (a hook at (943,150)
            // reads -29.6deg around (964,113) but -35.7deg around the measured (966,118)), and the
            // fit has already produced 55px, 20px and 5px errors on three different sessions.
            // Refining the arc RADIUS is useful; deciding where the centre is, is not.
            ArtPivot();
            double artR = BaseR;
            // 5%, not 20%. The centroid floor is 0.80*BaseR, so an inflated BaseR lifts the floor
            // ABOVE the real resting radius and the tracker goes blind. Measured breaking point:
            // at +20% (fitR 52.2) the floor is 41.8 while the measured resting minimum is 38.1,
            // i.e. anything from fitR >= 47.6 breaks it - and real logs contain fits at r=47/51/52/53.
            // At +-5% BaseR stays in 41.3..45.7, so 0.80*BaseR is 33.0..36.6, always below the
            // measured resting band of 38.1..50.9. The fit only refines the radius; it must not move it far.
            bool useFitR = fitted && Math.Abs(fitR - artR) <= 0.05 * artR;
            if (useFitR) BaseR = fitR;
            PivotSource = useFitR ? "ART+FITr" : "ART";     // the successful path must name its source too
            bool useFit = false;                 // kept for the log line below
            Log.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[cal] fit  pivot=({0:F0},{1:F0}) r={2:F0} spread={3:F1} ok={4}", fitX, fitY, fitR, err, fitted));
            Log.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[cal] art  pivot=({0:F0},{1:F0}) r={2:F0}   -> using {3}  src={4}",
                PivotX, PivotY, artR, useFitR ? "ART+FITr" : "ART", PivotSource));
            if (useFit) { PivotX = fitX; PivotY = fitY; BaseR = fitR; }
            SaveCalibDump(prev, null);
            RestR = BaseR;
            HookDeployed = false; LostFrames = 0; HaveAngle = false; NoHookFrames = 0;
            JumpRejects = 0;
            Angle = 0; Omega = 0; MinAngle = -1.6; MaxAngle = 1.6;
            LastAccept = DateTime.Now;

            Log.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[cal] using pivot=({0:F1},{1:F1}) radius={2:F1} spread={3:F2}px", PivotX, PivotY, BaseR, err));

            // learn the static rocks: grey blobs far from the swing arc
            Cur = cap.Grab();
            if (Cur != null)
            {
                List<Blob> blobs = GrayBlobs(Cur, 500);
                StaticRocks.Clear();
                foreach (Blob b in blobs)
                {
                    double d = Math.Sqrt((b.Cx - PivotX) * (b.Cx - PivotX) + (b.Cy - PivotY) * (b.Cy - PivotY));
                    if (d > BaseR * 2.2)
                        StaticRocks.Add(new Rectangle(b.X0 - 6, b.Y0 - 6, b.X1 - b.X0 + 12, b.Y1 - b.Y0 + 12));
                }
                Log.WriteLine("[cal] static grey blobs excluded from hook search: " + StaticRocks.Count);
            }
            C.PivotX = (int)PivotX; C.PivotY = (int)PivotY; C.Radius = (int)BaseR;
            C.FieldL = Field.Left; C.FieldT = Field.Top; C.FieldR = Field.Right; C.FieldB = Field.Bottom;
            C.GeometryTrusted = true;      // passed every sanity check -> reuse on later rounds
            C.ClientW = Cur != null ? Cur.W : 0;
            C.ClientH = Cur != null ? Cur.H : 0;
            return true;
        }

        // Kasa algebraic circle fit
        public static bool CircleFit(List<double> xs, List<double> ys, out double cx, out double cy, out double r)
        {
            cx = cy = r = 0;
            int n = xs.Count;
            if (n < 3) return false;
            double mx = 0, my = 0;
            for (int i = 0; i < n; i++) { mx += xs[i]; my += ys[i]; }
            mx /= n; my /= n;
            double suu = 0, svv = 0, suv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
            for (int i = 0; i < n; i++)
            {
                double u = xs[i] - mx, v = ys[i] - my;
                suu += u * u; svv += v * v; suv += u * v;
                suuu += u * u * u; svvv += v * v * v; suvv += u * v * v; svuu += v * u * u;
            }
            double det = suu * svv - suv * suv;
            if (Math.Abs(det) < 1e-9) return false;
            double c1 = 0.5 * (suuu + suvv), c2 = 0.5 * (svvv + svuu);
            double uc = (c1 * svv - c2 * suv) / det;
            double vc = (c2 * suu - c1 * suv) / det;
            cx = uc + mx; cy = vc + my;
            double s = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = xs[i] - cx, dy = ys[i] - cy;
                s += Math.Sqrt(dx * dx + dy * dy);
            }
            r = s / n;
            // sanity: a real swing arc is a decent circle, radius within the field
            return r > 20 && r < 1500;
        }

        // The hook is the only small object near the pivot that changes between frames.
        // Locate it by frame differencing, then take the centroid of the grey pixels inside
        // that bbox (the diff itself is only a crescent, its centroid would be biased).
        public bool HookCentroid(Frame f, Blob b, out double hx, out double hy)
        {
            int x0 = Math.Max(0, b.X0 - 9), x1 = Math.Min(f.W - 1, b.X1 + 9);
            int y0 = Math.Max(0, b.Y0 - 9), y1 = Math.Min(f.H - 1, b.Y1 + 9);
            double sx = 0, sy = 0; int n = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int c = f.P[y * f.W + x];
                    if (!Cls.Gray(c)) continue;
                    bool inRock = false;
                    foreach (Rectangle r0 in StaticRocks)
                        if (x >= r0.Left && x <= r0.Right && y >= r0.Top && y <= r0.Bottom) { inRock = true; break; }
                    if (inRock) continue;
                    sx += x; sy += y; n++;
                }
            if (n >= 10) { hx = sx / n; hy = sy / n; return true; }
            hx = (b.X0 + b.X1) / 2.0; hy = (b.Y0 + b.Y1) / 2.0;
            return false;
        }

        // Has the game rendered a new frame? The game runs at ~18fps while we sample at ~60fps,
        // so most captures are duplicates and must not be mistaken for "hook lost".
        public static bool FrameChanged(Frame a, Frame b, int step, int limit)
        {
            if (a == null || b == null || a.W != b.W) return true;
            int diff = 0;
            for (int i = 0; i < a.P.Length; i += step)
            {
                int p = a.P[i], q = b.P[i];
                if (p == q) continue;
                if (Math.Abs(Cls.R(p) - Cls.R(q)) + Math.Abs(Cls.G(p) - Cls.G(q)) + Math.Abs(Cls.B(p) - Cls.B(q)) > 40)
                    if (++diff > limit) return true;
            }
            return false;
        }

        /// <summary>DEPRECATED, no longer read. The continuity gate used to consult
        /// `LostFrames &lt; 3` for a three-strike grace period, but LostFrames is zeroed at the top of
        /// TrackHook on every successful HookPixels and every rejecting gate returns immediately, so
        /// it never accumulated and the clause was permanently true - a dead grace period. Replaced
        /// by JumpRejects, which is incremented only on a jump reject. Kept (and still written) only
        /// because removing it would touch seven call sites; it has no effect on behaviour.</summary>
        public int LostFrames;
        /// <summary>Pixels that passed the colour+motion tests this frame, BEFORE the geometric
        /// gates. Kept so the tracker can explain why it chose one blob over another - the user
        /// could see the hook at -66 while the tracker reported -37, and nothing in any log said
        /// what else was on offer.</summary>
        public List<int> CandidateMask = new List<int>();

        /// <summary>Summarise the candidate pixels as blobs with their radius and angle from the
        /// pivot, so the run loop can log what the tracker had to choose from.</summary>
        public string DescribeCandidates(int w)
        {
            if (CandidateMask.Count == 0) return "none";
            List<int> pts = new List<int>(CandidateMask);
            pts.Sort();
            List<string> parts = new List<string>();
            List<int> used = new List<int>();
            for (int i = 0; i < pts.Count && parts.Count < 4; i++)
            {
                if (used.Contains(pts[i])) continue;
                int seed = pts[i];
                int sx0 = seed % w, sy0 = seed / w;
                double cx = 0, cy = 0; int cnt = 0;
                // Bounded flood over the sorted candidate set: a pixel joins the blob if it is
                // within 3px of one already in it. Cheap and enough to separate the hook from the
                // winch, which are tens of pixels apart.
                List<int> blob = new List<int>();
                blob.Add(seed); used.Add(seed);
                for (int k = 0; k < blob.Count && blob.Count < 4000; k++)
                {
                    int q = blob[k];
                    int qx = q % w, qy = q / w;
                    foreach (int r in pts)
                    {
                        if (used.Contains(r)) continue;
                        int rx = r % w, ry = r / w;
                        if (Math.Abs(rx - qx) <= 3 && Math.Abs(ry - qy) <= 3) { blob.Add(r); used.Add(r); }
                    }
                }
                for (int k = 0; k < blob.Count; k++) { cx += blob[k] % w; cy += blob[k] / w; cnt++; }
                if (cnt == 0) continue;
                cx /= cnt; cy /= cnt;
                double dx = cx - PivotX, dy = cy - PivotY;
                parts.Add(string.Format(CultureInfo.InvariantCulture,
                    "n={0} c=({1:F0},{2:F0}) ang={3:F1} r={4:F0}",
                    cnt, cx, cy, Math.Atan2(dx, dy) * 180 / Math.PI, Math.Sqrt(dx * dx + dy * dy)));
            }
            return string.Join(" | ", parts.ToArray());
        }
        /// <summary>Why the last TrackHook call gave up; empty on success. Logged (rate limited) by
        /// the run loop, because previously NO log field recorded a tracking failure at all - the
        /// only trace was a status string, so "the hook is invisible" was undiagnosable.</summary>
        public string Reject = "";
        /// <summary>One game frame of swing, published by the run loop for the continuity gate.</summary>
        public double GameStep = 0.14;
        public DateTime LastAccept = DateTime.Now;

        public Blob FindHookBlob(Frame f)
        {
            List<Blob> mv = MovingBlobs(f, Prev, 40, 190);
            Blob pick = null;
            double bd = double.MaxValue;
            if (HaveAngle)
            {
                double ex = PivotX + Math.Sin(Angle) * HookR;
                double ey = PivotY + Math.Cos(Angle) * HookR;
                foreach (Blob b in mv)
                {
                    if (!b.HookLike) continue;
                    double d = (b.Cx - ex) * (b.Cx - ex) + (b.Cy - ey) * (b.Cy - ey);
                    if (d < bd) { bd = d; pick = b; }
                }
                if (pick != null && bd > 160 * 160) pick = null;
            }
            else
            {
                double centre = (Field.Left + Field.Right) / 2.0;
                foreach (Blob b in mv)
                {
                    if (!b.HookLike) continue;
                    double d = Math.Abs(b.Cx - centre) + Math.Abs(b.Cy - (Field.Top - 40)) * 0.6;
                    if (d < bd) { bd = d; pick = b; }
                }
            }
            return pick;
        }

        // Bounded, outlier-tolerant pivot fit.
        //
        // An unconstrained algebraic fit happily returns radius 200+ for a 50px swing arc, and a
        // far-away centre makes every radius nearly equal (a degenerate "flat arc" minimum), so we
        // (a) confine the pivot to the winch area at the top-centre, (b) require a plausible rope
        // length, and (c) score with a trimmed spread so stray samples cannot drag the result.
        public bool FitPivot(List<double> xs, List<double> ys, out double bx, out double by, out double br, out double berr)
        {
            double centre = (Field.Left + Field.Right) / 2.0;
            double rMin = 14, rMax = Math.Max(70.0, Field.Height * 0.22);
            bx = by = br = 0; berr = double.MaxValue;
            if (xs.Count < 10) return false;

            double[] d = new double[xs.Count];
            int lo = xs.Count / 5, hi = xs.Count - xs.Count / 5;
            if (hi - lo < 6) return false;

            for (double px = centre - 95; px <= centre + 95; px += 3)
                for (double py = Field.Top - 175; py <= Field.Top + 15; py += 3)
                {
                    for (int i = 0; i < xs.Count; i++)
                    {
                        double dx = xs[i] - px, dy = ys[i] - py;
                        d[i] = Math.Sqrt(dx * dx + dy * dy);
                    }
                    Array.Sort(d);
                    double mean = 0;
                    for (int i = lo; i < hi; i++) mean += d[i];
                    mean /= (hi - lo);
                    if (mean < rMin || mean > rMax) continue;
                    double v = 0;
                    for (int i = lo; i < hi; i++) { double t = d[i] - mean; v += t * t; }
                    v = Math.Sqrt(v / (hi - lo));
                    if (v < berr) { berr = v; bx = px; by = py; br = mean; }
                }
            return berr < 8.0;
        }

        /// <summary>Where the current pivot came from: ART or ART+FITr (measured on a sane level
        /// field), CLIENT (fallback from the client size), PREV (kept from an earlier run), NONE
        /// (fallback also failed). Initialised to "?" meaning "nothing has run yet". Logged on every
        /// calibration attempt - a wrong pivot is this tool's most damaging failure (31px was worth
        /// 23 degrees of angle) and it was previously untraceable.</summary>
        public string PivotSource = "?";

        // Fallback pivot straight from the game's fixed art: the winch sits at the horizontal
        // centre of the field, a little above the dirt line, on a short cable.
        /// <summary>Derive a usable Field from the client size alone, for when no level frame has
        /// ever been seen. Without this the pivot stays 0, HookBox() lands at x=-150 (entirely off
        /// screen), HookPixels finds n=0 forever and the tracker is blind for the whole session.
        /// Takes the frame EXPLICITLY: the first version relied on Cur, which is still null at this
        /// point in the loop, so it returned immediately while the caller printed a success message
        /// - a log that claims work it did not do is worse than no log at all. Returns whether it
        /// actually produced a geometry.</summary>
        public bool ArtPivotFromClient(Frame f)
        {
            if (f == null || f.W < 64 || f.H < 64) return false;
            int top = (int)(f.H * 0.16);
            Field = new Rectangle(0, top, f.W, f.H - top);
            ArtPivot();
            return PivotX > 0 && BaseR > 0;
        }

        /// <summary>Client width in pixels, published by the run loop every frame. The winch sits at
        /// the horizontal CENTRE OF THE CLIENT, which is the anchor the measurements agree on:
        /// (966,118) at a 1936 client, 965 at 1930. DetectField's field edges are ~9px off, so
        /// deriving the pivot from (Field.Left+Field.Right)/2 gave 957 and moved the aim 7-11 degrees
        /// at close radii - a reviewer caught it live: with piv=957 only 5 of 101 resting frames had a
        /// radius inside the 45..55 invariant band, against 76 of 101 for the client centre.</summary>
        public int ClientW;

        public void ArtPivot()
        {
            // Measured off a real frame: the rope hangs from (966, 118) while Field.Top is 159 and
            // Field.Height 837, i.e. the pivot sits only ~4.9% of the field height above the dirt
            // line. The old 10.5% put it 47px too high, which skews every angle - and a wrong pivot
            // is exactly what makes a fired hook land well off the target. The value agrees with the
            // successful calibrations, which fitted y=113..119.
            // Anchor X to the CLIENT CENTRE, not to the detected field's edges. DetectField's edges
            // are ~9px off on a level frame, so (Field.Left+Field.Right)/2 gave 957 where the truth is
            // 965 - and a reviewer measured the consequence live: with piv=957 only 5 of 101 resting
            // frames had a radius inside the 45..55 invariant band, against 76 of 101 for the client
            // centre. 8px is 7-11 degrees at close radii. Fall back to the field centre only before
            // any frame has been seen.
            PivotX = ClientW > 0 ? ClientW / 2.0 : (Field.Left + Field.Right) / 2.0;
            PivotY = Field.Top - Field.Height * 0.049;
            // 0.052, not 0.062. The formula is an ESTIMATE of the resting radius, and it was 20% high:
            // measured resting centroid radius is 42-45px on an 836px field = 0.050-0.054, while
            // 0.062 gives 51.8px. That error propagated into every arc-band test, so a 0.80*BaseR
            // floor landed ON the real resting distribution (41.4 vs a 42.4 median) and the deploy
            // latch tripped late. See FIXES.md and the independent audit.
            BaseR = Field.Height * 0.052;
        }

        // ---- rope direction ---------------------------------------------------
        // The cable is the one long, thin, continuous dark line from the pivot to the hook. Blob
        // tracking kept latching onto the miner (his hair, beard and winch are grey and he is
        // always cranking), whereas the rope points straight at the answer. The rope is dark, so
        // it is already part of the outline mask - we just look for the angle with the longest
        // unbroken run of outline pixels.
        public bool FindRope(out double angle, out double len)
        {
            angle = 0; len = 0;
            if (OutlineMask == null || Cur == null || Field.Width <= 8) return false;
            int W = _ow, H = OutlineMask.Length / Math.Max(1, W);
            int x0 = Field.Left + 6, x1 = Field.Right - 6, yTop = Field.Top + 18, yBot = Field.Bottom;
            double bestScore = -1e18, bestA = 0, bestLen = 0;

            for (int half = -170; half <= 170; half++)
            {
                double a = half * 0.5 * Math.PI / 180.0;      // -85..+85 deg in 0.5 deg steps
                double sn = Math.Sin(a), cs = Math.Cos(a);
                double px = -cs, py = sn;                     // perpendicular unit vector
                int hits = 0, lastHit = 0, gap = 0, maxGap = 0;
                for (int r = 16; r < 1500; r += 2)
                {
                    int bx = (int)(PivotX + sn * r), by = (int)(PivotY + cs * r);
                    if (bx < x0 || bx > x1 || by > yBot || by < yTop) break;
                    bool hit = false;
                    for (int o = -2; o <= 2; o += 2)          // tolerate the rope being 1-2px off
                    {
                        int x = (int)(bx + px * o), y = (int)(by + py * o);
                        if (x < 0 || y < 0 || x >= W || y >= H) continue;
                        if (OutlineMask[y * W + x]) { hit = true; break; }
                    }
                    if (hit) { hits++; lastHit = r; gap = 0; }
                    else { gap++; if (gap > maxGap) maxGap = gap; }
                }
                if (lastHit < 22) continue;
                double samples = (lastHit - 16) / 2.0;
                double density = hits / Math.Max(1.0, samples);
                double score = lastHit * density - maxGap * 9.0;
                if (score > bestScore) { bestScore = score; bestA = a; bestLen = lastHit; }
            }
            if (bestScore <= 0) return false;
            angle = bestA; len = bestLen;
            return true;
        }

        // Centroid of pixels that BOTH changed since the previous frame AND are grey now.
        //
        // Taking grey pixels over a bounding box failed because the box is full of static grey
        // furniture (the miner's hair and beard, the winch, and the tan wooden platform he stands
        // on). Intersecting the two conditions removes all of it: the platform, beard and winch do
        // not change, the cranking arm is brown, and the winch drum is too dark to count as grey.
        public bool HookPixels(Frame cur, Frame prev, Rectangle box,
                               out double hx, out double hy, out int count)
        {
            double sx = 0, sy = 0; int n = 0;
            List<int> acc = new List<int>();
            LastCandidates.Clear();
            CandidateMask.Clear();
            int x0 = Math.Max(0, box.Left), x1 = Math.Min(cur.W - 1, box.Right);
            int y0 = Math.Max(0, box.Top), y1 = Math.Min(cur.H - 1, box.Bottom);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int p = y * cur.W + x;
                    int a = cur.P[p], b = prev.P[p];
                    int d = Math.Abs(Cls.R(a) - Cls.R(b)) + Math.Abs(Cls.G(a) - Cls.G(b)) + Math.Abs(Cls.B(a) - Cls.B(b));
                    if (d <= 45) continue;
                    if (!Cls.Gray(a)) continue;
                    // Everything below passed the COLOUR and MOTION tests. Record it before the
                    // geometry gates so the candidate dump can show what the gates then removed.
                    CandidateMask.Add(p);
                    // The hook only ever hangs BELOW the winch, inside the swing arc. The search box
                    // reaches 70px above the predicted hook, which put the miner's grey hair and
                    // beard and the winch drum inside it - grey, always moving, and right at the
                    // pivot. They dragged the centroid above the pivot and produced tracked angles
                    // like -141deg and -167deg, which is why the hook either fired at nothing or
                    // never fired at all. Anything at or above the pivot is not the hook.
                    double cdx = x - PivotX, cdy = y - PivotY;
                    if (cdy < 8) continue;
                    if (Math.Abs(Math.Atan2(cdx, cdy)) > 1.45) continue;
                    // The hook rides a fixed arc around the pivot, so its distance is known. A blob
                    // right next to the pivot is the winch or the miner, not the hook - and because
                    // a point that close sweeps a huge angle for a couple of pixels of movement, it
                    // poisons the angle completely.
                    //
                    // ONLY while the hook is at rest: once it has been fired it travels hundreds of
                    // pixels out along the rope, far beyond this arc, and applying the arc test then
                    // made the tracker blind for the whole shot - which is why the flight
                    // measurement stopped appearing in the log altogether. Use the calibrated BaseR,
                    // not the adaptive RestR, so the arc cannot drift.
                    if (BaseR > 6)
                    {
                        double dist = Math.Sqrt(cdx * cdx + cdy * cdy);
                        // The LOWER bound applies always: at rest the hook sits on the arc, and
                        // once fired it is further out still, so nothing genuinely near the pivot is
                        // ever the hook. Applying it unconditionally matters because HookDeployed is
                        // only updated at the end of this routine, so a frame that transiently
                        // believed "deployed" used to skip the whole test and admit a blob 26px out
                        // - which then fired two shots wildly off target.
                        if (dist < BaseR * 0.80) continue;
                        // REVERTED to 1.60. Widening it to 2.40 was a HYPOTHESIS - that rope pixels
                        // drag the centroid outward at wide angles and the ceiling was clipping the
                        // swing - and the user disproved it on the next run: the tracked angle was
                        // still -37 while the hook was visibly at -66. That is not a clipping
                        // artefact, it is the tracker holding a different object, so the ceiling was
                        // never the cause and the change is not kept.
                        if (!HookDeployed && dist > BaseR * 1.60) continue;
                    }
                    bool inRock = false;
                    foreach (Rectangle r0 in StaticRocks)
                        if (x >= r0.Left && x <= r0.Right && y >= r0.Top && y <= r0.Bottom) { inRock = true; break; }
                    if (inRock) continue;
                    sx += x; sy += y; n++;
                    acc.Add(p);
                }
            // Take the LARGEST connected blob, not the mean of everything that passed. Summing all
            // accepted pixels let a 400px hook blob and a handful of stray pixels share one
            // centroid, which pulls the reported position between two objects - the user saw the
            // hook at -66 while the tracker reported -37. A live candidate dump shows the two
            // populations are cleanly separable by SIZE and not by radius:
            //     n>=200 : 46 blobs   (the hook; median 318px)
            //     n<30   : 20 blobs   (noise)
            // while the hook blob's centroid sits at r=32 and the noise at 26-28 - only 4px apart,
            // so the 0.80*BaseR radius gate could never separate them and in fact cut straight
            // through the hook blob, which is why the tracked radius read 39-45 instead of 32.
            count = n;
            if (acc.Count >= 12)
            {
                List<int> pts = new List<int>(acc);
                pts.Sort();
                List<int> best = null;
                List<int> seen = new List<int>();
                for (int i = 0; i < pts.Count; i++)
                {
                    if (seen.Contains(pts[i])) continue;
                    List<int> blob = new List<int>();
                    blob.Add(pts[i]); seen.Add(pts[i]);
                    for (int k = 0; k < blob.Count; k++)
                    {
                        int q = blob[k]; int qx = q % cur.W, qy = q / cur.W;
                        foreach (int r2 in pts)
                        {
                            if (seen.Contains(r2)) continue;
                            int rx = r2 % cur.W, ry = r2 / cur.W;
                            if (Math.Abs(rx - qx) <= 2 && Math.Abs(ry - qy) <= 2) { blob.Add(r2); seen.Add(r2); }
                        }
                    }
                    if (best == null || blob.Count > best.Count) best = blob;
                }
                if (best != null && best.Count >= 12)
                {
                    double bx = 0, by = 0;
                    for (int k = 0; k < best.Count; k++) { bx += best[k] % cur.W; by += best[k] / cur.W; }
                    count = best.Count;
                    hx = bx / best.Count; hy = by / best.Count;
                    return true;
                }
            }
            hx = (x0 + x1) / 2.0; hy = (y0 + y1) / 2.0;
            return false;
        }

        // Where the hook can possibly be: just under the winch, a short cable's length out.
        public Rectangle HookBox()
        {
            double ex = HaveAngle ? PivotX + Math.Sin(Angle) * HookR : PivotX;
            double ey = HaveAngle ? PivotY + Math.Cos(Angle) * HookR : Field.Top + 10;
            int cx = (int)ex, cy = (int)ey;
            return new Rectangle(cx - 150, cy - 70, 300, 140);
        }

    }
}
