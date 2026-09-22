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
    internal class Frame
    {
        public int W, H;
        public int[] P;              // 0x00RRGGBB

        public Frame(int w, int h) { W = w; H = h; P = new int[w * h]; }

        public int At(int x, int y)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return -1;
            return P[y * W + x];
        }
    }

    internal static class Cls
    {
        public static int R(int c) { return (c >> 16) & 255; }
        public static int G(int c) { return (c >> 8) & 255; }
        public static int B(int c) { return c & 255; }
        public static int Lum(int c) { return (299 * R(c) + 587 * G(c) + 114 * B(c)) / 1000; }

        // Measured palette (Gold Miner: Classic Edition, 200% DPI capture):
        //   gold    #FEF400 / #FEE811   R 254  G 232-245  B 0-17
        //   rock    #7B7B7B             R=G=B 123
        //   bag     #EDCD8B / #FDA95A   R 237-253  B 90-139
        //   dirt    #6D5737..#DAAD6C    R 109-218  B 55-108   (never above R 218)

        // A gold pixel, judged by HUE not by absolute brightness. This is the one test that holds
        // in every level theme: measured G/R is 0.91-0.97 for gold, 0.59 for brown dirt, 0.71 for
        // sand and ~0.85 for the pale khaki dirt - so the gap is wide and stable.
        public static bool GoldPixel(int c)
        {
            int r = R(c), g = G(c), b = B(c);
            return r > 185 && g > 165 && b < 120 && g * 100 >= 90 * r;
        }
        // diamond / gem: bright and almost colourless
        public static bool GemPixel(int c)
        {
            int r = R(c), g = G(c), b = B(c);
            return r > 205 && g > 205 && b > 190;
        }
        // money bag body: pale cream. Deliberately strict - pale dirt can look similar, and a
        // false "money bag" on empty dirt is exactly the failure that made the alert useless.
        public static bool BagPixel(int c)
        {
            int r = R(c), g = G(c), b = B(c);
            return r > 232 && g > 208 && b > 150 && b < 210 && g * 100 >= 88 * r;
        }
        public static bool Goldish(int c)
        {
            int r = R(c), g = G(c), b = B(c);
            if (r > 225 && g > 200 && b < 90 && g * 100 >= 88 * r) return true;   // solid gold
            if (r > 228 && b >= 60 && g * 100 >= 93 * r) return true;             // gold's white highlight
            return false;
        }
        // diamond / white sparkle
        public static bool White(int c)
        {
            int r = R(c), g = G(c), b = B(c);
            return r > 213 && g > 213 && b > 195;
        }
        // money bag: brighter than any dirt, but neither gold nor white
        public static bool Cream(int c)
        {
            int r = R(c), b = B(c);
            return r > 228 && b >= 60 && !White(c) && !Goldish(c);
        }
        // neutral grey: rocks, hook, cable
        public static bool Gray(int c)
        {
            int r = R(c), g = G(c), b = B(c);
            int d1 = r - g, d2 = g - b, d3 = r - b;
            if (d1 < 0) d1 = -d1; if (d2 < 0) d2 = -d2; if (d3 < 0) d3 = -d3;
            int l = Lum(c);
            return d1 < 18 && d2 < 18 && d3 < 18 && l >= 55 && l <= 228;
        }
        // warm brown dirt / strata, explicitly excluding every object class above
        public static bool Dirt(int c)
        {
            int r = R(c), g = G(c), b = B(c);
            if (r <= 55 || r > 230) return false;
            if (r < g + 10 || g < b - 8 || (r - b) < 25) return false;
            // jitter around the dirt/ledge boundary is still dirt
            if (Goldish(c) || Cream(c) || White(c) || Gray(c)) return false;
            return true;
        }
        public static bool Dark(int c) { return Lum(c) < 60; }
    }
}
