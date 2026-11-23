using AdeskPro.Platform;

namespace AdeskPro.Mac;

/// <summary>
/// Window lookup: listing and matching come from CoreGraphics (<see cref="CgWindowList"/>), so it
/// works without Accessibility permission and carries the CGWindowID screenshots need. The AX
/// application element is fetched only when a caller actually walks or acts on the window.
/// </summary>
internal static class MacWindows
{
    /// <summary>WindowInfo.Hwnd carries the CGWindowID on macOS.</summary>
    public static IReadOnlyList<WindowInfo> TopWindows()
    {
        var list = new List<WindowInfo>();
        foreach (var entry in CgWindowList.TopWindows())
        {
            var processName = MacWindows.ProcessNameOf(entry.Pid);
            // Prefer the process name the agent can match on ("Safari"); fall back to the owner
            // name CG reports, which is the app's display name.
            var name = entry.Title.Length > 0 ? entry.Title : entry.OwnerName;
            list.Add(new WindowInfo(name, entry.Pid, processName.Length > 0 ? processName : entry.OwnerName, entry.WindowId));
        }
        return list;
    }

    public static (WindowInfo Info, AxElement Window) FindWindow(string? app, int? pid, long? hwnd)
    {
        var windows = TopWindows();
        IEnumerable<WindowInfo> query = windows;
        if (pid is { } p && p > 0) query = query.Where(w => w.Pid == p);
        if (hwnd is { } h && h > 0) query = query.Where(w => w.Hwnd == h);
        if (!string.IsNullOrWhiteSpace(app))
            query = query.Where(w =>
                w.Name.Contains(app, StringComparison.OrdinalIgnoreCase)
                || w.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase));

        var matches = query.ToList();
        if (matches.Count == 0)
            throw new AppNotFoundException(
                "No window matches. The app must be running and on screen; if listing is empty, grant " +
                "Accessibility permission in System Settings > Privacy & Security > Accessibility.");
        if (matches.Count > 1)
            throw new AmbiguousWindowException(matches);

        var target = matches[0];
        return (target, AxisElementFor(target.Pid, target.Name, target.Hwnd));
    }

    /// <summary>The AX window element that corresponds to a CG window. AX windows carry no window
    /// id, so the match is by title, falling back to the app's first window.</summary>
    private static AxElement AxisElementFor(int pid, string title, long windowId)
    {
        var app = AxElement.CreateForPid(pid)
            ?? throw new AppNotFoundException(
                $"cannot attach to pid {pid}; grant Accessibility permission to this terminal");
        try
        {
            var windows = WindowElements(app);
            if (windows.Count == 0)
                throw new AppNotFoundException($"pid {pid} exposes no accessibility window");

            foreach (var window in windows)
            {
                var windowTitle = window.GetString(AxNames.Title) ?? "";
                if (windowTitle == title)
                {
                    foreach (var other in windows) if (!ReferenceEquals(other, window)) other.Dispose();
                    return window;
                }
                window.Dispose();
            }
            // No title match (CG and AX titles can differ): use the first window, which is the
            // frontmost one for a single-window app.
            var fallback = WindowElements(app).FirstOrDefault();
            if (fallback is null) throw new AppNotFoundException($"pid {pid} exposes no accessibility window");
            _ = windowId;
            return fallback;
        }
        finally { app.Dispose(); }
    }

    /// <summary>Window elements of one app; the caller disposes each.</summary>
    public static List<AxElement> WindowElements(AxElement app)
    {
        var raw = app.CopyAttribute(AxNames.Windows);
        if (raw == IntPtr.Zero) return [];
        var windows = new List<AxElement>();
        try
        {
            if (Native.CFGetTypeID(raw) != Native.CFArrayGetTypeID()) return windows;
            var count = Native.CFArrayGetCount(raw);
            for (var i = 0; i < count; i++)
            {
                var handle = Native.CFArrayGetValueAtIndex(raw, i);
                if (handle == IntPtr.Zero) continue;
                Native.CFRetain(handle);
                var element = new AxElement(handle);
                element.SetTimeout(2f);
                windows.Add(element);
            }
            return windows;
        }
        finally { Native.CFRelease(raw); }
    }

    public static string ProcessNameOf(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ""; }
    }
}
