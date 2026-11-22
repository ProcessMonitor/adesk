using System.Windows.Automation;
using AdeskPro.Platform;
using AdeskPro.Ui;

namespace AdeskPro.Windows;

/// <summary>The Windows IPlatform: UI Automation for observation, Win32 for synthesis and window geometry.</summary>
public sealed class WindowsPlatform : IPlatform
{
    public string PlatformName => $"windows {Environment.OSVersion.Version}";

    public IReadOnlyList<WindowInfo> ListTopWindows() => UiASource.TopWindows();

    public WindowInfo FindWindow(string? app, int? pid, long? hwnd) => UiASource.FindWindow(app, pid, hwnd).Info;

    public Walker.ISource SourceForWindow(WindowInfo window)
    {
        var el = ElementFor(window);
        return UiASource.ForElement(el);
    }

    public LiveResolve Resolve(RefEntry entry) => UiASource.ResolveRef(entry);

    public Walker.ISource SourceForResolved(LiveResolve resolved)
    {
        var raw = resolved.RootHandle as AutomationElement
            ?? throw new InvalidOperationException("resolved handle is not an automation element");
        return UiASource.ForElement(raw);
    }

    private static AutomationElement ElementFor(WindowInfo info)
    {
        if (info.Hwnd > 0)
        {
            try { return AutomationElement.FromHandle(new IntPtr(info.Hwnd)); }
            catch (ElementNotAvailableException) { }
        }
        return UiASource.FindWindow(info.Name, info.Pid, null).El;
    }

    public void FocusWindow(WindowInfo window) => ElementFor(window).SetFocus();

    public string? ClipboardGet() => Actions.ClipboardGet();
    public void ClipboardSet(string text) => Actions.ClipboardSet(text);

    public (int Pid, long Hwnd, string Window) Launch(string exe, string args) => WindowOps.Launch(exe, args);

    public int CloseApp(string? app, int? pid, long? hwnd, bool force) =>
        WindowOps.CloseApp(app, pid, hwnd, force);

    public int WindowState(string? app, int? pid, long? hwnd, string state)
    {
        var visual = state switch
        {
            "minimize" => WindowVisualState.Minimized,
            "maximize" => WindowVisualState.Maximized,
            _ => WindowVisualState.Normal,
        };
        return WindowOps.SetWindowState(app, pid, hwnd, visual);
    }

    public void MoveResize(long hwnd, int? x, int? y, int? width, int? height) =>
        WindowOps.MoveResize(hwnd, x, y, width, height);

    public byte[] CapturePng(long hwnd) => Screenshot.CapturePng(hwnd);

    public void PressKeys(string keys, string? focusApp, int? focusPid)
    {
        if (!string.IsNullOrWhiteSpace(focusApp) || focusPid is { } p && p > 0)
        {
            var windows = WindowOps.MatchWindows(focusApp, focusPid, null);
            var first = windows.FirstOrDefault()
                ?? throw new AppNotFoundException("no window matches the filter");
            try { AutomationElement.FromHandle(new IntPtr(first.Hwnd)).SetFocus(); }
            catch (ElementNotAvailableException) { }
        }
        Actions.PressKeys(keys);
    }

    public void MouseClick(int x, int y, bool right, bool doubleClick) => Actions.MouseClick(x, y, right, doubleClick);
    public void Hover(int x, int y) => Actions.Hover(x, y);
    public void Drag(int fromX, int fromY, int toX, int toY) => Actions.Drag(fromX, fromY, toX, toY);
}

/// <summary>One live UIA element behind the semantic ILiveElement surface.</summary>
public sealed class WinLiveElement : ILiveElement
{
    private readonly AutomationElement _el;

    public WinLiveElement(AutomationElement el) { _el = el; }

    public string Role
    {
        get
        {
            try { return string.IsNullOrEmpty(_el.Current.LocalizedControlType) ? _el.Current.ControlType.ProgrammaticName.TrimStart('.').Replace("ControlType.", "") : _el.Current.LocalizedControlType; }
            catch (ElementNotAvailableException) { return "unavailable"; }
        }
    }

    public string Name
    {
        get { try { return _el.Current.Name ?? ""; } catch (ElementNotAvailableException) { return ""; } }
    }

    public string AutomationId
    {
        get { try { return _el.Current.AutomationId ?? ""; } catch (ElementNotAvailableException) { return ""; } }
    }

    public bool Enabled
    {
        get { try { return _el.Current.IsEnabled; } catch (ElementNotAvailableException) { return false; } }
    }

    public bool Offscreen
    {
        get { try { return _el.Current.IsOffscreen; } catch (ElementNotAvailableException) { return true; } }
    }

    public (int X, int Y, int W, int H) Rect
    {
        get
        {
            try
            {
                var r = _el.Current.BoundingRectangle;
                return ((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            }
            catch (ElementNotAvailableException) { return (0, 0, 0, 0); }
        }
    }

    public IReadOnlyList<string> Patterns
    {
        get { try { return UiASource.PatternsOf(_el); } catch (ElementNotAvailableException) { return []; } }
    }

    public string? CurrentValue()
    {
        try
        {
            return _el.TryGetCurrentPattern(ValuePattern.Pattern, out var v)
                ? ((ValuePattern)v).Current.Value
                : null;
        }
        catch (ElementNotAvailableException) { return null; }
    }

    public (string How, string Value) TypeText(string text) => Actions.TypeText(_el, text);
    public string SetValue(string text) => Actions.SetValue(_el, text);
    public string ClickDefault() => Actions.Click(_el);
    public string Toggle() => Actions.Toggle(_el);
    public string ExpandCollapse(bool collapse) => Actions.Expand(_el, collapse);
    public string SelectItem() => Actions.Select(_el);
    public string ScrollDy(int dy) => Actions.ScrollDy(_el, dy);
    public void FocusNow() => Actions.FocusEl(_el);
}
