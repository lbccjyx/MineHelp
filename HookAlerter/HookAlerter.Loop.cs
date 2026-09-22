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
        static void RunLoop(WindowCapture cap, Vision v, Overlay ov, Cfg cfg, string cfgPath, CtrlPanel panel)
        {
            bool running = true;
            DateTime started = DateTime.Now;
            bool shotTaken = false;
            DateTime last = DateTime.Now;
            DateTime lastLearn = DateTime.MinValue;
            DateTime lastTrackOk = DateTime.Now;
            DateTime lastAutoFire = DateTime.MinValue;
            double prevAngle = 0; bool havePrevAngle = false;
            double flightAim = 0, flightA0 = 0, flightA1 = 0; DateTime flightUntil = DateTime.MinValue;
            double gameStep = 0.14;      // one game frame of swing, learned at runtime
                                         // start generous: too small and the very first shot
                                         // skips the nearest step and fires a whole step late
            double lastDir = 1;          // which way the swing is actually moving
            double dirA = 1; int dirStreak = 0;
            double[] jumps = new double[16]; int jumpN = 0, jumpI = 0;
            double[] periods = new double[16]; int perN = 0, perI = 0;
            double framePeriod = 0.055;              // how long one game position lasts
            int stableFrames = 0;                    // consecutive frames locked on the hook arc
            double stuckAngle = 999; DateTime stuckAt = DateTime.Now;
            bool stuckReleased = false;
            double stallAngle = 999; DateTime stallAt = DateTime.Now;
            DateTime lastRejectLog = DateTime.MinValue;
            DateTime lastTraceLog = DateTime.MinValue;
            DateTime lastCalTry = DateTime.MinValue;
            DateTime lastJumpAt = DateTime.Now, sampleAt = DateTime.Now;
            DateTime lastReturnSeen = DateTime.MinValue;

            // Per-frame decision trace, dumped around every shot. Guessing at the cause from
            // summary lines has cost far more than just recording what the program was thinking on
            // each frame leading up to a shot, so keep a rolling window of the last 100 frames and
            // print it, plus 25 frames after, whenever the hook fires.
            double[] overshoots = new double[8]; int ovN = 0, ovI = 0;
            double leadAdj = 0;                      // learned "how far past does it fly"
            double flightFireAngle = 0;
            string[] trace = new string[100];
            int traceI = 0, traceCount = 0, traceAfter = 0;
            bool tracing = false;
            bool alert = false;
            int lastHit = Vision.None;
            bool swOn = true;                 // F7 master switch for the red alert
            string status = "";
            bool prevF6 = false, prevF7 = false, prevF8 = false, prevF9 = false, prevF10 = false, prevF11 = false;
            bool calibrated = false;
            Bitmap canvas = null;

            // The pivot only depends on the window geometry, which does not change between rounds.
            // Reusing a calibration that already passed every sanity check means the next round is
            // ready instantly instead of spending its first seconds re-deriving the same numbers.
            bool sizeMatches = cfg.ClientW == cap.ClientScreenRect.Width && cfg.ClientH == cap.ClientScreenRect.Height;
            if (cfg.PivotX > 0 && cfg.PivotY > 0 && cfg.Radius > 0 && (cfg.ManualGeometry || sizeMatches))
            {
                v.PivotX = cfg.PivotX; v.PivotY = cfg.PivotY;
                // Derive the legal band from the CONFIG, not from v.Field. At this point v.Field is
                // still Rectangle.Empty - Vision's only constructor does not initialise it and every
                // assignment happens later in the loop - so reading v.Field.Height here yields 0, the
                // whole clamp silently never runs, and the 47..52 hole stays open. A reviewer caught
                // exactly that. The cfg field extents are already in hand.
                double legal = (cfg.FieldB > cfg.FieldT) ? (cfg.FieldB - cfg.FieldT) * 0.052 : 0;
                if (legal > 0 && (cfg.Radius < legal * 0.95 || cfg.Radius > legal * 1.05))
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "[cfg] saved radius {0} is outside the legal band {1:F1}..{2:F1} - ignoring it, will recalibrate",
                        cfg.Radius, legal * 0.95, legal * 1.05));
                    goto skipSavedPivot;
                }
                v.BaseR = cfg.Radius;
                v.RestR = cfg.Radius;
                v.PivotSource = "INI";
                if (cfg.FieldL >= 0 && cfg.FieldR > cfg.FieldL)
                    v.Field = new Rectangle(cfg.FieldL, cfg.FieldT, cfg.FieldR - cfg.FieldL, cfg.FieldB - cfg.FieldT);
                v.HaveAngle = false;
                calibrated = true;
                Console.WriteLine("[cfg] reusing saved pivot (" + cfg.PivotX + "," + cfg.PivotY + ") r=" + cfg.Radius + " src=INI");
            }
        skipSavedPivot:

            while (running)
            {
                if (RunSeconds > 0)
                {
                    double el = (DateTime.Now - started).TotalSeconds;
                    if (el >= RunSeconds) break;
                    if (!shotTaken && RunShot != null && el >= RunSeconds * 0.6)
                    {
                        shotTaken = true;
                        try
                        {
                            Size ss = new Size(Nat.GetSystemMetrics(0), Nat.GetSystemMetrics(1));
                            using (Bitmap b = new Bitmap(ss.Width, ss.Height, PixelFormat.Format32bppArgb))
                            {
                                using (Graphics g = Graphics.FromImage(b))
                                    g.CopyFromScreen(0, 0, 0, 0, ss, CopyPixelOperation.SourceCopy);
                                b.Save(RunShot, ImageFormat.Png);
                            }
                            Console.WriteLine("desktop shot (overlay should be visible) -> " + RunShot);
                        }
                        catch (Exception ex) { Console.WriteLine("shot failed: " + ex.Message); }
                    }
                }
                // The game window may have been closed and reopened: re-resolve it rather than
                // spending the rest of the session capturing a dead 0x0 handle. Done first so the
                // panel buttons (record / dump) also act on a live window.
                if (!cap.Usable)
                {
                    IntPtr again = FindGameWindow();
                    if (again != IntPtr.Zero && again != cap.Hwnd)
                    {
                        cap.Retarget(again);
                        v.JumpRejects = 0; v.PivotSource = "?";
                            Console.WriteLine("[win] re-targeted the game window -> " + again);
                        calibrated = false;
                        v.HaveAngle = false;
                        v.Field = Rectangle.Empty;
                    }
                    else
                    {
                        status = "找不到游戏窗口（游戏没开？）";
                        CtrlPanel.StatusText = status;
                        alert = false;
                        Thread.Sleep(500);
                        continue;
                    }
                }
                else if (cap.ClientScreenRect.Width <= 0 || cap.ClientScreenRect.Height <= 0)
                {
                    cap.Refresh();
                    Thread.Sleep(200);
                    continue;
                }

                // requests raised by the control panel (UI thread) or by hotkeys
                if (CtrlPanel.ReqToggle) { CtrlPanel.ReqToggle = false; swOn = !swOn; Console.WriteLine(swOn ? "提示开关: 开启" : "提示开关: 已暂停"); }
                if (CtrlPanel.ReqRecal) { CtrlPanel.ReqRecal = false; calibrated = false; v.JumpRejects = 0; v.PivotSource = "?"; v.HaveAngle = false; v.MinAngle = -1.6; v.MaxAngle = 1.6; Console.WriteLine("[recalibrate]"); }
                if (CtrlPanel.ReqStatus)
                {
                    CtrlPanel.ReqStatus = false;
                    cfg.ShowStatus = !cfg.ShowStatus;
                    cfg.Save(cfgPath);
                    Console.WriteLine("overlay text = " + (cfg.ShowStatus ? "on" : "off"));
                }
                int rl = CtrlPanel.ReqLead;
                if (rl >= 0) { CtrlPanel.ReqLead = -1; cfg.LeadMs = rl; cfg.Save(cfgPath); Console.WriteLine("lead=" + cfg.LeadMs); }
                int rt = CtrlPanel.ReqSettle;
                if (rt >= 0) { CtrlPanel.ReqSettle = -1; cfg.SettleMs = rt; cfg.Save(cfgPath); Console.WriteLine("settle=" + cfg.SettleMs); }
                if (CtrlPanel.ReqAuto)
                {
                    CtrlPanel.ReqAuto = false;
                    cfg.AutoFire = !cfg.AutoFire;
                    cfg.Save(cfgPath);
                    Console.WriteLine("autofire=" + cfg.AutoFire);
                    if (cfg.AutoFire)
                    {
                        // Switching auto fire on must make it usable AT ONCE. It used to need a
                        // manual shot first because the state was stale - a latched "hook is out"
                        // flag or timing values left over from before. Clear everything that could
                        // block the first automatic shot.
                        v.HookDeployed = false;
                        v.RestFrames = 0;
                        v.LastReturn = DateTime.MinValue;
                        v.LostFrames = 0;
                        lastAutoFire = DateTime.MinValue;
                        havePrevAngle = false;
                        dirStreak = 0;
                        Console.WriteLine("[auto] armed");
                    }
                }
                if (CtrlPanel.ReqRecord)
                {
                    CtrlPanel.ReqRecord = false;
                    CtrlPanel.StatusText = "正在录制诊断 8 秒…（录完看 HookAlerter\\diag）";
                    DoRecord(cap, v, 8.0);
                    calibrated = false;
                }

                bool f6 = Key(0x75), f7 = Key(0x76), f8 = Key(0x77), f9 = Key(0x78), f10 = Key(0x79), f11 = Key(0x7A);
                if (f6 && !prevF6 || CtrlPanel.ReqLeadEdit)
                {
                    CtrlPanel.ReqLeadEdit = false;
                    // the dialog must be created on the UI thread, which owns the message loop
                    bool ch = false;
                    try { ov.Invoke((MethodInvoker)delegate { ch = AskSettings(cfg); }); }
                    catch { }
                    if (ch)
                    {
                        cfg.Save(cfgPath);
                        Console.WriteLine("lead=" + cfg.LeadMs + " settle=" + cfg.SettleMs);
                    }
                }
                if (f7 && !prevF7)
                {
                    swOn = !swOn;
                Console.WriteLine(swOn ? "提示开关: 开启" : "提示开关: 已暂停");
                }
                if (f8 && !prevF8) { calibrated = false; v.JumpRejects = 0; v.PivotSource = "?"; v.HaveAngle = false; v.MinAngle = -1.6; v.MaxAngle = 1.6; Console.WriteLine("[recalibrate]"); }
                if (f10 && !prevF10) { cfg.LeadMs = Math.Max(0, cfg.LeadMs - 10); cfg.Save(cfgPath); Console.WriteLine("lead=" + cfg.LeadMs + "ms"); }
                if (f11 && !prevF11) { cfg.LeadMs = cfg.LeadMs + 10; cfg.Save(cfgPath); Console.WriteLine("lead=" + cfg.LeadMs + "ms"); }
                prevF6 = f6; prevF7 = f7; prevF8 = f8; prevF10 = f10; prevF11 = f11;

                // The game window may have been closed and reopened: re-resolve it rather than
                // spending the rest of the session capturing a dead 0x0 handle.
                if (!cap.Usable)
                {
                    IntPtr again = FindGameWindow();
                    if (again != IntPtr.Zero && again != cap.Hwnd)
                    {
                        cap.Retarget(again);
                        v.JumpRejects = 0; v.PivotSource = "?";
                            Console.WriteLine("[win] re-targeted the game window -> " + again);
                        calibrated = false;
                        v.HaveAngle = false;
                        v.Field = Rectangle.Empty;
                    }
                    else
                    {
                        Thread.Sleep(250);
                        continue;
                    }
                }

                bool skipTrack = false;
                // A fallback pivot is a STOPGAP, not a result. It gives usable geometry but it never
                // runs LearnObjects, so StaticRocks stays empty - and without that exclusion list
                // HookPixels happily locks onto a static grey blob near the pivot (the winch, the
                // miner). A live log proved it: 524 trace frames with ang pinned at -54.03 and
                // hx,hy pinned at (935,140), 37px from the pivot, while the real hook swung. A frozen
                // angle can never cross the pointer line, so the tool waits forever and the user
                // hooks by hand. "calibrated = true regardless of failure" made that permanent,
                // because the only retry was gone. So: if the geometry did not come from a real
                // calibration, keep trying until it does.
                if (calibrated && v.PivotSource != "ART" && v.PivotSource != "ART+FITr"
                    && (DateTime.Now - lastCalTry).TotalSeconds > 5.0)
                {
                    lastCalTry = DateTime.Now;
                    calibrated = false;
                    Console.WriteLine("[cal] geometry came from " + v.PivotSource + " - retrying a real calibration");
                }
                if (!calibrated)
                {
                    cfg.PivotX = -1;
                    if (!v.Calibrate(cap, 3, -1))
                    {
                        // Never let a failed calibration block the tool. The pivot comes from
                        // ArtPivot (a measured constant) and no longer from the fit, so a failure
                        // only means "static blobs were not learned and the radius was not
                        // refined" - the geometry is already valid.
                        //
                        // Blocking here was a real bug: the sample window is seconds long and a
                        // failure burns all of it with the main loop stopped, so nothing could fire.
                        // The player gave up waiting, hooked once by hand, and the hook's motion then
                        // made the next calibration succeed - exactly the reported "I have to hook
                        // one myself before auto fire starts working".
                        // ...but the pivot may only be derived from a frame whose GEOMETRY is
                        // trustworthy. The usual failure reason is "not in a level", and on such a
                        // frame Field came from a menu/shop screen, so ArtPivot() would place the
                        // pivot at THAT frame's centre: the log caught field={X=213,Width=1567} ->
                        // pivot x=997 instead of the true 966, a 31px error worth 23 degrees of angle
                        // at close radii, which hooked a big nugget when a small one was selected.
                        //
                        // Never block on this: `calibrated` staying false disables firing until the
                        // player presses F8. If the geometry is not trustworthy, keep whatever pivot
                        // we already have (from the ini or an earlier good calibration) and carry on.
                        // Note the object-count test is deliberately NOT used here - an audit measured
                        // real level frames at up to 13.5% objects and menus from 15.4%, and the
                        // level's own fraction oscillates across any threshold near 0.12 within
                        // seconds, so gating on it strands the tool on a real level.
                        if (v.FieldGeometrySaneNow())
                        {
                            v.ArtPivot(); v.PivotSource = "ART";
                            v.RestR = v.BaseR;
                            v.JumpRejects = 0;
                            Console.WriteLine("[cal] skipped (" + v.CalibMessage + ") - measured pivot src=" + v.PivotSource);
                        }
                        else if (v.PivotX <= 0 || v.BaseR <= 0)
                        {
                            // Nothing usable has ever been measured (fresh start, no ini, and the
                            // first frame is a menu). "Keeping the previous pivot" would keep ZERO:
                            // HookBox would sit at x=-150, HookPixels would find n=0 forever and the
                            // tracker would be blind for the entire session. Grab a frame here - the
                            // loop has not grabbed one yet at this point, which is why the first
                            // version of this fallback silently did nothing.
                            if (v.ArtPivotFromClient(cap.Grab()))
                            {
                                v.PivotSource = "CLIENT";
                                v.RestR = v.BaseR;
                                Console.WriteLine("[cal] skipped (" + v.CalibMessage + ") - fallback pivot from client size");
                            }
                            else
                            {
                                v.PivotSource = "NONE";
                                Console.WriteLine("[cal] skipped (" + v.CalibMessage + ") - FAILED to establish any geometry");
                            }
                        }
                        else
                        {
                            v.PivotSource = "PREV";
                            Console.WriteLine("[cal] skipped (" + v.CalibMessage + ") - keeping the previous pivot");
                        }
                        v.HookDeployed = false;
                        v.DeployPeak = 0;
                        v.LastReturn = DateTime.MinValue;
                        havePrevAngle = false;
                        dirStreak = 0;
                        stuckReleased = false;
                        calibrated = true;
                    }
                    else
                    {
                        cfg.Save(cfgPath);
                        calibrated = true;
                        // Arm auto fire the moment calibration finishes. Auto fire being on from the
                        // config had no equivalent of the "[auto] armed" reset that the switch does,
                        // so the session could start in a state that blocked the first shots.
                        if (cfg.AutoFire)
                        {
                            v.HookDeployed = false;
                            v.RestFrames = 0;
                            v.LastReturn = DateTime.MinValue;
                            v.LostFrames = 0;
                            lastAutoFire = DateTime.MinValue;
                            havePrevAngle = false;
                            dirStreak = 0;
                            Console.WriteLine("[auto] armed at startup");
                        }
                    }
                }

                Frame f = skipTrack ? null : cap.Grab();
                if (!skipTrack && f == null) { Thread.Sleep(30); continue; }
                if (!skipTrack)
                {
                if (v.Field.Width == 0)
                {
                    v.Field = v.DetectField(f);
                    v.LearnObjects(f);
                    lastLearn = DateTime.Now;
                }
                else if (v.OutlineMask == null || (DateTime.Now - lastLearn).TotalSeconds > 6)
                {
                    // the level can change palette between rounds; refresh the object mask
                    v.LearnObjects(f);
                    lastLearn = DateTime.Now;
                }
                v.Prev = v.Cur; v.Cur = f;

                // Ground truth for the shot: once the rope is out, its angle IS the direction the
                // hook is travelling. Measured independently of the hook tracker, so it shows
                // whether a miss was a timing error or a tracking error.
                if (flightUntil > DateTime.Now)
                {
                    // While the rope is out the tracker's radius is meaningless (it is following
                    // the moving cable) but the ANGLE is exactly the direction the hook flew, and
                    // it comes from an independent measurement of the frame rather than from the
                    // number the firing decision used. FindRope proved too strict to ever fire, so
                    // use the tracked angle once the hook is clearly out on the rope.
                    if (v.HookDeployed && v.HookR > 55)
                    {
                        // Learn the overshoot: how far past the firing angle the hook actually went.
                        double over = v.Angle - flightFireAngle;
                        // Only accept a believable overshoot. The hook flies at most a step or two
                        // past where it was fired; anything larger means the flight reading came
                        // from a tracking glitch, and the log caught exactly that - "over" of -37
                        // and +29 degrees while the tracker sat frozen on a stale position. Those
                        // bogus values were poisoning the learned lead.
                        if (Math.Abs(over) < 0.25)
                        {
                            // Store the MAGNITUDE. The overshoot is always along the direction of
                            // travel, and the swing reverses, so keeping the signed value would
                            // average the two directions to nothing; the sign is re-applied from
                            // the measured swing direction at firing time.
                            overshoots[ovI] = Math.Abs(over);
                            ovI = (ovI + 1) % overshoots.Length;
                            if (ovN < overshoots.Length) ovN++;
                            double[] so = new double[ovN];
                            Array.Copy(overshoots, so, ovN);
                            Array.Sort(so);
                            leadAdj = so[ovN / 2];
                        }
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "[flight] aim={0,7:F2}  fired={1,7:F2}  flew={2,7:F2}  over={3,6:F2}deg  lead={4,6:F2}deg  obj={5,6:F1}..{6,6:F1}",
                            flightAim * 180 / Math.PI, flightFireAngle * 180 / Math.PI,
                            v.Angle * 180 / Math.PI, over * 180 / Math.PI, leadAdj * 180 / Math.PI,
                            flightA0 * 180 / Math.PI, flightA1 * 180 / Math.PI));
                        flightUntil = DateTime.MinValue;
                    }
                }

                DateTime now = DateTime.Now;
                v.LastDt = Math.Max(0.004, (now - last).TotalSeconds);
                last = now;

                if (v.TrackHook(f))
                {
                    // A shot invalidates every timing estimate. When the hook comes back it can
                    // resume swinging the other way, and the previous angle belongs to before the
                    // shot - the previous code kept both, which is why the very same aim produced a
                    // fire difference of -6.6 degrees one moment and +6.5 the next: the lead was
                    // being applied in a stale direction. Forget the direction and the previous
                    // sample, and re-derive them from fresh movement.
                    if (v.LastReturn != lastReturnSeen)
                    {
                        lastReturnSeen = v.LastReturn;
                        lastDir = 0;                 // unknown -> no lead until movement is seen
                        dirStreak = 0;
                        havePrevAngle = false;        // no crossing across the gap either
                    }

                    double predicted = v.Angle + v.Omega * (cfg.LeadMs / 1000.0);
                    if (predicted > v.MaxAngle) predicted = v.MaxAngle;
                    if (predicted < v.MinAngle) predicted = v.MinAngle;

                    // ---- the whole decision ------------------------------------------
                    // The pointer picks the target; we take that object's angular SPAN, predict
                    // where the hook will be LeadMs from now, and turn it red whenever that
                    // prediction lands anywhere inside the span. A big nugget therefore stays red
                    // for its whole arc instead of for a single instant.
                    double mouseAngle = 0; bool haveMouse = false;
                    Nat.POINT mp;
                    if (Nat.GetCursorPos(out mp))
                    {
                        double mdx = (mp.X - cap.ClientScreenRect.X) - v.PivotX;
                        double mdy = (mp.Y - cap.ClientScreenRect.Y) - v.PivotY;
                        if (mdx * mdx + mdy * mdy > 900) { mouseAngle = Math.Atan2(mdx, mdy); haveMouse = true; }
                    }

                    double a0 = 0, a1 = 0, objCentre = 0; int mkind = Vision.None;
                    bool haveTarget = haveMouse && v.AimSpan(mouseAngle, out a0, out a1, out objCentre, out mkind);
                    // THE rule: the hook flies along the swing angle, so "point where you want it and
                    // the hook goes there" means firing at the instant the swing angle equals the
                    // angle from the pivot to the POINTER - not to the object's centre. Aiming at the
                    // centre sent the hook to the middle of a blob the player had pointed at the edge
                    // of. The centre is only kept for the status readout.
                    double aim = mouseAngle;
                    bool worth = mkind == Vision.GoldT || mkind == Vision.Bag || mkind == Vision.Diamond;

                    // Fire on the frame where the swing CROSSES the pointer line. Testing "is the
                    // angle within some tolerance" can miss the target entirely when the hook moves
                    // faster than the tolerance is wide; a sign change between two frames cannot be
                    // missed at all, so the hook lines up with the pointer 100% of the time.
                    // Settling: do not judge while the hook is still thrashing after a shot.
                    //
                    // This used to be a flat 500ms, which is both too long (the thrash is usually
                    // over well before that) and arbitrary (how long it lasts depends on how fast
                    // the hook was coming back). Instead wait for the track to actually be steady:
                    // the hook sitting on its arc for several frames running. The configured value
                    // is now only the upper bound, in case something never settles.
                    double sinceReturn = (DateTime.Now - v.LastReturn).TotalSeconds;
                    bool arcLocked = v.BaseR > 6 && v.HookR > v.BaseR * 0.85 && v.HookR < v.BaseR * 1.45;
                    if (!v.HookDeployed && arcLocked) stableFrames++;
                    else stableFrames = 0;
                    bool settling = sinceReturn < cfg.SettleMs / 1000.0
                                    && !(sinceReturn > 0.15 && stableFrames >= 8);

                    // Our loop runs far faster than the game's frame rate, so the tracked angle sits
                    // still for several of our frames and then jumps by one game step.
                    double moved = havePrevAngle ? Math.Abs(v.Angle - prevAngle) : 0;
                    // Only a real game-frame jump tells us the step size. Decaying on the still
                    // frames (most of them) collapsed the estimate toward zero, which shrank the
                    // window and pushed firing back onto the late path. Logs showed step -> 0.03deg.
                    if (moved > 0.003)
                    {
                        // Median of recent jumps, not the maximum. Taking the maximum was fooled by
                        // dropped frames: when the tracker misses one, the next observed jump spans
                        // TWO game steps and the maximum doubles - the log showed step swinging
                        // between 12 and 20 degrees when the true spacing is about 8. An inflated
                        // step widens the window to match and fires at a sample still far from the
                        // pointer line. The median ignores those double jumps.
                        jumps[jumpI] = moved;
                        jumpI = (jumpI + 1) % jumps.Length;
                        if (jumpN < jumps.Length) jumpN++;
                        double[] sorted = new double[jumpN];
                        Array.Copy(jumps, sorted, jumpN);
                        Array.Sort(sorted);
                        gameStep = sorted[jumpN / 2] * 1.15;

                        // How long a game position stays on screen. Two jumps apart gives it, and
                        // the median shrugs off a stutter.
                        double per = (now - lastJumpAt).TotalSeconds;
                        if (per > 0.005 && per < 0.5)
                        {
                            periods[perI] = per;
                            perI = (perI + 1) % periods.Length;
                            if (perN < periods.Length) perN++;
                            double[] sp = new double[perN];
                            Array.Copy(periods, sp, perN);
                            Array.Sort(sp);
                            framePeriod = sp[perN / 2];
                        }
                        lastJumpAt = now;
                        sampleAt = now;                 // a fresh position just appeared

                        lastDir = v.Angle > prevAngle ? 1.0 : -1.0;
                        if (lastDir == dirA) dirStreak++;
                        else { dirA = lastDir; dirStreak = 1; }
                    }
                    double tOnSample = Math.Max(0, (now - sampleAt).TotalSeconds);

                    // ---- one game frame of input latency --------------------------------
                    // Proven by the log: firing when the tracked angle read 29.12 against an aim of
                    // 29.21 still sent the hook to 36.34, and a read of 3.43 sent it to 12.23 - in
                    // both cases exactly one 8.02 degree step PAST the reading. The key press is
                    // only handled on the game's NEXT frame, so the comparison has to be made
                    // against where the hook will be when the press lands, not where it is now.
                    // Lead by the overshoot actually measured on previous shots. The trace showed
                    // the hook consistently flying PAST the angle it was fired at - by 1.3 to 8.1
                    // degrees, always in the direction of travel - which is why a target only 6.6
                    // degrees wide was missed every single time. Rather than guess the cause, use the
                    // measured overshoot: aim the comparison that far short of the pointer line, so
                    // the hook ends up ON it. Self-calibrating, and it adapts if the lag changes.
                    double ahead = lastDir * leadAdj;
                    double nowA = v.Angle + ahead;
                    double prevA2 = prevAngle + ahead;

                    bool crossed = false;
                    if (havePrevAngle)
                    {
                        double d1 = prevA2 - aim, d2 = nowA - aim;
                        crossed = (d1 <= 0 && d2 >= 0) || (d1 >= 0 && d2 <= 0);
                    }
                    prevAngle = v.Angle; havePrevAngle = true;

                    // Frozen-track stall: clear the crossing baseline. This is the real cure for the
                    // fake crossing that a magnitude gate could not fix - a frozen tracker holds the
                    // same angle for seconds, then snaps away, and comparing across that gap invents
                    // a crossing (the live log caught 3.79 -> -14.96 straight through the pointer
                    // line). Duplicate game frames coast for a frame or two normally, so the window
                    // is well above one game period.
                    if (Math.Abs(v.Angle - stallAngle) > 0.010) { stallAngle = v.Angle; stallAt = DateTime.Now; }
                    else if ((DateTime.Now - stallAt).TotalSeconds > 0.30 && havePrevAngle) havePrevAngle = false;

                    // Frozen-track detector - only valid when the hook is near the pivot. A hook
                    // flying out on its rope ALSO holds a near-constant angle (it travels in a
                    // straight line), so the naive version fired on every single normal shot: it
                    // released the "hook is out" latch mid-flight, the program thought the hook was
                    // back, and it fired again while the rope was still out. Require the radius to be
                    // near rest as well - that combination really is a dead track.
                    // The ceiling has to be genuinely NEAR the pivot, not merely "not far". The
                    // deployed latch trips at len > RestR*2.0 (81px) while this test accepted
                    // anything under BaseR*1.8 (~84px), so a tracked length in that band
                    // satisfied BOTH tests: the latch set itself, this released it, the latch set
                    // again - an oscillation that printed a run of 26 identical [stuck] lines and, by
                    // rewriting LastReturn every frame, kept the settle window permanently armed.
                    // ONE-SHOT. Making the two radius intervals disjoint was not enough, because one
                    // of them moves: the deployed latch trips at len > RestR*2.0 and RestR adapts
                    // anywhere in BaseR*0.75..1.35, so when RestR drifts low its threshold falls back
                    // inside this test's ceiling and the oscillation returns. The live log showed 36
                    // [stuck] lines inside a single level and ZERO shots, because each release
                    // rewrote LastReturn and kept the settle window permanently armed.
                    //
                    // So gate it explicitly: at most one release per genuine return. The flag clears
                    // only when the latch drops out on its own (RestFrames >= 6), i.e. the hook was
                    // actually seen sitting at rest again.
                    if (!v.HookDeployed && v.RestFrames >= 6) stuckReleased = false;

                    if (Math.Abs(v.Angle - stuckAngle) > 0.010)
                    {
                        stuckAngle = v.Angle;
                        stuckAt = DateTime.Now;
                    }
                    else if (!stuckReleased && v.HookDeployed && v.RestR > 6 && v.HookR < v.RestR * 1.10
                             && (DateTime.Now - stuckAt).TotalSeconds > 1.2)
                    {
                        v.HookDeployed = false;
                        v.RestFrames = 0;
                        v.LastReturn = DateTime.Now;
                        stuckReleased = true;
                        Console.WriteLine("[stuck] frozen track near the pivot - released the latch");
                    }

                    // Publish the learned step so the tracker's continuity gate can size itself
                    // against real motion instead of a flat 21.8 degrees.
                    v.GameStep = gameStep;

                    // Window: three quarters of a step, but with a FLOOR of 5 degrees. The step
                    // shrinks as the hook slows near the ends of its swing - the log showed step
                    // values of 2.81 - and the window shrank with it to 2.1 degrees, so a pass that
                    // came within 3 degrees of the line never fired at all. The floor means "if the
                    // hook gets within 5 degrees of the line, shoot" regardless of how slowly it is
                    // moving at that moment.
                    double half = Math.Max(0.087, gameStep * 0.75);
                    bool nearLine = Math.Abs(v.Angle - aim) <= half;

                    // Fire at the MIDDLE of the winning position's time on screen, not the instant
                    // it appears. The game only handles the key on its own next frame, and how much
                    // of that frame is left when we press is what made the latency look random -
                    // pressing early in the interval sometimes landed a whole step late. Waiting
                    // until the interval is nearly half over leaves at least half a frame of slack
                    // either way, so the game sees the position we aimed at.
                    bool settledOnIt = tOnSample >= framePeriod * 0.40;

                    bool ready = haveTarget && worth && !v.HookDeployed && !settling;
                    alert = ready && (crossed || (nearLine && settledOnIt));
                    v.Countdown = 0;

                    // ---- auto fire -------------------------------------------------------
                    bool firedNow = false;
                    if (cfg.AutoFire && alert && swOn && (DateTime.Now - lastAutoFire).TotalSeconds > 1.2)
                    {
                        if (Nat.GetForegroundWindow() != cap.Hwnd)
                        {
                            status += "   [自动出钩: 游戏不在前台]";
                        }
                        else
                        {
                            Nat.Key(0x28);                       // VK_DOWN
                            lastAutoFire = DateTime.Now;
                            firedNow = true;
                            // The measurement that matters: how far the direction the hook took is
                            // from the dashed line the player was shown.
                            double diffDeg = (v.Angle - aim) * 180 / Math.PI;
                            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                "[fire] aim={0,7:F2}  hook={1,7:F2}  diff={2,6:F2}deg  step={3,5:F2}deg  obj={4,6:F1}..{5,6:F1}  centre={6,6:F1}  r={7:F0}  {8}",
                                aim * 180 / Math.PI, v.Angle * 180 / Math.PI, diffDeg,
                                gameStep * 180 / Math.PI,
                                a0 * 180 / Math.PI, a1 * 180 / Math.PI, objCentre * 180 / Math.PI,
                                v.HookR, Vision.ClassName(mkind)));
                            // `hook` above is OUR tracked angle. The rope that unrolls after the
                            // shot is the ground truth for the direction the hook actually flew,
                            // so measure that too - otherwise the error figure is self-reported.
                            flightAim = aim;
                            flightA0 = a0; flightA1 = a1;
                            flightFireAngle = v.Angle;
                            flightUntil = DateTime.Now.AddSeconds(0.9);
                        }
                    }
                    v.PredValid = haveTarget && worth;
                    v.PredAngle = aim;
                    v.PredHitR = 0;
                    {
                        double hr2;
                        int k2 = v.RayHit(aim, out hr2);
                        if (k2 != Vision.None) v.PredHitR = hr2;
                    }
                    lastTrackOk = DateTime.Now;
                    lastHit = mkind;

                    status = string.Format(CultureInfo.InvariantCulture,
                        "[{0}]  钩子 {1,6:F1}°  鼠标 {2,6:F1}°  ({3})  差 {4,5:F1}°  {5}",
                        swOn ? "开启" : "已暂停",
                        v.Angle * 180 / Math.PI,
                        mouseAngle * 180 / Math.PI,
                        haveTarget ? Vision.ClassName(mkind) : "线上没有物体",
                        haveTarget ? (v.Angle - aim) * 180 / Math.PI : 0,
                        v.HookDeployed ? "(出钩中)" : (alert ? "★出钩！" : ""));

                    // One line per frame: everything the decision looked at.
                    {
                        string rec = string.Format(CultureInfo.InvariantCulture,
                            "[trace] ang={0,7:F2} aim={1,7:F2} d={2,6:F2} half={3,5:F2} obj={4,6:F1}..{5,6:F1} inObj={6} near={7} crossed={8} tOn={9,4:F0}ms step={10,5:F2} dep={11} setl={12} worth={13} hx={14:F0} hy={15:F0} piv={16:F0},{17:F0} hitR={18:F0} dpk={19:F0}{20}",
                            v.Angle * 180 / Math.PI, aim * 180 / Math.PI, (v.Angle - aim) * 180 / Math.PI,
                            half * 180 / Math.PI, a0 * 180 / Math.PI, a1 * 180 / Math.PI,
                            (v.Angle >= a0 && v.Angle <= a1) ? 1 : 0,
                            nearLine ? 1 : 0, crossed ? 1 : 0,
                            tOnSample * 1000, gameStep * 180 / Math.PI,
                            v.HookDeployed ? 1 : 0, settling ? 1 : 0, worth ? 1 : 0,
                            v.HookX, v.HookY, v.PivotX, v.PivotY, v.PredHitR, v.DeployPeak,
                            firedNow ? "   *** FIRE ***" : "");

                        if (firedNow)
                        {
                            Console.WriteLine("---- decision trace around a shot ----");
                            for (int k = 0; k < traceCount; k++)
                                Console.WriteLine(trace[(traceI - traceCount + k + trace.Length * 3) % trace.Length]);
                            Console.WriteLine(rec);
                            tracing = true; traceAfter = 25;
                        }
                        else if ((DateTime.Now - lastTraceLog).TotalSeconds > 1.0 && haveTarget)
                        {
                            // Heartbeat. Until now [trace] existed ONLY around a shot, so a session
                            // that never fired produced no trace at all and "why didn't it fire" was
                            // unanswerable - exactly what happened when the user waited 20 seconds
                            // for a gold nugget and hooked it by hand. One line per second (not per
                            // frame: ~18x cheaper) while a target is under the pointer, carrying the
                            // same fields the fire decision uses.
                            lastTraceLog = DateTime.Now;
                            Console.WriteLine(rec);
                        }
                        else if (tracing)
                        {
                            Console.WriteLine(rec);
                            if (--traceAfter <= 0) tracing = false;
                        }
                        trace[traceI] = rec;
                        traceI = (traceI + 1) % trace.Length;
                        if (traceCount < trace.Length) traceCount++;
                    }
                }
                else
                {
                    // Losing sight of the hook is NORMAL - it happens on every shot while the hook
                    // is out. Re-calibrating on that was the bug that made a whole 60s round go by
                    // in repeated calibration. Only give up if it has been gone for a long time.
                    alert = false;
                    v.PredValid = false;
                    // A gap in the track invalidates the crossing baseline. Without this, the
                    // continuity gate can reject a jump, accept it three frames later, and the
                    // crossing test then compares against an angle from BEFORE the gap - which is how
                    // a frozen tracker produced a fake crossing (3.79 -> -14.96 straight through the
                    // pointer line) and fired the hook at the wrong angle.
                    havePrevAngle = false;
                    double gone = (DateTime.Now - lastTrackOk).TotalSeconds;
                    // Re-calibrate only as a last resort, and only somewhere it can actually work.
                    // The pivot no longer comes from calibration at all (ArtPivot is authoritative,
                    // the fit may only refine the radius), so this exists solely to re-learn the
                    // static grey blobs - not worth the churn it caused. At 12s with no level test it
                    // fired three times while the player was still on the menu, each cycle costing a
                    // 3-second sample window, and the user measured the cost as "20 seconds to find
                    // the hook". Require a long absence AND a frame that looks like a level.
                    if (gone > 40.0 && v.ObjectFrac <= 0.145)
                    {
                        status = (swOn ? "[开启] " : "[已暂停] ") + "长时间找不到钩子，重新标定…（F8 立即重标）";
                        calibrated = false;
                    }
                    else
                    {
                        status = (swOn ? "[开启] " : "[已暂停] ") + "钩子出钩中 / 暂时看不到（正常，等它收回来）";
                    }
                    // Make a tracking failure diagnosable. Until now NO log field recorded one: the
                    // only evidence was the status string, so "the hook is plainly visible but it
                    // says it cannot see it" could not be investigated at all (skill §13.1 item 10 -
                    // an unverifiable change is no change). Rate limited to one line per second so a
                    // sustained failure cannot flood the log.
                    if ((DateTime.Now - lastRejectLog).TotalSeconds > 1.0)
                    {
                        lastRejectLog = DateTime.Now;
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "[miss] {0}  |  BaseR={1:F1} RestR={2:F1} gone={3:F1}s piv={4:F0},{5:F0} src={6} dpk={7:F0}",
                            v.Reject.Length == 0 ? "(no reason recorded)" : v.Reject,
                            v.BaseR, v.RestR, gone, v.PivotX, v.PivotY, v.PivotSource, v.DeployPeak));
                    }
                }
                }   // end if (!skipTrack)

                CtrlPanel.SwOn = swOn;
                CtrlPanel.LeadMs = cfg.LeadMs;
                CtrlPanel.SettleMs = cfg.SettleMs;
                CtrlPanel.AutoFireOn = cfg.AutoFire;
                CtrlPanel.StatusText = status;
                CtrlPanel.ShowStatusOn = cfg.ShowStatus;
                v.C.ShowStatus = cfg.ShowStatus;

                bool dumpReq = CtrlPanel.ReqDump;
                if (dumpReq) CtrlPanel.ReqDump = false;
                if ((f9 && !prevF9 || dumpReq) && f != null)
                {
                    string p = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath),
                                            "debug_" + DateTime.Now.ToString("HHmmss") + ".png");
                    using (Bitmap b = new Bitmap(f.W, f.H, PixelFormat.Format32bppArgb))
                    {
                        for (int y = 0; y < f.H; y++)
                            for (int x = 0; x < f.W; x++)
                                b.SetPixel(x, y, Color.FromArgb(255, (f.P[y * f.W + x] >> 16) & 255, (f.P[y * f.W + x] >> 8) & 255, f.P[y * f.W + x] & 255));
                        using (Graphics g = Graphics.FromImage(b)) v.DrawOverlay(g, f.W, f.H, alert, status);
                        b.Save(p, ImageFormat.Png);
                    }
                    Console.WriteLine("debug frame -> " + p);
                }
                prevF9 = f9;

                if (ov.Visible)
                {
                    int w = cap.ClientScreenRect.Width, h = cap.ClientScreenRect.Height;
                    int px = cap.ClientScreenRect.X, py = cap.ClientScreenRect.Y;
                    bool wantAlert = alert;
                    string wantStatus = status;
                    try
                    {
                        ov.Invoke((MethodInvoker)delegate
                        {
                            if (canvas == null || canvas.Width != w || canvas.Height != h)
                            {
                                if (canvas != null) canvas.Dispose();
                                // Format32bppPArgb: GDI+ premultiplies while drawing, which is what
                                // UpdateLayeredWindow + AC_SRC_ALPHA requires.
                                canvas = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                            }
                            using (Graphics g = Graphics.FromImage(canvas))
                            {
                                g.Clear(Color.Transparent);
                                v.DrawOverlay(g, w, h, wantAlert, wantStatus);
                                v.DrawDebug(g, w, h, wantStatus);
                            }
                            ov.Blit(canvas, px, py);
                        });
                    }
                    catch { }
                }

                Thread.Sleep(12);
            }

            if (RunSeconds > 0)
            {
                try { ov.BeginInvoke((MethodInvoker)delegate { ov.Close(); }); } catch { }
            }
        }

        // ---- offline validation of the classifier + ray caster ---------------
    }
}
