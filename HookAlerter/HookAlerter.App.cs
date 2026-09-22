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
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--selftest") return SelfTest(args[1]);
            if (args.Length >= 1 && args[0] == "--captest") return CaptureTest(args.Length >= 2 ? args[1] : null);
            if (args.Length >= 1 && args[0] == "--aimtest") return AimTest();
            if (args.Length >= 1 && args[0] == "--tracetest")
                return TraceTest(args.Length >= 2 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 8.0);
            if (args.Length >= 5 && args[0] == "--analyze")
                return Analyze(args[1],
                    double.Parse(args[2], CultureInfo.InvariantCulture),
                    double.Parse(args[3], CultureInfo.InvariantCulture),
                    double.Parse(args[4], CultureInfo.InvariantCulture),
                    args.Length >= 6 ? args[5] : null);

            Nat.SetProcessDPIAware();
            Application.EnableVisualStyles();

            // The game freezes whenever it is not the foreground window, so this tool must never
            // steal focus - that is why it is built without a console and logs to a file instead.
            try
            {
                StreamWriter sw = new StreamWriter(Path.Combine(ToolDir(), "hookalerter.log"), false);
                sw.AutoFlush = true;
                Console.SetOut(sw);
            }
            catch { }

            if (args.Length >= 2 && args[0] == "--autoplay")
                return AutoPlay(int.Parse(args[1], CultureInfo.InvariantCulture));

            if (args.Length >= 2 && args[0] == "--liverun")
            {
                RunSeconds = double.Parse(args[1], CultureInfo.InvariantCulture);
                if (args.Length >= 3) RunShot = args[2];
            }

            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string cfgPath = Path.Combine(dir, "hookalerter.ini");
            Cfg cfg = Cfg.Load(cfgPath);

            Console.WriteLine("HookAlerter - Gold Miner: Classic Edition");
            Console.WriteLine("Looking for the game window (process GOLD) ...");
            IntPtr hwnd = FindGameWindow();
            for (int i = 0; hwnd == IntPtr.Zero && i < 120; i++)      // wait up to ~60s for the game
            {
                Thread.Sleep(500);
                hwnd = FindGameWindow();
            }
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine("Game window not found. Start the game first, then run this tool.");
                Console.WriteLine("(The tool keeps looking for it, so you can also start it afterwards.)");
                return 1;
            }

            WindowCapture cap = new WindowCapture(hwnd);
            Console.WriteLine("client " + cap.ClientScreenRect.Width + "x" + cap.ClientScreenRect.Height +
                              " at (" + cap.ClientScreenRect.X + "," + cap.ClientScreenRect.Y + ")");

            Vision v = new Vision(cfg);
            Overlay ov = new Overlay();
            ov.Show();

            CtrlPanel panel = new CtrlPanel();
            CtrlPanel.CfgRef = cfg;
            panel.PlaceNear(cap.ClientScreenRect);
            panel.Show();
            CtrlPanel.LeadMs = cfg.LeadMs;
            CtrlPanel.SettleMs = cfg.SettleMs;
            CtrlPanel.ShowStatusOn = cfg.ShowStatus;

            Console.WriteLine("Overlay shown. Close this console window to quit.");
            Console.WriteLine("Hotkeys: F6 lead | F7 on/off | F8 recalibrate | F9 debug frame | F10/F11 lead -/+10ms");

            Thread worker = new Thread(delegate () { RunLoop(cap, v, ov, cfg, cfgPath, panel); });
            worker.IsBackground = true;
            worker.Start();
            Application.Run(ov);
            return 0;
        }

        static IntPtr FindGameWindow()
        {
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("GOLD"))
            {
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            }
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle == "Gold Miner")
                        return p.MainWindowHandle;
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        // F6 modal: same two values as the panel, for when the panel is collapsed to a ball.
        static bool AskSettings(Cfg cfg)
        {
            bool changed = false;
            using (Form dlg = new Form())
            {
                dlg.Text = "设置 (毫秒)";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterScreen;
                dlg.MinimizeBox = false; dlg.MaximizeBox = false;
                dlg.TopMost = true;
                dlg.ClientSize = new Size(370, 132);
                dlg.Font = new Font("Microsoft YaHei UI", 9f);

                string[] names = { "提前量", "回钩静默" };
                string[] hints = { "0 = 钩子扫过鼠标线那一刻才出钩", "钩子刚回来这段时间不判断（防乱摆）" };
                int[] vals = { cfg.LeadMs, cfg.SettleMs };
                TextBox[] tb = new TextBox[2];
                for (int i = 0; i < 2; i++)
                {
                    Label l = new Label();
                    l.Text = names[i];
                    l.SetBounds(14, 12 + i * 46, 120, 20);
                    Label h = new Label();
                    h.Text = hints[i];
                    h.ForeColor = Color.Gray;
                    h.SetBounds(14, 30 + i * 46, 340, 18);
                    tb[i] = new TextBox();
                    tb[i].Text = vals[i].ToString(CultureInfo.InvariantCulture);
                    tb[i].SetBounds(200, 10 + i * 46, 76, 24);
                    dlg.Controls.Add(l); dlg.Controls.Add(h); dlg.Controls.Add(tb[i]);
                }
                Button ok = new Button();
                ok.Text = "确定"; ok.DialogResult = DialogResult.OK;
                ok.SetBounds(180, 96, 84, 28);
                Button cancel = new Button();
                cancel.Text = "取消"; cancel.DialogResult = DialogResult.Cancel;
                cancel.SetBounds(270, 96, 84, 28);
                dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok; dlg.CancelButton = cancel;

                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    int[] outv = { cfg.LeadMs, cfg.SettleMs };
                    int[] lo = { 0, 0 }, hi = { 2000, 8000 };
                    for (int i = 0; i < 2; i++)
                    {
                        int n;
                        if (int.TryParse(tb[i].Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                            && n >= lo[i] && n <= hi[i]) outv[i] = n;
                    }
                    if (outv[0] != cfg.LeadMs || outv[1] != cfg.SettleMs) changed = true;
                    cfg.LeadMs = outv[0]; cfg.SettleMs = outv[1];
                }
            }
            return changed;
        }

        static bool Key(int vk) { return (Nat.GetAsyncKeyState(vk) & 0x8000) != 0; }

        public static string ToolDir()
        {
            try
            {
                string d = Path.GetDirectoryName(Application.ExecutablePath);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
            }
            catch { }
            return Environment.CurrentDirectory;
        }

        static void SaveFramePng(Frame f, string path)
        {
            using (Bitmap b = new Bitmap(f.W, f.H, PixelFormat.Format32bppArgb))
            {
                for (int y = 0; y < f.H; y++)
                    for (int x = 0; x < f.W; x++)
                    {
                        int c = f.P[y * f.W + x];
                        b.SetPixel(x, y, Color.FromArgb(255, (c >> 16) & 255, (c >> 8) & 255, c & 255));
                    }
                b.Save(path, ImageFormat.Png);
            }
        }

        // Records a few seconds of the live game (only frames that actually changed) plus a text
        // report of every mover and its grey fraction. This is the evidence needed to fix tracking.
    }
}
