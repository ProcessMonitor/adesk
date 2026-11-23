using System.Runtime.InteropServices;
using AdeskPro.Platform;

namespace AdeskPro.Mac;

/// <summary>
/// Window enumeration through <c>CGWindowListCopyWindowInfo</c>. This is the only reliable way to
/// list macOS windows from .NET: <c>Process.MainWindowHandle</c> is always zero on macOS, and
/// walking every AX application is slow and needs Accessibility permission just to count windows.
/// Each entry carries the CGWindowID, which is also what screenshots address.
/// </summary>
internal static class CgWindowList
{
    private const uint OnScreenOnly = 1;
    private const uint ExcludeDesktopElements = 16;
    private const int NormalLayer = 0;

    [DllImport(Native.CG)] private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(Native.CF)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    /// <summary>On-screen, layer-0 windows with a non-empty title, in front-to-back order.</summary>
    public static IReadOnlyList<WindowEntry> TopWindows()
    {
        var info = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktopElements, 0);
        if (info == IntPtr.Zero) return [];

        var list = new List<WindowEntry>();
        try
        {
            if (Native.CFGetTypeID(info) != Native.CFArrayGetTypeID()) return list;
            var count = Native.CFArrayGetCount(info);
            for (var i = 0; i < count; i++)
            {
                var dict = Native.CFArrayGetValueAtIndex(info, i);
                if (dict == IntPtr.Zero) continue;
                if (ReadInt(dict, "kCGWindowLayer") != NormalLayer) continue;

                var windowId = ReadInt(dict, "kCGWindowNumber");
                var pid = (int)ReadInt(dict, "kCGWindowOwnerPID");
                if (windowId <= 0 || pid <= 0) continue;

                var owner = ReadString(dict, "kCGWindowOwnerName") ?? "";
                var title = ReadString(dict, "kCGWindowName") ?? "";
                list.Add(new WindowEntry(title, pid, owner, windowId));
            }
            return list;
        }
        finally { Native.CFRelease(info); }
    }

    private static IntPtr ValueFor(IntPtr dict, string key)
    {
        var keyRef = Native.CFStringCreateWithCString(IntPtr.Zero, key, Native.EncodingUtf8);
        if (keyRef == IntPtr.Zero) return IntPtr.Zero;
        try { return CFDictionaryGetValue(dict, keyRef); }
        finally { Native.CFRelease(keyRef); }
    }

    private static long ReadInt(IntPtr dict, string key)
    {
        var value = ValueFor(dict, key);
        if (value == IntPtr.Zero) return -1;
        if (Native.CFGetTypeID(value) != Native.CFNumberGetTypeID()) return -1;
        Native.CFNumberGetValue(value, 4 /* kCFNumberSInt32Type */, out var number);
        return (long)number;
    }

    private static string? ReadString(IntPtr dict, string key)
    {
        var value = ValueFor(dict, key);
        if (value == IntPtr.Zero) return null;
        return CfRef.ReadString(value);
    }
}

/// <summary>One on-screen window as CoreGraphics sees it.</summary>
internal sealed record WindowEntry(string Title, int Pid, string OwnerName, long WindowId);
