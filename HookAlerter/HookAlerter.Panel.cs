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
    internal class CtrlPanel : Form
    {
        // written by the worker thread, read by the 150ms UI timer
        public static volatile string StatusText = "";
        public static volatile bool SwOn = true;
        public static volatile int LeadMs = 0;
        public static volatile int SettleMs = 500;

        // set by the UI, consumed by the worker
        public static volatile bool ReqToggle;
        public static volatile bool ReqRecal;
        public static volatile bool ReqDump;
        public static volatile bool ReqRecord;
        public static volatile bool ReqStatus;
        public static volatile bool ReqAuto;
        public static volatile bool AutoFireOn;
        public static volatile bool ReqLeadEdit;
        public static volatile int ReqLead = -1;
        public static volatile int ReqSettle = -1;

        // The live config object. The panel writes straight into it so a changed timing applies on
        // the very next frame; routing it through the worker meant a change waited for whatever
        // long job the worker was in the middle of (calibration is seconds, recording is 8s).
        public static Cfg CfgRef;

        Button _sw, _recal, _dump, _rec, _miniBtn, _statusBtn, _autoBtn;
        TextBox _lead, _settle;
        Label _status;
        int _s;
        bool _mini, _prog;

        // One labelled, typed-in field on the panel. A field the user has edited is tagged so the
        // refresh timer will not stomp on it: it used to overwrite whatever was typed the instant
        // focus moved to the Apply button, so Apply then read back the old value.
        TextBox MakeField(string label, int x)
        {
            Label l = new Label();
            l.Text = label;
            l.SetBounds(Px(x, _s), Px(60, _s), Px(100, _s), Px(20, _s));
            TextBox tb = new TextBox();
            tb.SetBounds(Px(x, _s), Px(81, _s), Px(100, _s), Px(25, _s));
            tb.TextChanged += delegate { if (!_prog) tb.Tag = "edited"; };
            tb.KeyDown += delegate (object s2, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { ApplySettings(); e.SuppressKeyPress = true; }
            };
            Controls.Add(l);
            return tb;
        }

        void ShowValue(TextBox tb, int v)
        {
            if (tb.Focused || (tb.Tag != null && (string)tb.Tag == "edited")) return;
            _prog = true;
            tb.Text = v.ToString(CultureInfo.InvariantCulture);
            _prog = false;
            tb.BackColor = SystemColors.Window;
        }

        public CtrlPanel()
        {
            Text = "HookAlerter";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            // The process is DPI aware, so WinForms coordinates are physical pixels. Lay the panel
            // out in 96-dpi logical units and scale, otherwise a 200% display clips everything.
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9f);
            _s = DpiScale();

            ClientSize = new Size(Px(560, _s), Px(212, _s));

            // row 1: switch | overlay text | auto fire | collapse
            _sw = new Button();
            _sw.SetBounds(Px(12, _s), Px(12, _s), Px(148, _s), Px(34, _s));
            _sw.Click += delegate { ReqToggle = true; };
            _sw.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);

            _statusBtn = new Button();
            _statusBtn.SetBounds(Px(168, _s), Px(12, _s), Px(148, _s), Px(34, _s));
            _statusBtn.Click += delegate { ReqStatus = true; };

            _autoBtn = new Button();
            _autoBtn.SetBounds(Px(324, _s), Px(12, _s), Px(160, _s), Px(34, _s));
            _autoBtn.Click += delegate { ReqAuto = true; };
            _autoBtn.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);

            _miniBtn = new Button();
            _miniBtn.Text = "收起";
            _miniBtn.SetBounds(Px(492, _s), Px(12, _s), Px(56, _s), Px(34, _s));
            _miniBtn.Click += delegate { SetMini(true); };

            // row 2/3: four labelled values, one column each
            _lead = MakeField("提前量", 14);
            _settle = MakeField("回钩静默", 122);

            Button apply = new Button();
            apply.Text = "应用";
            apply.SetBounds(Px(230, _s), Px(80, _s), Px(74, _s), Px(28, _s));
            apply.Click += delegate { ApplySettings(); };

            // row 4: live status, on its own line so it can never sit on top of the fields
            _status = new Label();
            _status.SetBounds(Px(14, _s), Px(118, _s), Px(532, _s), Px(44, _s));
            _status.Text = "启动中…";
            _status.Font = new Font("Microsoft YaHei UI", 8.5f);
            _status.AutoSize = false;
            _status.BackColor = Color.FromArgb(238, 238, 238);
            _status.Padding = new Padding(Px(4, _s), Px(3, _s), Px(4, _s), Px(3, _s));

            // row 5: actions
            _recal = new Button();
            _recal.Text = "重新标定 (F8)";
            _recal.SetBounds(Px(12, _s), Px(168, _s), Px(160, _s), Px(32, _s));
            _recal.Click += delegate { ReqRecal = true; };

            _dump = new Button();
            _dump.Text = "保存诊断图 (F9)";
            _dump.SetBounds(Px(180, _s), Px(168, _s), Px(172, _s), Px(32, _s));
            _dump.Click += delegate { ReqDump = true; };

            _rec = new Button();
            _rec.Text = "录制诊断 8 秒";
            _rec.SetBounds(Px(360, _s), Px(168, _s), Px(160, _s), Px(32, _s));
            _rec.Click += delegate { ReqRecord = true; };

            Controls.Add(_sw); Controls.Add(_lead); Controls.Add(_settle);
            Controls.Add(apply); Controls.Add(_status); Controls.Add(_recal); Controls.Add(_dump);
            Controls.Add(_rec); Controls.Add(_miniBtn); Controls.Add(_statusBtn); Controls.Add(_autoBtn);

            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 150;
            t.Tick += delegate { Refresh_(); };
            t.Start();
        }

        static int Px(int logical, int s) { return (int)Math.Round(logical * s / 100.0); }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // Deliberately ACTIVATABLE: a NOACTIVATE window cannot receive keyboard input, so
                // the timing fields on it would be uneditable. ShowWithoutActivation below still
                // keeps it from stealing focus when it first appears.
                cp.ExStyle |= Nat.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // ---- collapse to a small round ball ---------------------------------
        public void SetMini(bool on)
        {
            _mini = on;
            SuspendLayout();
            foreach (Control c in Controls) c.Visible = !on;
            if (on)
            {
                FormBorderStyle = FormBorderStyle.None;
                ClientSize = new Size(Px(78, _s), Px(78, _s));
                GraphicsPath gp = new GraphicsPath();
                gp.AddEllipse(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
                Region = new Region(gp);
            }
            else
            {
                Region = null;
                FormBorderStyle = FormBorderStyle.FixedToolWindow;
                ClientSize = new Size(Px(542, _s), Px(212, _s));
            }
            ResumeLayout();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!_mini) return;
            bool on = SwOn;
            using (SolidBrush b = new SolidBrush(on ? Color.FromArgb(60, 190, 90) : Color.FromArgb(150, 150, 150)))
                e.Graphics.FillEllipse(b, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
            using (Pen p = new Pen(Color.White, Px(3, _s)))
                e.Graphics.DrawEllipse(p, 1, 1, ClientSize.Width - 3, ClientSize.Height - 3);
            using (SolidBrush b = new SolidBrush(Color.White))
            using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            {
                string tx = on ? "开" : "关";
                SizeF sz = e.Graphics.MeasureString(tx, f);
                e.Graphics.DrawString(tx, f, b, (ClientSize.Width - sz.Width) / 2, (ClientSize.Height - sz.Height) / 2);
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (_mini) SetMini(false);
        }

        // Let the whole panel be dragged from anywhere, not just the title bar.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0xA1 && m.WParam.ToInt32() == 1)     // WM_NCLBUTTONDOWN, HTCLIENT
            {
                Nat.ReleaseCapture();
                Nat.SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // HTCAPTION
                return;
            }
            base.WndProc(ref m);
        }

        static int DpiScale()
        {
            int dpi = 96;
            try
            {
                IntPtr dc = Nat.GetDC(IntPtr.Zero);
                if (dc != IntPtr.Zero) { dpi = Nat.GetDeviceCaps(dc, 88); Nat.ReleaseDC(IntPtr.Zero, dc); }
            }
            catch { }
            if (dpi < 96 || dpi > 480) dpi = 96;
            return dpi * 100 / 96;
        }

        void ApplySettings()
        {
            Check(_lead, 0, 2000, delegate (int v)
            {
                ReqLead = v; LeadMs = v;
                if (CfgRef != null) CfgRef.LeadMs = v;
            });
            Check(_settle, 0, 8000, delegate (int v)
            {
                ReqSettle = v; SettleMs = v;
                if (CfgRef != null) CfgRef.SettleMs = v;
            });
        }

        // Out-of-range input used to be dropped silently, so the box kept showing a number that
        // had never been applied. Now a rejected field is flagged in red and stays flagged until
        // it is fixed, so it is obvious whether a value took.
        void Check(TextBox tb, int lo, int hi, Action<int> apply)
        {
            int n;
            if (int.TryParse(tb.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= lo && n <= hi)
            {
                tb.BackColor = SystemColors.Window;
                tb.Tag = null;
                apply(n);
            }
            else
            {
                tb.BackColor = Color.FromArgb(255, 210, 210);
            }
        }

        void Refresh_()
        {
            bool on = SwOn;
            _sw.Text = on ? "提示：开启中" : "提示：已暂停";
            _sw.BackColor = on ? Color.FromArgb(120, 220, 120) : Color.FromArgb(200, 200, 200);
            ShowValue(_lead, LeadMs);
            ShowValue(_settle, SettleMs);
            _status.Text = StatusText;
            _autoBtn.Text = AutoFireOn ? "自动出钩：开" : "自动出钩：关";
            _autoBtn.BackColor = AutoFireOn ? Color.FromArgb(255, 150, 150) : Color.FromArgb(225, 225, 225);
            _statusBtn.Text = ShowStatusOn ? "画面文字：开" : "画面文字：关";
            _statusBtn.BackColor = ShowStatusOn ? Color.FromArgb(255, 225, 150) : Color.FromArgb(225, 225, 225);
        }

        public static volatile bool ShowStatusOn;

        // Keep it out of the play area: above the game window when there is room, else inside its
        // top-left corner.
        public void PlaceNear(Rectangle client)
        {
            int x = client.X + 8;
            int y = client.Y - Height - 8;
            if (y < 0) y = client.Y + 8;
            if (x + Width > Screen.PrimaryScreen.WorkingArea.Width)
                x = Math.Max(0, Screen.PrimaryScreen.WorkingArea.Width - Width - 8);
            Location = new Point(x, y);
        }
    }
}
