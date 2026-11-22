using AdeskPro.Platform;
using AdeskPro.Ui;

namespace AdeskPro.Tests;

/// <summary>Minimal IPlatform for tests that never leave the core: observation throws, actions are no-ops.</summary>
public sealed class FakePlatform : IPlatform
{
    public string PlatformName => "fake";

    public IReadOnlyList<WindowInfo> ListTopWindows() => [];

    public WindowInfo FindWindow(string? app, int? pid, long? hwnd) =>
        throw new AppNotFoundException("no windows exist in the fake platform");

    public Walker.ISource SourceForWindow(WindowInfo window) =>
        throw new AppNotFoundException("no windows exist in the fake platform");

    public LiveResolve Resolve(RefEntry entry) => new() { Diagnostic = new System.Text.Json.Nodes.JsonObject() };

    public Walker.ISource SourceForResolved(LiveResolve resolved) =>
        throw new AppNotFoundException("no windows exist in the fake platform");

    public void FocusWindow(WindowInfo window) => throw new AppNotFoundException("no windows exist in the fake platform");

    public string? ClipboardGet() => "";
    public void ClipboardSet(string text) { }

    public (int Pid, long Hwnd, string Window) Launch(string exe, string args) => (0, 0, "");
    public int CloseApp(string? app, int? pid, long? hwnd, bool force) => 0;
    public int WindowState(string? app, int? pid, long? hwnd, string state) => 0;
    public void MoveResize(long hwnd, int? x, int? y, int? width, int? height) { }
    public byte[] CapturePng(long hwnd) => [];
    public void PressKeys(string keys, string? focusApp, int? focusPid) { }
    public void MouseClick(int x, int y, bool right, bool doubleClick) { }
    public void Hover(int x, int y) { }
    public void Drag(int fromX, int fromY, int toX, int toY) { }
}
