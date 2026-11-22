using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdeskPro.Ui;

/// <summary>Window capture via GDI BitBlt/PrintWindow, encoded as PNG through WPF imaging.</summary>
public static class Screenshot
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    public static byte[] CapturePng(long hwnd)
    {
        if (hwnd <= 0) throw new ArgumentException("hwnd must be a real window handle");
        if (!GetWindowRect(new IntPtr(hwnd), out var rect))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed");
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) throw new ArgumentException("window rect is empty");

        var hdcWindow = GetWindowDC(new IntPtr(hwnd));
        try
        {
            var hdcMem = CreateCompatibleDC(hdcWindow);
            try
            {
                var hbm = CreateCompatibleBitmap(hdcWindow, width, height);
                try
                {
                    SelectObject(hdcMem, hbm);
                    if (!PrintWindow(new IntPtr(hwnd), hdcMem, PW_RENDERFULLCONTENT))
                        BitBlt(hdcMem, 0, 0, width, height, hdcWindow, 0, 0, 0x00CC0020 /*SRCCOPY*/);

                    var bmi = new BITMAPINFO();
                    bmi.biHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                    bmi.biHeader.biWidth = width;
                    bmi.biHeader.biHeight = -height; // top-down
                    bmi.biHeader.biPlanes = 1;
                    bmi.biHeader.biBitCount = 32;
                    bmi.biHeader.biCompression = 0; // BI_RGB
                    var pixels = new byte[width * height * 4];
                    if (GetDIBits(hdcMem, hbm, 0, (uint)height, pixels, ref bmi, 0) == 0)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDIBits failed");

                    var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                        pixels, width * 4);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(source));
                    using var ms = new MemoryStream();
                    encoder.Save(ms);
                    return ms.ToArray();
                }
                finally { DeleteObject(hbm); }
            }
            finally { DeleteDC(hdcMem); }
        }
        finally { ReleaseDC(new IntPtr(hwnd), hdcWindow); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER biHeader;
        public uint biColors;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
