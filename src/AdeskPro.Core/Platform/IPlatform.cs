using System.Text.Json.Nodes;
using AdeskPro.Ui;

namespace AdeskPro.Platform;

/// <summary>One live element, expressed only through what every platform can do. Handlers never see native types.</summary>
public interface ILiveElement
{
    string Role { get; }
    string Name { get; }
    string AutomationId { get; }
    bool Enabled { get; }
    bool Offscreen { get; }
    (int X, int Y, int W, int H) Rect { get; }
    IReadOnlyList<string> Patterns { get; }

    string? CurrentValue();

    /// <summary>Enters text: value pattern when available, otherwise focus plus keyboard synthesis.</summary>
    (string How, string Value) TypeText(string text);
    string SetValue(string text);
    string ClickDefault();
    string Toggle();
    string ExpandCollapse(bool collapse);
    string SelectItem();
    string ScrollDy(int dy);
    void FocusNow();
}

/// <summary>The platform adapter. One implementation per OS; the binary picks one at startup.</summary>
public interface IPlatform
{
    /// <summary>Human-readable platform string for the status envelope, e.g. "windows 10.0.19045".</summary>
    string PlatformName { get; }

    IReadOnlyList<WindowInfo> ListTopWindows();

    /// <summary>Resolves the target window. Throws AppNotFoundException / AmbiguousWindowException.</summary>
    WindowInfo FindWindow(string? app, int? pid, long? hwnd);

    /// <summary>Brings a window to the foreground.</summary>
    void FocusWindow(WindowInfo window);

    /// <summary>Tree source rooted at a window.</summary>
    Walker.ISource SourceForWindow(WindowInfo window);

    /// <summary>Re-resolves a saved ref to a live element, with per-platform pinning and diagnostics.</summary>
    LiveResolve Resolve(RefEntry entry);

    /// <summary>Tree source rooted at the element a previous Resolve returned.</summary>
    Walker.ISource SourceForResolved(LiveResolve resolved);

    string? ClipboardGet();
    void ClipboardSet(string text);

    (int Pid, long Hwnd, string Window) Launch(string exe, string args);
    int CloseApp(string? app, int? pid, long? hwnd, bool force);
    int WindowState(string? app, int? pid, long? hwnd, string state);
    void MoveResize(long hwnd, int? x, int? y, int? width, int? height);

    byte[] CapturePng(long hwnd);

    /// <summary>Sends a key combo; when focusApp/focusPid is given, focuses that window first.</summary>
    void PressKeys(string keys, string? focusApp, int? focusPid);

    void MouseClick(int x, int y, bool right, bool doubleClick);
    void Hover(int x, int y);
    void Drag(int fromX, int fromY, int toX, int toY);
}

public sealed class LiveResolve
{
    public ILiveElement? Element { get; init; }
    /// <summary>Native handle the platform can walk from (Windows: AutomationElement; macOS: AXUIElement).</summary>
    public object? RootHandle { get; init; }
    public string How { get; init; } = "";
    public JsonObject Diagnostic { get; init; } = new();
}
