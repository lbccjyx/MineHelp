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
        public bool TrackHook(Frame f)
        {
            if (Prev == null) return false;
            if (!FrameChanged(f, Prev, 11, 40)) return HaveAngle;   // duplicate frame, coast
            if (OutlineMask == null) return false;

            double hx, hy; int cnt;
            Reject = "";
            if (!HookPixels(f, Prev, HookBox(), out hx, out hy, out cnt))
            {
                Reject = string.Format(CultureInfo.InvariantCulture, "no pixels (n={0}) box={1}", cnt, HookBox());
                NoHookFrames++;
                if (NoHookFrames > 6) HookDeployed = true;
                if (NoHookFrames > 45) { HaveAngle = false; LostFrames = 45; }
                // RECOVERY. Nothing used to pull the tracker out of this state, so a single bad
                // stretch could blind it permanently: HookBox() is centred on the last predicted
                // position, and with HaveAngle cleared it kept staring at the same stale spot while
                // the real hook swung somewhere else. A live log ran 246 consecutive misses over
                // 68 SECONDS with dep=1 haveAngle=0, and in that state nothing fires and the aim
                // line flickers (drawn whenever a single frame happens to succeed). After ~3s of
                // blindness, re-seed the search box at the resting position so the next frame has a
                // chance of finding the hook again.
                if (NoHookFrames > 60)
                {
                    double rr = RestR > 6 ? RestR : (BaseR > 6 ? BaseR : 45);
                    Angle = 0; HookR = rr;
                    HookX = PivotX; HookY = PivotY + rr;
                    // HaveAngle deliberately left FALSE: claiming an angle for a synthetic
                    // position lets the nearLine + settledOnIt path fire on a spot the tracker
                    // never observed. Leaving it false makes the next accepted frame establish a
                    // real angle before anything can fire.
                    HookDeployed = false;
                    NoHookFrames = 0;
                    Reject = "blind for 3s - re-seeded the search box at rest";
                }
                return HaveAngle;
            }
            NoHookFrames = 0;
            LostFrames = 0;

            double dx = hx - PivotX, dy = hy - PivotY;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double ang = Math.Atan2(dx, dy);

            // The CENTROID must also sit on the arc - gating the pixels is not enough. An annulus
            // is NOT convex, so the mean of pixels that are each individually on the arc can land
            // back inside the hole: two accepted pixels on opposite sides of the pivot at radius 37
            // average to the pivot itself. A live log had 110 of 678 resting frames with the centroid
            // below the pixel floor (minimum 26.25px), and a point that close to the pivot sweeps tens
            // of degrees for a few pixels of jitter.
            //
            // The factor is 0.65, NOT 0.80, and there is no upper bound:
            //  * An independent audit measured the real resting radius at 42-45px. With BaseR now
            //    corrected to 0.052*Field.Height (~43px) a 0.80 floor would still clip the low tail
            //    (and with an adopted fitR near +20% it rejected essentially every resting frame,
            //    which bricks the tracker for the whole session). 0.65 clears the 26px junk while
            //    leaving the real resting band untouched.
            //  * The upper bound was provably DEAD CODE: HookPixels already rejects pixels beyond
            //    1.60*BaseR while not deployed, and a disk is convex, so the centroid can never
            //    exceed it. Dead guards only give false confidence.
            if (BaseR > 6 && len < BaseR * 0.65)
            {
                Reject = string.Format(CultureInfo.InvariantCulture, "centroid r={0:F1} < {1:F1} (0.65*BaseR)", len, BaseR * 0.65);
                LostFrames++; return false;
            }

            // Physical sanity gate. The hook hangs BELOW the winch and swings within roughly +-85
            // degrees of straight down, so |ang| can never approach 90 degrees. A tracked angle
            // past that is not the hook at all - it is the miner, the winch drum or a stray rope
            // pixel - and believing it is catastrophic: the log caught -141 and -167 degree samples
            // which fired the hook 75 and 102 degrees away from the aim. Reject, do not clamp, so
            // the bad frame cannot corrupt Omega or the crossing test either.
            if (Math.Abs(ang) > 1.50)
            {
                Reject = string.Format(CultureInfo.InvariantCulture, "angle {0:F1}deg out of range", ang * 180 / Math.PI);
                LostFrames++;
                return false;
            }

            // Continuity gate - REVERTED to the flat 0.38 rad, because the audit proved no magnitude
            // threshold can work here. Real per-frame jumps reach 31-54 degrees (dropped frames) while
            // the noise jump that motivated tightening was 18.75 degrees: the two ranges OVERLAP, so
            // any bound either lets the noise through or rejects real motion. The tightened version
            // also had a dead grace period - LostFrames is zeroed on every successful HookPixels, so
            // `LostFrames < 3` was always true and every over-limit jump became a hard reject, which
            // made 15-40% of logged crossings unreachable (and blocked the very jumps[] samples that
            // would have raised its own threshold).
            //
            // The fake crossing is instead prevented at its real source - a frozen tracker - by
            // clearing the crossing baseline when the angle stops moving (see RunLoop).
            if (HaveAngle)
            {
                double jump = ang - Angle;
                while (jump > Math.PI) jump -= 2 * Math.PI;
                while (jump < -Math.PI) jump += 2 * Math.PI;
                if (Math.Abs(jump) > 0.38 && LostFrames < 3)
                {
                    Reject = string.Format(CultureInfo.InvariantCulture, "jump {0:F1}deg", jump * 180 / Math.PI);
                    LostFrames++;
                    return false;
                }
            }

            // Same idea for the radius: at rest the hook sits just under the pivot, and while
            // deployed it is far out. A tiny radius means we latched onto something at the winch.
            if (RestR > 0 && len < RestR * 0.35)
            {
                LostFrames++;
                return false;
            }

            DateTime now = DateTime.Now;
            double dt = Math.Max(0.004, (now - LastAccept).TotalSeconds);
            LastAccept = now;
            LastDt = dt;

            if (HaveAngle)
            {
                double d = ang - Angle;
                while (d > Math.PI) d -= 2 * Math.PI;
                while (d < -Math.PI) d += 2 * Math.PI;
                double inst = d / dt;
                if (Math.Abs(inst) < 12) Omega = Omega * 0.7 + inst * 0.3;
            }
            Angle = ang;
            HaveAngle = true;
            if (ang < MinAngle) MinAngle = ang;
            if (ang > MaxAngle) MaxAngle = ang;

            HookR = len;
            HookX = hx; HookY = hy;
            // "Deployed" has to LATCH. While the hook is out on its rope the tracker loses the real
            // hook and starts following the moving rope near the pivot, which reads as a resting
            // radius - and the countdown would come back mid-shot. So once the hook is out it stays
            // out until it has been seen back at the resting radius for several frames in a row.
            // RestR is the radius of the arc the hook rides. It must NOT be learned from the
            // tracked length: when the tracker locked onto the winch instead of the hook that
            // length got shorter, which made RestR shorter, which made the radius sanity check
            // looser, which kept the tracker locked on the winch. A self-reinforcing trap. Keep the
            // calibrated value and only nudge it, clamped, so it cannot run away.
            if (!HookDeployed && RestR > 0 && BaseR > 0)
            {
                double blended = RestR * 0.98 + len * 0.02;
                RestR = Math.Max(BaseR * 0.75, Math.Min(BaseR * 1.35, blended));
            }
            if (RestR > 0)
            {
                // 2.0, not 1.45. The old threshold sat at RestR*1.45 = 59.5px while the swinging
                // radius measures 40-46px, so a few stray rope pixels in the centroid were enough to
                // latch "the hook is out" - and `ready` requires !HookDeployed, so every shot taken
                // during that window was suppressed. A live round showed dep=1 on 23 of 58 tracked
                // frames, the hook reaching the aim 9 times and the fire window 3 times, yet only
                // ONE shot: the guard was eating them. A genuine shot sends the hook hundreds of
                // pixels out, so 2x the resting radius still cannot be reached by swing noise.
                if (len > RestR * 2.0)
                {
                    HookDeployed = true; RestFrames = 0;
                    if (len > DeployPeak) DeployPeak = len;      // how far out it actually got
                }
                else if (len < RestR * 1.5)
                {
                    if (++RestFrames >= 6)
                    {
                        // Only a REAL shot arms the settle window. The latch can also cycle on a
                        // one-frame radius bump: 6 rest frames + 1 deploy frame is ~7 changed frames
                        // = 0.39s at 18fps, which is SHORTER than SettleMs (0.5s), so re-arming on
                        // every clear pinned `settling` permanently true and suppressed every shot -
                        // the cycle that kept being reported as "[stuck] storm" was only the visible
                        // symptom of it. A genuine shot takes the hook hundreds of pixels out first,
                        // so require that before treating the return as the end of a shot.
                        if (HookDeployed && DeployPeak > RestR * 2.5) LastReturn = DateTime.Now;
                        HookDeployed = false;
                        DeployPeak = 0;
                    }
                }
                else RestFrames = 0;
            }
            return true;
        }

        /// <summary>Peak tracked radius during the current deployment; distinguishes a real shot from
        /// a one-frame latch cycle.</summary>
        public double DeployPeak;

        public int RestFrames;
        public DateTime LastReturn = DateTime.MinValue;

        public double HookX, HookY, HookR;
        public double RestR;
        public double LastDt = 0.016;

        // ---- target classification along a ray -------------------------------
        public const int None = 0, Rock = 1, GoldT = 2, Bag = 3, Diamond = 4, Unknown = 5, Small = 6;

        // Area of the connected object under a point, capped so a huge blob cannot run away.
        // Buffers are reused because this runs on every frame while an alert is possible.
        bool[] _areaSeen; int[] _areaStack; int _areaW, _areaH;
        public int LastObjW, LastObjH;
        public int LastObjX0, LastObjY0, LastObjX1, LastObjY1;
        public double LastObjCx, LastObjCy;      // centre of mass of the object

        public int ObjectArea(int sx, int sy, int cap)
        {
            const int R = 115;
            if (ObjectMask == null) return cap;
            int W = _ow, H = ObjectMask.Length / Math.Max(1, W);
            if (sx < 0 || sy < 0 || sx >= W || sy >= H) return 0;
            if (!ObjectMask[sy * W + sx]) return 0;
            int x0 = Math.Max(0, sx - R), x1 = Math.Min(W - 1, sx + R);
            int y0 = Math.Max(0, sy - R), y1 = Math.Min(H - 1, sy + R);
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            if (_areaSeen == null || _areaW < bw || _areaH < bh)
            {
                _areaW = Math.Max(bw, 260); _areaH = Math.Max(bh, 260);
                _areaSeen = new bool[_areaW * _areaH];
                _areaStack = new int[_areaW * _areaH];
            }
            Array.Clear(_areaSeen, 0, bw * bh);
            int sp = 0, area = 0;
            long sumX = 0, sumY = 0;
            int bx0 = bw, bx1 = -1, by0 = bh, by1 = -1;
            int start = (sy - y0) * bw + (sx - x0);
            _areaSeen[start] = true; _areaStack[sp++] = start;
            while (sp > 0)
            {
                int p = _areaStack[--sp];
                int x = p % bw, y = p / bw;
                if (x < bx0) bx0 = x; if (x > bx1) bx1 = x;
                if (y < by0) by0 = y; if (y > by1) by1 = y;
                sumX += x + x0; sumY += y + y0;
                if (++area >= cap) break;
                if (x > 0) { int q = p - 1; if (!_areaSeen[q] && ObjectMask[(y + y0) * W + (x - 1 + x0)]) { _areaSeen[q] = true; _areaStack[sp++] = q; } }
                if (x < bw - 1) { int q = p + 1; if (!_areaSeen[q] && ObjectMask[(y + y0) * W + (x + 1 + x0)]) { _areaSeen[q] = true; _areaStack[sp++] = q; } }
                if (y > 0) { int q = p - bw; if (!_areaSeen[q] && ObjectMask[(y - 1 + y0) * W + (x + x0)]) { _areaSeen[q] = true; _areaStack[sp++] = q; } }
                if (y < bh - 1) { int q = p + bw; if (!_areaSeen[q] && ObjectMask[(y + 1 + y0) * W + (x + x0)]) { _areaSeen[q] = true; _areaStack[sp++] = q; } }
            }
            // Report the bounding-box area, not the pixel count: when only an object's outline is
            // detected the interior is hollow, and counting pixels would call a big nugget small.
            LastObjW = bx1 < bx0 ? 0 : bx1 - bx0 + 1;
            LastObjH = by1 < by0 ? 0 : by1 - by0 + 1;
            LastObjX0 = bx0 < bw ? bx0 + x0 : 0;
            LastObjY0 = by0 < bh ? by0 + y0 : 0;
            LastObjX1 = bx1 >= 0 ? bx1 + x0 : 0;
            LastObjY1 = by1 >= 0 ? by1 + y0 : 0;
            LastObjCx = area > 0 ? (double)sumX / area : (bx0 + bx1) / 2.0 + x0;
            LastObjCy = area > 0 ? (double)sumY / area : (by0 + by1) / 2.0 + y0;
            if (bx1 < bx0) return 0;
            return LastObjW * LastObjH;
        }

        public int RayHit(double angle, out double hitR)
        {
            hitR = 0;
            if (Cur == null) return None;
            double sin = Math.Sin(angle), cos = Math.Cos(angle);
            // Start just past the RESTING hook. Using the live HookR meant that while the hook was
            // out on its rope the ray began several hundred pixels away - beyond the very objects
            // the player was pointing at.
            double start = (RestR > 0 ? RestR : HookR) + 36;

            int run = 0;                       // consecutive object samples (anti-speckle)
            for (double rr = start; rr < 3000; rr += 2)
            {
                int x = (int)(PivotX + sin * rr), y = (int)(PivotY + cos * rr);
                if (x < Field.Left + 8 || x > Field.Right - 8 || y > Field.Bottom) break;
                if (y < Field.Top + 62) { run = 0; continue; }   // the ledge / HUD strip
                int c = Cur.At(x, y);
                if (c == -1) break;
                if (!IsObject(x, y)) { run = 0; continue; }
                run++;
                if (run < 10) continue;        // ~20px of solid object
                hitR = rr;

                // Reject the blobs that plain dirt produces along its colour-band edges. They are
                // long thin STRIPS; real objects are compact. Using shape rather than a flat size
                // cut-off matters because a diamond is small but perfectly chunky, and a 45px rule
                // was throwing the diamonds away.
                ObjectArea(x, y, 40000);
                int ow = LastObjW, oh = LastObjH;
                int omn = Math.Min(ow, oh), omx = Math.Max(ow, oh);
                if (omn < 20 || (omn > 0 && omx > omn * 4)) { run = 0; continue; }

                int gold = 0, bag = 0, dia = 0, rock = 0, obj = 0;
                for (int oy = -8; oy <= 8; oy += 2)
                    for (int ox = -8; ox <= 8; ox += 2)
                    {
                        int c2 = Cur.At(x + ox, y + oy);
                        if (c2 == -1) continue;
                        if (!IsObject(x + ox, y + oy)) continue;
                        obj++;
                        if (Cls.Gray(c2)) rock++;
                        else if (Cls.GoldPixel(c2)) gold++;
                        else if (Cls.BagPixel(c2)) bag++;
                        else if (Cls.GemPixel(c2)) dia++;
                    }
                if (obj == 0) return Unknown;
                // Grey is the only colour judgement that holds across every level theme: rocks are
                // neutral grey, everything else is grabbable.
                //
                // There is deliberately NO size rule. Whether a small nugget is worth taking is the
                // player's business, not this program's - if the pointer is on something that reads
                // as a valuable object, that is the only question that matters.
                if (rock * 2 >= obj) return Rock;
                if (dia * 2 >= obj) return Diamond;
                if (bag * 2 >= obj) return Bag;
                return GoldT;
            }
            return None;
        }

        // Minimum nugget area worth alerting on, as a fraction of the play area, so it scales with
        // the window. Measured on a real level: tiny nuggets 150-1500px, medium 3200-4500px,
        // big ones 17000-19000px - so this sits in the empty gap above "medium".
        public int MinGoldArea()
        {
            int a = (int)(Field.Width * (double)Field.Height * 0.0035);
            return a < 1500 ? 1500 : a;
        }

        // Angular span of whatever the pointer is aimed at. A big nugget covers a wide arc and the
        // countdown should run against that whole arc, not a single angle.
        //
        // ANY object counts, not only the ones the colour classifier likes: that classifier is the
        // least reliable part of this program, and demanding it say "gold" was silently disabling
        // the countdown for perfectly valid targets.
        public bool AimSpan(double aimAngle, out double a0, out double a1, out double centre, out int kind)
        {
            a0 = a1 = centre = 0; kind = None;
            // One ray is fragile: if it happens to slip through a gap in the object mask (the
            // outline against a pale band is faint) the whole target is declared missing even
            // though the player is plainly pointing at a nugget. Sweep a few degrees either side
            // before giving up - the player aims at a blob, not at an exact pixel.
            double hitR = 0, used = aimAngle;
            bool found = false;
            // +-2 degrees only. This sweep exists to bridge a small gap in the object mask, but at
            // +-5 it could hop from the diamond the player was pointing at onto the money bag beside
            // it - the status line read GOLD while a diamond was under the cursor.
            double[] offs = { 0, 0.035, -0.035 };
            foreach (double off in offs)
            {
                double a = aimAngle + off;
                int k = RayHit(a, out hitR);
                if (k != None && hitR > 0) { kind = k; used = a; found = true; break; }
            }
            if (!found) return false;

            int hx = (int)(PivotX + Math.Sin(used) * hitR);
            int hy = (int)(PivotY + Math.Cos(used) * hitR);
            ObjectArea(hx, hy, 40000);
            int x0 = LastObjX0, y0 = LastObjY0, x1 = LastObjX1, y1 = LastObjY1;
            if (x1 <= x0 || y1 <= y0) return false;

            // Aim at the object's CENTRE, not its edge. The countdown used to finish when the hook
            // reached the leading edge of the blob, so "1" meant "pointing at the near rim" - the
            // hook then flew visibly off-centre. The centre of mass is where the hook should go.
            centre = Math.Atan2(LastObjCx - PivotX, LastObjCy - PivotY);

            // Nothing in the game is larger than the biggest gold nugget, so cap the box at that
            // size. Without the cap a mis-merged blob (a diamond fused to the nugget next to it)
            // reports a span several times too wide and the countdown runs far too long.
            int maxW = (int)(Field.Width * 0.105), maxH = (int)(Field.Height * 0.19);
            if (x1 - x0 > maxW) { int cx = (x0 + x1) / 2; x0 = cx - maxW / 2; x1 = cx + maxW / 2; }
            if (y1 - y0 > maxH) { int cy = (y0 + y1) / 2; y0 = cy - maxH / 2; y1 = cy + maxH / 2; }

            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < 4; i++)
            {
                double cx = (i & 1) == 0 ? x0 : x1;
                double cy = (i & 2) == 0 ? y0 : y1;
                double a = Math.Atan2(cx - PivotX, cy - PivotY);
                if (a < lo) lo = a;
                if (a > hi) hi = a;
            }
            lo -= 0.01; hi += 0.01;
            a0 = lo; a1 = hi;
            return true;
        }

        public static string ClassName(int k)
        {
            switch (k)
            {
                case Rock: return "rock";
                case GoldT: return "GOLD";
                case Bag: return "MONEY BAG";
                case Diamond: return "DIAMOND";
                case Small: return "小块黄金(不提示)";
                case Unknown: return "unknown";
                default: return "none";
            }
        }

        // ---- drawing ---------------------------------------------------------
        // Where the shot is predicted to go LeadMs from now. Set by the run loop so the overlay can
        // draw the aim line without changing DrawOverlay's signature everywhere.
        public double PredAngle;
        public double PredHitR;
        public bool PredValid;
        public int Countdown;          // 3/2/1 shown as floating text; 0 = nothing

        // Numbers that are on their way out: they drift up and fade instead of blinking off.
        public class Ghost { public string Text; public double Age; public bool Green; }
        public List<Ghost> Ghosts = new List<Ghost>();
        public double GhostLife = 1.25;      // seconds, from the config

        public void SetCountdown(int n)
        {
            if (n == Countdown) return;
            // Only ever ghost a number that was actually on screen, and never spawn the same digit
            // twice within a fraction of a second - otherwise a jittery signal stacks up copies.
            if (Countdown > 0 && !(Ghosts.Count > 0 && Ghosts[Ghosts.Count - 1].Text == Countdown.ToString(CultureInfo.InvariantCulture) && Ghosts[Ghosts.Count - 1].Age < 0.30))
                Ghosts.Add(new Ghost { Text = Countdown.ToString(CultureInfo.InvariantCulture), Age = 0, Green = Countdown == 1 });
            Countdown = n;
        }

        public void DrawOverlay(Graphics g, int w, int h, bool alert, string status)
        {
            // A dashed line along the pointer direction, shown only while the pointer is on
            // something grabbable. It marks the exact line the hook has to sweep across, so a miss
            // is visible immediately instead of having to be guessed at - and the fire log records
            // the angle between this line and the direction the hook actually took.
            if (!PredValid) return;
            // Never draw past the play area. Falling back to max(w,h) sent the line into the screen
            // corner whenever the ray found nothing, which reads as "it is aiming somewhere else"
            // when actually it is the ray test that failed.
            double reach = PredHitR > 60 ? PredHitR : (Field.Height * 1.05);
            float x1 = (float)(PivotX + Math.Sin(PredAngle) * reach);
            float y1 = (float)(PivotY + Math.Cos(PredAngle) * reach);
            using (Pen pen = new Pen(Color.FromArgb(235, 255, 50, 50), 3))
            {
                pen.DashStyle = DashStyle.Custom;
                pen.DashPattern = new float[] { 6f, 5f };
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawLine(pen, (float)PivotX, (float)PivotY, x1, y1);
            }
            using (Pen pen = new Pen(Color.FromArgb(255, 255, 255, 255), 2))
                g.DrawEllipse(pen, x1 - 9, y1 - 9, 18, 18);
        }

        public void DrawDebug(Graphics g, int w, int h, string status)
        {
            if (C.DebugDraw && Cur != null)
            {
                using (Pen pen = new Pen(Color.FromArgb(140, 0, 255, 255), 1))
                {
                    for (int k = -9; k <= 9; k++)
                    {
                        double a = k * 0.17;
                        g.DrawLine(pen, (float)PivotX, (float)PivotY,
                            (float)(PivotX + Math.Sin(a) * 1400), (float)(PivotY + Math.Cos(a) * 1400));
                    }
                    g.DrawEllipse(pen, (float)(PivotX - BaseR), (float)(PivotY - BaseR), (float)(BaseR * 2), (float)(BaseR * 2));
                    g.DrawRectangle(pen, Field);
                }
                using (SolidBrush br = new SolidBrush(Color.FromArgb(120, 255, 0, 255)))
                    foreach (Rectangle r0 in StaticRocks) g.FillRectangle(br, r0);
                using (Pen pen = new Pen(Color.FromArgb(200, 0, 255, 0), 2))
                    for (int k = -14; k <= 14; k++)
                    {
                        double a = Angle + k * 0.02;
                        g.DrawLine(pen, (float)(PivotX + Math.Sin(a) * (BaseR + 40)), (float)(PivotY + Math.Cos(a) * (BaseR + 40)),
                                        (float)(PivotX + Math.Sin(a) * (BaseR + 46)), (float)(PivotY + Math.Cos(a) * (BaseR + 46)));
                    }
                // where we think the hook is, and how far it is from the pivot
                using (Pen pen = new Pen(Color.FromArgb(230, 255, 0, 255), 2))
                {
                    g.DrawLine(pen, (float)PivotX, (float)PivotY, (float)HookX, (float)HookY);
                    g.DrawEllipse(pen, (float)(HookX - 6), (float)(HookY - 6), 12, 12);
                    g.DrawEllipse(pen, (float)(PivotX - 5), (float)(PivotY - 5), 10, 10);
                    double rr = Math.Max(RestR, 1);
                    g.DrawEllipse(pen, (float)(PivotX - rr), (float)(PivotY - rr), (float)(rr * 2), (float)(rr * 2));
                }
            }
            if (C.ShowStatus && status != null)
            {
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
                using (SolidBrush fg = new SolidBrush(Color.FromArgb(255, 255, 255, 120)))
                using (Font f = new Font("Consolas", 13f, FontStyle.Bold))
                {
                    SizeF sz = g.MeasureString(status, f);
                    // along the bottom edge so the HUD (money / target / timer) stays readable
                    float bx = 8, by = h - sz.Height - 12;
                    g.FillRectangle(bg, bx, by, sz.Width + 14, sz.Height + 8);
                    g.DrawString(status, f, fg, bx + 6, by + 4);
                }
            }
        }
    }
}
