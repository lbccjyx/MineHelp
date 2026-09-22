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
    internal interface ICapture
    {
        Frame Grab();
        Rectangle ClientScreenRect { get; }
        bool Alive { get; }
    }

    internal class WindowCapture : ICapture
    {
        IntPtr _hwnd;
        int _w, _h, _ww, _wh, _offX, _offY;
        Bitmap _bmp;
        public Rectangle ClientScreenRect { get; private set; }

        public IntPtr Hwnd { get { return _hwnd; } }

        public WindowCapture(IntPtr hwnd)
        {
            _hwnd = hwnd;
            Refresh();
        }

        // The game window can be closed and reopened (a brand new handle) while we are running,
        // so the capture has to be able to point at a different window without being rebuilt.
        public void Retarget(IntPtr hwnd)
        {
            if (hwnd == _hwnd) return;
            _hwnd = hwnd;
            Refresh();
        }

        public bool Usable
        {
            get
            {
                Nat.RECT r;
                if (_hwnd == IntPtr.Zero || !Nat.GetClientRect(_hwnd, out r)) return false;
                return (r.Right - r.Left) > 0 && (r.Bottom - r.Top) > 0;
            }
        }

        public void Refresh()
        {
            Nat.RECT wr, cr;
            Nat.GetWindowRect(_hwnd, out wr);
            Nat.GetClientRect(_hwnd, out cr);
            Nat.POINT p = new Nat.POINT(); p.X = 0; p.Y = 0;
            Nat.ClientToScreen(_hwnd, ref p);
            _ww = wr.Right - wr.Left; _wh = wr.Bottom - wr.Top;
            _w = cr.Right - cr.Left; _h = cr.Bottom - cr.Top;
            _offX = p.X - wr.Left; _offY = p.Y - wr.Top;
            ClientScreenRect = new Rectangle(p.X, p.Y, _w, _h);

            if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
            _bmp = new Bitmap(_ww, _wh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        }

        public bool Alive
        {
            get
            {
                Nat.RECT r;
                return _hwnd != IntPtr.Zero && Nat.GetWindowRect(_hwnd, out r) && !Nat.IsIconic(_hwnd);
            }
        }

        public Frame Grab()
        {
            Frame f = new Frame(_w, _h);
            IntPtr wdc = Nat.GetWindowDC(_hwnd);
            if (wdc == IntPtr.Zero) return null;
            bool ok;
            using (Graphics g = Graphics.FromImage(_bmp))
            {
                IntPtr ddc = g.GetHdc();
                ok = Nat.BitBlt(ddc, 0, 0, _ww, _wh, wdc, 0, 0, Nat.SRCCOPY);
                g.ReleaseHdc(ddc);
            }
            Nat.ReleaseDC(_hwnd, wdc);
            if (!ok) return null;

            BitmapData bd = _bmp.LockBits(new Rectangle(0, 0, _ww, _wh),
                                          ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int[] row = new int[_ww];
            int[] dst = f.P;
            for (int y = 0; y < _h; y++)
            {
                Marshal.Copy((IntPtr)(bd.Scan0.ToInt64() + (long)(y + _offY) * bd.Stride), row, 0, _ww);
                int di = y * _w;
                int si = _offX;
                for (int x = 0; x < _w; x++) dst[di + x] = row[si + x] & 0x00FFFFFF;
            }
            _bmp.UnlockBits(bd);
            return f;
        }
    }

    internal class Overlay : Form
    {
        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Nat.WS_EX_LAYERED | Nat.WS_EX_TRANSPARENT | Nat.WS_EX_TOOLWINDOW | Nat.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void Blit(Bitmap bmp, int x, int y)
        {
            IntPtr screenDc = Nat.GetDC(IntPtr.Zero);
            IntPtr memDc = Nat.CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Nat.SelectObject(memDc, hBmp);

            Nat.SIZE size = new Nat.SIZE(); size.cx = bmp.Width; size.cy = bmp.Height;
            Nat.POINT src = new Nat.POINT(); src.X = 0; src.Y = 0;
            Nat.POINT dst = new Nat.POINT(); dst.X = x; dst.Y = y;
            Nat.BLENDFUNCTION bf = new Nat.BLENDFUNCTION();
            bf.BlendOp = Nat.AC_SRC_OVER; bf.SourceConstantAlpha = 255; bf.AlphaFormat = Nat.AC_SRC_ALPHA;
            Nat.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bf, Nat.ULW_ALPHA);

            Nat.SelectObject(memDc, old);
            Nat.DeleteObject(hBmp);
            Nat.DeleteDC(memDc);
            Nat.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
