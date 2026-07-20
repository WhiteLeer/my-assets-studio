using System;
using System.Runtime.InteropServices;

namespace AnimeStudio
{
    public static class FontHelper
    {
        [DllImport("gdi32.dll")]
        public static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);
    }
}
