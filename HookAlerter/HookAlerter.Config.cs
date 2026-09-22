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
    internal class Cfg
    {
        // Bump whenever the tracker changes in a way that invalidates saved geometry,
        // so an ini written by an older build can never poison a new one.
        public const int CfgVersion = 2;

        public int LeadMs = 0;
        public int SettleMs = 500;    // ignore everything for this long after the hook comes back
        public bool AutoFire = true;   // fire the hook for the player at the "1" (on by default)
        public bool ShowStatus = false;         // off by default: the user wants no floating text on the game
        public bool DebugDraw = false;
        public int PivotX = -1, PivotY = -1;   // -1 = auto calibrate
        public int Radius = -1;
        public int FieldL = -1, FieldT = -1, FieldR = -1, FieldB = -1;
        public bool ManualGeometry;             // true only when loaded from a hand-edited ini
        public bool GeometryTrusted;            // a calibration that passed every sanity check
        public int ClientW = -1, ClientH = -1;  // geometry is only valid for this client size

        public static Cfg Load(string path)
        {
            Cfg c = new Cfg();
            if (!File.Exists(path)) return c;
            int ver = 0;
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    int n;
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                    switch (k)
                    {
                        case "version": ver = n; break;
                        case "leadms": c.LeadMs = n; break;
                        case "settlems": c.SettleMs = n; break;
                        case "autofire": c.AutoFire = n != 0; break;
                        case "showstatus": c.ShowStatus = n != 0; break;
                        case "debugdraw": c.DebugDraw = n != 0; break;
                        case "pivotx": c.PivotX = n; break;
                        case "pivoty": c.PivotY = n; break;
                        case "radius": c.Radius = n; break;
                        case "fieldl": c.FieldL = n; break;
                        case "fieldt": c.FieldT = n; break;
                        case "fieldr": c.FieldR = n; break;
                        case "fieldb": c.FieldB = n; break;
                        case "clientw": c.ClientW = n; break;
                        case "clienth": c.ClientH = n; break;
                    }
                }
            }
            catch { }
            if (ver != CfgVersion)
            {
                c.PivotX = c.PivotY = c.Radius = -1;
                c.FieldL = c.FieldT = c.FieldR = c.FieldB = -1;
            }
            c.ManualGeometry = c.PivotX > 0 && c.PivotY > 0 && c.Radius > 0;
            return c;
        }

        public void Save(string path)
        {
            // Does NOT normally persist pivot / radius / field - they are re-derived by calibration
            // on every start, and writing them back means one bad calibration would poison every
            // later run. BUT it DOES write them when ManualGeometry or GeometryTrusted is set (the
            // write block below), which is how an older build's adopted fit of 47..52 could end up
            // in the ini. That is why RunLoop clamps the saved radius on load - and why this comment
            // previously hid the very path a reviewer needed to see.
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# HookAlerter config. Close the tool before hand-editing.");
            sb.AppendLine("# Hotkeys: F6 type lead | F7 on/off | F8 recalibrate | F9 debug frame | F10/F11 lead -/+10ms");
            sb.AppendLine("# Leave PivotX/PivotY/Radius at -1 to auto-calibrate (recommended).");
            sb.AppendLine("Version=" + CfgVersion);
            sb.AppendLine("LeadMs=" + LeadMs);
            sb.AppendLine("SettleMs=" + SettleMs);
            sb.AppendLine("AutoFire=" + (AutoFire ? 1 : 0));
            sb.AppendLine("ShowStatus=" + (ShowStatus ? 1 : 0));
            sb.AppendLine("DebugDraw=" + (DebugDraw ? 1 : 0));
            bool keep = ManualGeometry || GeometryTrusted;
            sb.AppendLine("PivotX=" + (keep ? PivotX : -1));
            sb.AppendLine("PivotY=" + (keep ? PivotY : -1));
            sb.AppendLine("Radius=" + (keep ? Radius : -1));
            sb.AppendLine("FieldL=" + (keep ? FieldL : -1));
            sb.AppendLine("FieldT=" + (keep ? FieldT : -1));
            sb.AppendLine("FieldR=" + (keep ? FieldR : -1));
            sb.AppendLine("FieldB=" + (keep ? FieldB : -1));
            sb.AppendLine("ClientW=" + (keep ? ClientW : -1));
            sb.AppendLine("ClientH=" + (keep ? ClientH : -1));
            try { File.WriteAllText(path, sb.ToString()); } catch { }
        }
    }
}
