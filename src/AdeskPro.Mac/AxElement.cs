using AdeskPro.Platform;
using AdeskPro.Ui;

namespace AdeskPro.Mac;

/// <summary>
/// One AX element. Owns the CF reference it was created from, so callers use it like a handle
/// and dispose it when the enclosing command finishes (<see cref="MacPlatform.ReleaseCommandResources"/>).
/// </summary>
internal sealed class AxElement : IDisposable
{
    private IntPtr _handle;
    public IntPtr Handle => _handle;

    internal AxElement(IntPtr handle) { _handle = handle; }

    public static AxElement? CreateForPid(int pid)
    {
        var handle = Native.AXUIElementCreateApplication(pid);
        if (handle == IntPtr.Zero) return null;
        var element = new AxElement(handle);
        element.SetTimeout(2f);
        return element;
    }

    /// <summary>Per-element messaging timeout: the AX equivalent of the COM-level timeout the
    /// Windows adapter lacks. A hung app then fails the call instead of stalling the agent.</summary>
    public void SetTimeout(float seconds) => Native.AXUIElementSetMessagingTimeout(_handle, seconds);

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Native.CFRelease(_handle);
        _handle = IntPtr.Zero;
    }

    // --- attribute reads ---

    /// <summary>Raw attribute handle, or IntPtr.Zero. The caller owns releasing it.</summary>
    public IntPtr CopyAttribute(CfString attribute)
    {
        if (_handle == IntPtr.Zero) return IntPtr.Zero;
        var status = Native.AXUIElementCopyAttributeValue(_handle, attribute.Handle, out var value);
        return status == Native.AxErrorSuccess ? value : IntPtr.Zero;
    }

    public string? GetString(CfString attribute)
    {
        var raw = CopyAttribute(attribute);
        if (raw == IntPtr.Zero) return null;
        try { return CfRef.ReadString(raw); }
        finally { Native.CFRelease(raw); }
    }

    public bool? GetBool(CfString attribute)
    {
        var raw = CopyAttribute(attribute);
        if (raw == IntPtr.Zero) return null;
        try
        {
            if (Native.CFGetTypeID(raw) != Native.CFBooleanGetTypeID()) return null;
            return Native.CFBooleanGetValue(raw);
        }
        finally { Native.CFRelease(raw); }
    }

    public double? GetNumber(CfString attribute)
    {
        var raw = CopyAttribute(attribute);
        if (raw == IntPtr.Zero) return null;
        try
        {
            if (Native.CFGetTypeID(raw) != Native.CFNumberGetTypeID()) return null;
            Native.CFNumberGetValue(raw, 4 /* kCFNumberSInt32Type */, out var value);
            return value;
        }
        finally { Native.CFRelease(raw); }
    }

    /// <summary>Child elements, as adopted handles the caller must dispose.</summary>
    public List<AxElement> GetChildren()
    {
        var raw = CopyAttribute(AxNames.Children);
        if (raw == IntPtr.Zero) return [];
        var list = new List<AxElement>();
        try
        {
            if (Native.CFGetTypeID(raw) != Native.CFArrayGetTypeID()) return list;
            var count = Native.CFArrayGetCount(raw);
            for (var i = 0; i < count; i++)
            {
                var child = Native.CFArrayGetValueAtIndex(raw, i);
                if (child == IntPtr.Zero) continue;
                Native.CFRetain(child);          // the array owns it; we keep our own reference
                var element = new AxElement(child);
                element.SetTimeout(2f);
                list.Add(element);
            }
            return list;
        }
        finally { Native.CFRelease(raw); }
    }

    public List<string> GetActionNames()
    {
        var status = Native.AXUIElementCopyActionNames(_handle, out var names);
        if (status != Native.AxErrorSuccess || names == IntPtr.Zero) return [];
        var list = new List<string>();
        try
        {
            if (Native.CFGetTypeID(names) != Native.CFArrayGetTypeID()) return list;
            var count = Native.CFArrayGetCount(names);
            for (var i = 0; i < count; i++)
            {
                var name = CfRef.ReadString(Native.CFArrayGetValueAtIndex(names, i));
                if (name is not null) list.Add(name);
            }
            return list;
        }
        finally { Native.CFRelease(names); }
    }

    public bool IsAttributeSettable(CfString attribute)
    {
        var status = Native.AXUIElementIsAttributeSettable(_handle, attribute.Handle, out var settable);
        return status == Native.AxErrorSuccess && settable;
    }

    // --- attribute / action writes ---

    public bool SetCString(CfString attribute, string text)
    {
        var value = Native.CFStringCreateWithCString(IntPtr.Zero, text, Native.EncodingUtf8);
        if (value == IntPtr.Zero) return false;
        try { return Native.AXUIElementSetAttributeValue(_handle, attribute.Handle, value) == Native.AxErrorSuccess; }
        finally { Native.CFRelease(value); }
    }

    public bool Perform(CfString action)
    {
        var status = Native.AXUIElementPerformAction(_handle, action.Handle);
        if (status == Native.AxErrorAPIDisabled)
            throw new NoPatternException("accessibility is not enabled for this process",
                "grant adesk-pro Accessibility permission in System Settings > Privacy & Security > Accessibility");
        return status == Native.AxErrorSuccess;
    }

    // --- geometry ---

    public (int X, int Y, int W, int H) Rect
    {
        get
        {
            var x = 0; var y = 0; var w = 0; var h = 0;
            var positionRaw = CopyAttribute(AxNames.Position);
            if (positionRaw != IntPtr.Zero)
            {
                try
                {
                    if (Native.AXValueGetPoint(positionRaw, Native.AxValueCgPointType, out var point))
                    { x = (int)point.X; y = (int)point.Y; }
                }
                finally { Native.CFRelease(positionRaw); }
            }
            var sizeRaw = CopyAttribute(AxNames.Size);
            if (sizeRaw != IntPtr.Zero)
            {
                try
                {
                    if (Native.AXValueGetSize(sizeRaw, Native.AxValueCgSizeType, out var size))
                    { w = (int)size.Width; h = (int)size.Height; }
                }
                finally { Native.CFRelease(sizeRaw); }
            }
            return (x, y, w, h);
        }
    }

    public int Pid
    {
        get
        {
            Native.AXUIElementGetPid(_handle, out var pid);
            return pid;
        }
    }
}
