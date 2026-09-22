# Shared Win32 + GDI capture layer for the Gold Miner dev tools.
# Defines the [WinCap] type (idempotent - safe to dot-source repeatedly).

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ('WinCap' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;

public class WinCap {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("gdi32.dll")]  public static extern bool BitBlt(IntPtr dst, int x, int y, int cx, int cy, IntPtr src, int sx, int sy, int rop);
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int idx);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetActiveWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool alt);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);

    // Screen point -> client point (handles the window's non-client offset)
    public static POINT ScreenToClientPt(IntPtr h, int x, int y) {
        POINT c0; c0.X = 0; c0.Y = 0; ClientToScreen(h, ref c0);
        POINT cp; cp.X = x - c0.X; cp.Y = y - c0.Y;
        return cp;
    }

    // Post mouse messages straight to the window (no focus stealing needed).
    public static void PostClick(IntPtr h, int clientX, int clientY) {
        IntPtr lp = (IntPtr)((clientY << 16) | (clientX & 0xFFFF));
        PostMessage(h, 0x0200, IntPtr.Zero, lp);              // WM_MOUSEMOVE
        System.Threading.Thread.Sleep(40);
        PostMessage(h, 0x0201, (IntPtr)1, lp);                // WM_LBUTTONDOWN
        System.Threading.Thread.Sleep(60);
        PostMessage(h, 0x0202, IntPtr.Zero, lp);              // WM_LBUTTONUP
    }

    public static void BringToFront(IntPtr h) {
        if (IsIconic(h)) ShowWindow(h, 9);
        if (GetForegroundWindow() == h) return;
        uint fgT = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        uint myT = GetCurrentThreadId();
        keybd_event(0x12, 0, 0, IntPtr.Zero);   // ALT down  - unlocks SetForegroundWindow
        keybd_event(0x12, 0, 2, IntPtr.Zero);   // ALT up
        if (fgT != myT) AttachThreadInput(myT, fgT, true);
        try {
            ShowWindow(h, 9);
            SetWindowPos(h, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            BringWindowToTop(h);
            SetForegroundWindow(h);
            SetActiveWindow(h);
        } finally {
            if (fgT != myT) AttachThreadInput(myT, fgT, false);
        }
        if (GetForegroundWindow() != h) SwitchToThisWindow(h, true);
    }

    public static void Key(byte vk) {
        keybd_event(vk, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(45);
        keybd_event(vk, 0, 2, IntPtr.Zero);
    }

    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(70);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);   // LEFTDOWN
        System.Threading.Thread.Sleep(70);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);   // LEFTUP
    }

    public static void KeyDown(byte vk) { keybd_event(vk, 0, 0, IntPtr.Zero); }
    public static void KeyUp(byte vk)   { keybd_event(vk, 0, 2, IntPtr.Zero); }

    public static void MakeDpiAware() { try { SetProcessDPIAware(); } catch { } }
    public static Size ScreenSize() { return new Size(GetSystemMetrics(0), GetSystemMetrics(1)); }

    public static Bitmap FullDesktop() {
        Size s = ScreenSize();
        var bmp = new Bitmap(s.Width, s.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(0, 0, 0, 0, s, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_SHOWWINDOW = 0x40;

    // Screen copy over the window's outer rect (needs the window visible on top).
    public static Bitmap FromScreen(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        int w = r.R - r.L, ht = r.B - r.T;
        var bmp = new Bitmap(w, ht, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.L, r.T, 0, 0, new Size(w, ht), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    // BitBlt straight out of the window DC.
    public static Bitmap FromWindowDC(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        int w = r.R - r.L, ht = r.B - r.T;
        var bmp = new Bitmap(w, ht, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        IntPtr wdc = GetWindowDC(h);
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr ddc = g.GetHdc();
            BitBlt(ddc, 0, 0, w, ht, wdc, 0, 0, 0x00CC0020); // SRCCOPY
            g.ReleaseHdc(ddc);
        }
        ReleaseDC(h, wdc);
        return bmp;
    }

    public static Bitmap FromPrintWindow(IntPtr h, uint flags) {
        RECT r; GetWindowRect(h, out r);
        int w = r.R - r.L, ht = r.B - r.T;
        var bmp = new Bitmap(w, ht, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) {
            IntPtr ddc = g.GetHdc();
            PrintWindow(h, ddc, flags);
            g.ReleaseHdc(ddc);
        }
        return bmp;
    }

    // Average colour + how "alive" a bitmap looks (all-black => capture failed).
    public static string Describe(Bitmap b) {
        long sum = 0; int n = 0; var seen = new System.Collections.Generic.HashSet<int>();
        for (int y = 0; y < b.Height; y += 7)
            for (int x = 0; x < b.Width; x += 7) {
                int c = b.GetPixel(x, y).ToArgb() & 0xFFFFFF;
                seen.Add(c);
                sum += (c & 0xFF) + ((c >> 8) & 0xFF) + ((c >> 16) & 0xFF);
                n++;
            }
        return string.Format("avg={0:F1} distinctColors={1}", (double)sum / (3.0 * n), seen.Count);
    }
}
'@ -ReferencedAssemblies System.Drawing
}
