using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using AdeskPro.Platform;
using System.Windows.Automation;

namespace AdeskPro.Windows;

/// <summary>Window lifecycle and geometry: launch, close, minimize/maximize/restore, move, resize.</summary>
public static class WindowOps
{
    public static (int Pid, long Hwnd, string Name) Launch(string exe, string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = true,
        };
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"failed to start '{exe}'");
        // wait briefly for a visible top-level window belonging to the new process
        for (var waited = 0; waited < 4000; waited += 250)
        {
            Thread.Sleep(250);
            var window = TopWindows().FirstOrDefault(w => w.Pid == process.Id);
            if (window is not null)
                return (window.Pid, window.Hwnd, window.Name);
        }
        return (process.Id, 0, "");
    }

    public static IReadOnlyList<WindowInfo> TopWindows()
    {
        var list = new List<WindowInfo>();
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(
            TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                if (w.Current.ControlType != ControlType.Window) continue;
                list.Add(UiASource.InfoOf(w));
            }
            catch (ElementNotAvailableException) { }
        }
        return list;
    }

    /// <summary>Closes every top-level window of the matched app via the Window pattern (graceful).</summary>
    public static int CloseApp(string? app, int? pid, long? hwnd, bool force)
    {
        var windows = MatchWindows(app, pid, hwnd);
        if (windows.Count == 0) throw new AppNotFoundException("no window matches the filter");
        var closed = 0;
        foreach (var info in windows)
        {
            try
            {
                var el = AutomationElement.FromHandle(new IntPtr(info.Hwnd));
                if (el.TryGetCurrentPattern(WindowPattern.Pattern, out var window))
                {
                    ((WindowPattern)window).Close();
                    closed++;
                    continue;
                }
            }
            catch (ElementNotAvailableException) { }
            if (force)
            {
                try { Process.GetProcessById(info.Pid).Kill(); closed++; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
            }
        }
        if (closed == 0 && !force)
            throw new InvalidOperationException(
                "windows have no Window pattern; rerun with --force to kill the process");
        return closed;
    }

    public static int SetWindowState(string? app, int? pid, long? hwnd, WindowVisualState state)
    {
        var windows = MatchWindows(app, pid, hwnd);
        if (windows.Count == 0) throw new AppNotFoundException("no window matches the filter");
        var changed = 0;
        foreach (var info in windows)
        {
            try
            {
                var el = AutomationElement.FromHandle(new IntPtr(info.Hwnd));
                if (el.TryGetCurrentPattern(WindowPattern.Pattern, out var window))
                {
                    ((WindowPattern)window).SetWindowVisualState(state);
                    changed++;
                }
            }
            catch (ElementNotAvailableException) { }
        }
        return changed;
    }

    /// <summary>Moves/resizes; null components keep their current value.</summary>
    public static void MoveResize(long hwnd, int? x, int? y, int? width, int? height)
    {
        var needsCurrent = x is null || y is null || width is null || height is null;
        var left = 0; var top = 0; var right = 0; var bottom = 0;
        if (needsCurrent)
        {
            if (!GetWindowRect(new IntPtr(hwnd), out var rect))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed");
            left = rect.Left; top = rect.Top; right = rect.Right; bottom = rect.Bottom;
        }
        var finalX = x ?? left;
        var finalY = y ?? top;
        var finalW = width ?? (right - left);
        var finalH = height ?? (bottom - top);
        const uint swpNoZorder = 0x0004;
        if (!SetWindowPos(new IntPtr(hwnd), IntPtr.Zero, finalX, finalY, finalW, finalH, swpNoZorder))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos failed");
    }

    public static List<WindowInfo> MatchWindows(string? app, int? pid, long? hwnd)
    {
        IEnumerable<WindowInfo> query = TopWindows();
        if (pid is { } p && p > 0) query = query.Where(w => w.Pid == p);
        if (hwnd is { } h && h > 0) query = query.Where(w => w.Hwnd == h);
        if (!string.IsNullOrWhiteSpace(app))
            query = query.Where(w =>
                (w.Name?.Contains(app, StringComparison.OrdinalIgnoreCase) ?? false)
                || w.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase));
        return query.ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
}
