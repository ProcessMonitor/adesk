using System.Runtime.InteropServices;

namespace AdeskPro.Mac;

/// <summary>
/// Thin, hand-written bridge to CoreFoundation + ApplicationServices. Everything here is
/// P/Invoke into frameworks that ship with macOS, so no extra SDK/workload is needed to build.
/// All CF objects are reference counted: anything returned by a *Create*/*Copy* function must be
/// released by the caller, which is what <see cref="CfRef"/> exists for.
/// </summary>
internal static class Native
{
    internal const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    internal const string AX = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    internal const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    internal const uint EncodingUtf8 = 0x08000100;

    // --- CoreFoundation ---

    [DllImport(CF)] internal static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);
    [DllImport(CF)] internal static extern long CFStringGetLength(IntPtr theString);
    [DllImport(CF)] internal static extern long CFStringGetMaximumSizeForEncoding(long length, uint encoding);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CFStringGetCString(IntPtr theString, IntPtr buffer, long bufferSize, uint encoding);
    [DllImport(CF)] internal static extern void CFRelease(IntPtr cf);
    [DllImport(CF)] internal static extern IntPtr CFRetain(IntPtr cf);
    [DllImport(CF)] internal static extern long CFArrayGetCount(IntPtr array);
    [DllImport(CF)] internal static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);
    [DllImport(CF)] internal static extern IntPtr CFGetTypeID(IntPtr cf);
    [DllImport(CF)] internal static extern IntPtr CFStringGetTypeID();
    [DllImport(CF)] internal static extern IntPtr CFBooleanGetTypeID();
    [DllImport(CF)] internal static extern IntPtr CFNumberGetTypeID();
    [DllImport(CF)] internal static extern IntPtr CFArrayGetTypeID();
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CFBooleanGetValue(IntPtr boolean);
    [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CFNumberGetValue(IntPtr number, int theType, out long value);
    [DllImport(CF)] internal static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, long count,
        IntPtr keyCallBacks, IntPtr valueCallBacks);
    [DllImport(CF)] internal static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, long capacity,
        IntPtr keyCallBacks, IntPtr valueCallBacks);
    [DllImport(CF)] internal static extern void CFDictionarySetValue(IntPtr dict, IntPtr key, IntPtr value);

    /// <summary><c>kCFBooleanTrue</c> is an exported data symbol, not a function: DllImport cannot
    /// bind it, so it is resolved once and dereferenced to get the CFBoolean object.</summary>
    private static IntPtr _cfBooleanTrue;
    internal static IntPtr CFBooleanTrue
    {
        get
        {
            if (_cfBooleanTrue == IntPtr.Zero) _cfBooleanTrue = ReadDataSymbol("kCFBooleanTrue");
            return _cfBooleanTrue;
        }
    }

    private static IntPtr _typeDictionaryKeyCallbacks;
    private static IntPtr _typeDictionaryValueCallbacks;

    /// <summary>Type-safe CFDictionary callbacks. Passing <see cref="IntPtr.Zero"/> instead makes
    /// CFDictionaryCreate treat the dictionary as pointer-based and crash on string keys.</summary>
    internal static IntPtr TypeDictionaryKeyCallbacks =>
        _typeDictionaryKeyCallbacks != IntPtr.Zero
            ? _typeDictionaryKeyCallbacks
            : _typeDictionaryKeyCallbacks = ReadDataSymbol("kCFTypeDictionaryKeyCallBacks");

    internal static IntPtr TypeDictionaryValueCallbacks =>
        _typeDictionaryValueCallbacks != IntPtr.Zero
            ? _typeDictionaryValueCallbacks
            : _typeDictionaryValueCallbacks = ReadDataSymbol("kCFTypeDictionaryValueCallBacks");

    /// <summary>Resolves the address of an exported CF data symbol (the symbol *is* the struct).</summary>
    private static IntPtr ReadDataSymbol(string name)
    {
        var library = NativeLibrary.Load(CF);
        return NativeLibrary.GetExport(library, name);
    }

    // --- Accessibility (AX) ---

    internal const int AxErrorSuccess = 0;
    internal const int AxErrorFailure = -25200;
    internal const int AxErrorIllegalArgument = -25201;
    internal const int AxErrorInvalidUIElement = -25202;
    internal const int AxErrorCannotComplete = -25204;
    internal const int AxErrorAttributeUnsupported = -25205;
    internal const int AxErrorNotImplemented = -25208;
    internal const int AxErrorAPIDisabled = -25211;
    internal const int AxErrorNoValue = -25212;

    [DllImport(AX)] internal static extern IntPtr AXUIElementCreateApplication(int pid);
    [DllImport(AX)] internal static extern IntPtr AXUIElementCreateSystemWide();
    [DllImport(AX)] internal static extern int AXUIElementGetPid(IntPtr element, out int pid);
    [DllImport(AX)] internal static extern int AXUIElementSetMessagingTimeout(IntPtr element, float seconds);
    [DllImport(AX)] internal static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
    [DllImport(AX)] internal static extern int AXUIElementCopyAttributeNames(IntPtr element, out IntPtr names);
    [DllImport(AX)] internal static extern int AXUIElementCopyActionNames(IntPtr element, out IntPtr names);
    [DllImport(AX)] internal static extern int AXUIElementIsAttributeSettable(IntPtr element, IntPtr attribute,
        [MarshalAs(UnmanagedType.I1)] out bool settable);
    [DllImport(AX)] internal static extern int AXUIElementSetAttributeValue(IntPtr element, IntPtr attribute, IntPtr value);
    [DllImport(AX)] internal static extern int AXUIElementPerformAction(IntPtr element, IntPtr action);
    [DllImport(AX)] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool AXIsProcessTrusted();
    [DllImport(AX)] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool AXIsProcessTrustedWithOptions(IntPtr options);
    [DllImport(AX)] internal static extern IntPtr AXValueCreate(int type, ref CgPoint value);
    [DllImport(AX)] internal static extern IntPtr AXValueCreate(int type, ref CgSize value);
    [DllImport(AX, EntryPoint = "AXValueGetValue")] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool AXValueGetPoint(IntPtr value, int type, out CgPoint point);
    [DllImport(AX, EntryPoint = "AXValueGetValue")] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool AXValueGetSize(IntPtr value, int type, out CgSize size);

    internal const int AxValueCgPointType = 1;
    internal const int AxValueCgSizeType = 2;

    // --- Screen recording permission (macOS 10.15+) ---

    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CGRequestScreenCaptureAccess();

    // --- CoreGraphics events ---

    [DllImport(CG)] internal static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, bool keyDown);
    [DllImport(CG)] internal static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, CgPoint mouseCursorPosition, uint mouseButton);
    [DllImport(CG)] internal static extern void CGEventPost(uint tap, IntPtr ev);
    [DllImport(CG)] internal static extern void CGEventSetFlags(IntPtr ev, ulong flags);
    [DllImport(CG)] internal static extern void CGEventKeyboardSetUnicodeString(IntPtr ev, long length, ushort[] unicodeString);
    [DllImport(CG)] internal static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CG)] internal static extern bool CGWarpMouseCursorPosition(CgPoint newCursorPosition);
    [DllImport(CG)] internal static extern void CGAssociateMouseAndMouseCursorPosition(bool connected);

    internal const uint HidEventTap = 0;

    internal const uint MouseMoved = 5;
    internal const uint LeftMouseDown = 1;
    internal const uint LeftMouseUp = 2;
    internal const uint RightMouseDown = 3;
    internal const uint RightMouseUp = 4;
    internal const uint LeftMouseDragged = 6;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CgPoint { public double X; public double Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CgSize { public double Width; public double Height; }
}

/// <summary>A CoreFoundation object with deterministic release. Disposing twice is safe.</summary>
internal sealed class CfRef : IDisposable
{
    private IntPtr _handle;
    public IntPtr Handle => _handle;
    public bool IsNull => _handle == IntPtr.Zero;

    private CfRef(IntPtr handle) { _handle = handle; }

    public static CfRef Adopt(IntPtr handle) => new(handle);

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Native.CFRelease(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>Reads a CFString as a .NET string; null when the object is not a string.</summary>
    public static string? ReadString(IntPtr cf)
    {
        if (cf == IntPtr.Zero) return null;
        if (Native.CFGetTypeID(cf) != Native.CFStringGetTypeID()) return null;
        var length = Native.CFStringGetLength(cf);
        if (length == 0) return "";
        var max = Native.CFStringGetMaximumSizeForEncoding(length, Native.EncodingUtf8) + 1;
        var buffer = Marshal.AllocHGlobal((int)max);
        try
        {
            if (!Native.CFStringGetCString(cf, buffer, max, Native.EncodingUtf8)) return null;
            return Marshal.PtrToStringUTF8(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}

/// <summary>Interned CFString for attribute/action names. One instance per name, never released:
/// the process is short-lived and the set of names is tiny and bounded.</summary>
internal sealed class CfString
{
    private static readonly Dictionary<string, CfString> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public IntPtr Handle { get; }

    private CfString(IntPtr handle) { Handle = handle; }

    public static CfString Get(string value)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(value, out var existing)) return existing;
            var handle = Native.CFStringCreateWithCString(IntPtr.Zero, value, Native.EncodingUtf8);
            var created = new CfString(handle);
            Cache[value] = created;
            return created;
        }
    }
}

/// <summary>Well-known AX attribute and action names, so call sites read like the Apple headers.</summary>
internal static class AxNames
{
    public static readonly CfString Windows = CfString.Get("AXWindows");
    public static readonly CfString Children = CfString.Get("AXChildren");
    public static readonly CfString Role = CfString.Get("AXRole");
    public static readonly CfString Subrole = CfString.Get("AXSubrole");
    public static readonly CfString RoleDescription = CfString.Get("AXRoleDescription");
    public static readonly CfString Title = CfString.Get("AXTitle");
    public static readonly CfString Description = CfString.Get("AXDescription");
    public static readonly CfString Identifier = CfString.Get("AXIdentifier");
    public static readonly CfString Value = CfString.Get("AXValue");
    public static readonly CfString Position = CfString.Get("AXPosition");
    public static readonly CfString Size = CfString.Get("AXSize");
    public static readonly CfString Enabled = CfString.Get("AXEnabled");
    public static readonly CfString Focused = CfString.Get("AXFocused");
    public static readonly CfString Expanded = CfString.Get("AXExpanded");
    public static readonly CfString Selected = CfString.Get("AXSelected");
    public static readonly CfString MinValue = CfString.Get("AXMinValue");
    public static readonly CfString MaxValue = CfString.Get("AXMaxValue");
    public static readonly CfString Rows = CfString.Get("AXRows");
    public static readonly CfString VisibleChildren = CfString.Get("AXVisibleChildren");
    public static readonly CfString Placeholder = CfString.Get("AXPlaceholderValue");

    public static readonly CfString Press = CfString.Get("AXPress");
    public static readonly CfString ShowMenu = CfString.Get("AXShowMenu");
    public static readonly CfString Increment = CfString.Get("AXIncrement");
    public static readonly CfString Pick = CfString.Get("AXPick");

    public static readonly CfString TrustedCheckPrompt = CfString.Get("AXTrustedCheckOptionPrompt");
}
