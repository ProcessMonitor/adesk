using System.Diagnostics;
using System.Runtime.InteropServices;
using AdeskPro.Platform;
using AdeskPro.Ui;

namespace AdeskPro.Mac;

/// <summary>
/// The macOS IPlatform. Observation goes through the Accessibility (AX) API; synthesis goes
/// through CoreGraphics; window geometry through AX; screenshots through <c>screencapture</c>.
/// </summary>
public sealed class MacPlatform : IPlatform
{
    /// <summary>Elements resolved during one command, released when the command ends.</summary>
    private readonly List<IDisposable> _perCommand = new();

    public string PlatformName => "macos " + Environment.OSVersion.VersionString;

    public IReadOnlyList<WindowInfo> ListTopWindows() => MacWindows.TopWindows();

    public WindowInfo FindWindow(string? app, int? pid, long? hwnd) =>
        MacWindows.FindWindow(app, pid, hwnd).Info;

    public Walker.ISource SourceForWindow(WindowInfo window)
    {
        // Match on the CGWindowID (Hwnd here): titles change with the document name.
        var (_, element) = MacWindows.FindWindow(null, window.Pid, window.Hwnd);
        _perCommand.Add(element);
        return new AxSource(element);
    }

    public void FocusWindow(WindowInfo window)
    {
        var (_, element) = MacWindows.FindWindow(null, window.Pid, window.Hwnd);
        try
        {
            element.Perform(CfStringRaise);
            // Fall back to activating the app when AXRaise is unavailable (some panel windows).
            try
            {
                var activate = Process.GetProcessById(window.Pid);
                _ = activate;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
            element.Dispose();
        }
        catch (NoPatternException) { element.Dispose(); throw; }
    }

    // --- ref re-resolution ---

    public LiveResolve Resolve(RefEntry entry) => ResolveRef(entry);

    internal LiveResolve ResolveRef(RefEntry entry)
    {
        var diagnostic = new System.Text.Json.Nodes.JsonObject
        {
            ["savedPid"] = entry.Pid,
            ["savedWindowName"] = entry.WindowName,
            ["savedRole"] = entry.Role,
            ["savedRoleId"] = entry.RoleId,
            ["scopePathLength"] = entry.ScopePath.Count,
        };

        AxElement? app;
        try { app = AxElement.CreateForPid(entry.Pid); }
        catch (Exception ex) { return Missing(diagnostic, "pid_unavailable", ex.Message); }
        if (app is null) return Missing(diagnostic, "pid_unavailable", "no AX application for pid");

        try
        {
            // Re-find the window the snapshot walked (titles change with document names, so
            // fall back to the pid's first window when the title no longer matches).
            var windows = MacWindows.WindowElements(app);
            diagnostic["windowCount"] = windows.Count;
            if (windows.Count == 0)
                return Missing(diagnostic, "window_absent", "the app has no windows left");

            // Pick by saved title, falling back to the first window. Nothing is disposed until the
            // choice is final: disposing a candidate and then selecting it would hand out a dead handle.
            var chosen = 0;
            for (var i = 0; i < windows.Count; i++)
            {
                var title = windows[i].GetString(AxNames.Title) ?? "";
                if (entry.WindowName.Length > 0
                    && title.Contains(entry.WindowName, StringComparison.OrdinalIgnoreCase))
                {
                    chosen = i;
                    break;
                }
            }
            diagnostic["chosenWindowTitle"] = windows[chosen].GetString(AxNames.Title) ?? "";
            for (var i = 0; i < windows.Count; i++)
                if (i != chosen) windows[i].Dispose();
            var target = windows[chosen];
            _perCommand.Add(target);

            var byPath = DescendPath(target, entry.ScopePath, diagnostic);
            if (byPath is not null)
            {
                var matchesPath = MatchesIdentity(byPath, entry, requireName: false);
                diagnostic["scopePathIdentityMatch"] = matchesPath;
                if (matchesPath)
                    return Adopt(byPath, "scope-path", diagnostic);
                byPath.Dispose();
            }

            var identityMatches = ScanByIdentity(target, entry, diagnostic);
            if (identityMatches.Count == 1)
                return Adopt(identityMatches[0], "identity", diagnostic);
            foreach (var match in identityMatches.Skip(identityMatches.Count == 1 ? 0 : 1)) match.Dispose();
            if (identityMatches.Count > 1)
                throw new AmbiguousTargetException(
                    $"{identityMatches.Count} live AX elements match the saved identity");
            return Missing(diagnostic, "no_match", "nothing in the window matches the saved identity");
        }
        finally { app.Dispose(); }
    }

    private static LiveResolve Missing(System.Text.Json.Nodes.JsonObject diagnostic, string kind, string message)
    {
        diagnostic["kind"] = kind;
        diagnostic["message"] = message;
        return new LiveResolve { Diagnostic = diagnostic };
    }

    private LiveResolve Adopt(AxElement element, string how, System.Text.Json.Nodes.JsonObject diagnostic)
    {
        _perCommand.Add(element);
        var live = new MacLiveElement(element);
        return new LiveResolve { Element = live, RootHandle = element, How = how, Diagnostic = diagnostic };
    }

    /// <summary>
    /// Identity comparison for a live element against a saved entry.
    /// <paramref name="requireName"/> is false for scope-path descent: walking the exact saved
    /// sibling path already proves position, and the name of a text field is its volatile content.
    /// </summary>
    private static bool MatchesIdentity(AxElement element, RefEntry entry, bool requireName = true)
    {
        if (element.Pid != entry.Pid) return false;
        if (!string.Equals(element.GetString(AxNames.Identifier) ?? "", entry.AutomationId, StringComparison.Ordinal))
            return false;

        var roleId = element.GetString(AxNames.Role) ?? "";
        var roleMatches = entry.RoleId.Length > 0
            ? string.Equals(roleId, entry.RoleId, StringComparison.Ordinal)
            : string.Equals(roleId, entry.Role, StringComparison.Ordinal);
        if (!roleMatches) return false;

        if (!requireName || entry.Name.Length == 0) return true;

        // Same name derivation as the snapshot, or text fields (empty AXTitle, content in
        // AXValue) can never be re-identified.
        var liveName = AxSource.DisplayName(
            roleId,
            element.GetString(AxNames.Title) ?? "",
            element.GetString(AxNames.Description) ?? "",
            element.GetString(AxNames.Value) ?? "");
        // A widget whose name is its value (text fields, edit areas) changes name on every edit.
        // Treat the name as part of identity only for elements whose name is a label.
        var nameIsValue = roleId is "AXTextField" or "AXTextArea" or "AXComboBox";
        return nameIsValue || string.Equals(liveName, entry.Name, StringComparison.Ordinal);
    }

    private static AxElement? DescendPath(AxElement window, List<int> scopePath, System.Text.Json.Nodes.JsonObject diagnostic)
    {
        if (scopePath.Count == 0) { Native.CFRetain(window.Handle); return new AxElement(window.Handle); }
        var children = AxChildrenOf(window);
        AxElement? current = null;
        var level = 0;
        foreach (var index in scopePath)
        {
            if (index < 0 || index >= children.Count)
            {
                diagnostic["pathBrokeAtLevel"] = level;
                FreeAll(children);
                current?.Dispose();
                return null;
            }
            current?.Dispose();
            current = children[index];
            for (var i = 0; i < children.Count; i++)
                if (i != index) children[i].Dispose();
            children = AxChildrenOf(current);
            level++;
        }
        FreeAll(children);
        return current;
    }

    private static List<AxElement> AxChildrenOf(AxElement parent) => parent.GetChildren();

    private static void FreeAll(List<AxElement> elements)
    {
        foreach (var element in elements) element.Dispose();
    }

    private static List<AxElement> ScanByIdentity(AxElement window, RefEntry entry, System.Text.Json.Nodes.JsonObject diagnostic)
    {
        var matches = new List<AxElement>();
        var scanned = 0;
        void Scan(AxElement node, int depth)
        {
            if (depth > 40) return;
            foreach (var child in node.GetChildren())
            {
                scanned++;
                if (MatchesIdentity(child, entry)) matches.Add(child);
                else Scan(child, depth + 1);
            }
        }
        Scan(window, 0);
        diagnostic["scannedNodes"] = scanned;
        diagnostic["identityMatchCount"] = matches.Count;
        return matches;
    }

    public Walker.ISource SourceForResolved(LiveResolve resolved)
    {
        var root = resolved.RootHandle as AxElement
            ?? throw new InvalidOperationException("resolved handle is not an AX element");
        _perCommand.Add(root);
        return new AxSource(root);
    }

    // --- clipboard ---

    public string? ClipboardGet()
    {
        var output = RunOsa("the clipboard as text");
        return output;
    }

    public void ClipboardSet(string text)
    {
        var quoted = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        RunOsa($"set the clipboard to \"{quoted}\"");
    }

    /// <summary>osascript is the one pragmatic dependency for the pasteboard: it is present
    /// on every macOS install and avoids dragging AppKit bindings into the build.</summary>
    private static string? RunOsa(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("failed to start osascript");
        var output = process.StandardOutput.ReadToEnd().TrimEnd('\n');
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        if (process.ExitCode != 0 && output.Length == 0)
            throw new InvalidOperationException("osascript failed: " + error.Trim());
        return output;
    }

    // --- app lifecycle and geometry ---

    public (int Pid, long Hwnd, string Window) Launch(string exe, string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = true,
        };
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"failed to start '{exe}'");
        for (var waited = 0; waited < 4000; waited += 250)
        {
            Thread.Sleep(250);
            var windows = MacWindows.TopWindows().Where(w => w.Pid == process.Id).ToList();
            if (windows.Count > 0)
            {
                var first = windows[0];
                return (process.Id, first.Hwnd, first.Name);
            }
        }
        return (process.Id, 0, "");
    }

    public int CloseApp(string? app, int? pid, long? hwnd, bool force)
    {
        var windows = MacWindowSelection.Select(app, pid, hwnd);
        if (windows.Count == 0) throw new AppNotFoundException("no window matches the filter");
        var closed = 0;
        foreach (var window in windows)
        {
            if (force)
            {
                try { Process.GetProcessById(window.Pid).Kill(); closed++; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
                continue;
            }
            var (_, element) = MacWindows.FindWindow(null, window.Pid, window.Hwnd);
            try
            {
                if (element.Perform(CfStringClose)) closed++;
            }
            finally { element.Dispose(); }
        }
        if (closed == 0 && !force)
            throw new InvalidOperationException("window exposes no close action; rerun with --force to kill the process");
        return closed;
    }

    public int WindowState(string? app, int? pid, long? hwnd, string state)
    {
        // AX has no minimise/maximise verb; the menu bar owns those. Buttons in the window's
        // title bar are addressable, so drive them by subrole.
        var windows = MacWindowSelection.Select(app, pid, hwnd);
        if (windows.Count == 0) throw new AppNotFoundException("no window matches the filter");
        var changed = 0;
        foreach (var window in windows)
        {
            var (_, element) = MacWindows.FindWindow(null, window.Pid, window.Hwnd);
            try
            {
                var subrole = state switch
                {
                    "minimize" => "AXMinimizeButton",
                    "maximize" or "restore" => "AXZoomButton",
                    _ => "",
                };
                foreach (var child in element.GetChildren())
                {
                    try
                    {
                        if (child.GetString(AxNames.Subrole) == subrole && child.Perform(AxNames.Press)) changed++;
                    }
                    finally { child.Dispose(); }
                }
            }
            finally { element.Dispose(); }
        }
        return changed;
    }

    public void MoveResize(long hwnd, int? x, int? y, int? width, int? height)
    {
        var (_, element) = MacWindows.FindWindow(null, null, hwnd);
        try
        {
            var (cx, cy, cw, ch) = element.Rect;
            var position = new Native.CgPoint { X = x ?? cx, Y = y ?? cy };
            var size = new Native.CgSize { Width = width ?? cw, Height = height ?? ch };
            if (x is not null || y is not null)
            {
                var value = Native.AXValueCreate(Native.AxValueCgPointType, ref position);
                try { Native.AXUIElementSetAttributeValue(element.Handle, AxNames.Position.Handle, value); }
                finally { Native.CFRelease(value); }
            }
            if (width is not null || height is not null)
            {
                var value = Native.AXValueCreate(Native.AxValueCgSizeType, ref size);
                try { Native.AXUIElementSetAttributeValue(element.Handle, AxNames.Size.Handle, value); }
                finally { Native.CFRelease(value); }
            }
        }
        finally { element.Dispose(); }
    }

    public byte[] CapturePng(long hwnd) => MacScreenshot.CapturePng(hwnd);

    public void PressKeys(string keys, string? focusApp, int? focusPid)
    {
        if (!string.IsNullOrWhiteSpace(focusApp) || focusPid is { } p && p > 0)
        {
            var (info, element) = MacWindows.FindWindow(focusApp, focusPid, null);
            try { element.Perform(CfStringRaise); }
            finally { element.Dispose(); }
            _ = info;
        }
        MacInput.PressKeys(keys);
    }

    public void MouseClick(int x, int y, bool right, bool doubleClick) => MacInput.Click(x, y, right, doubleClick);
    public void Hover(int x, int y) => MacInput.Hover(x, y);
    public void Drag(int fromX, int fromY, int toX, int toY) => MacInput.Drag(fromX, fromY, toX, toY);

    // --- diagnostics ---

    /// <summary>The macOS equivalent of "why can't the agent see anything?": accessibility and
    /// screen-recording permissions, target reachability, visible window count.</summary>
    public IReadOnlyList<(string Key, string Value)> Diagnostics()
    {
        var list = new List<(string, string)>();
        var trusted = Native.AXIsProcessTrusted();
        list.Add(("accessibilityPermission", trusted ? "granted" : "DENIED"));
        if (!trusted)
            list.Add(("warning",
                "Accessibility permission is required: System Settings > Privacy & Security > Accessibility — " +
                "add your terminal (or adesk-pro) and restart it"));

        list.Add(("screenRecordingPermission",
            Native.CGPreflightScreenCaptureAccess() ? "granted" : "denied (only needed for screenshot)"));

        try
        {
            var windows = MacWindows.TopWindows();
            list.Add(("visibleWindows", windows.Count.ToString()));
            if (windows.Count == 0)
                list.Add(("warning", "no windows visible — grant Accessibility permission or launch the target app"));
        }
        catch (Exception ex)
        {
            list.Add(("error", ex.GetType().Name + ": " + ex.Message));
        }
        return list;
    }

    /// <summary>Fires the OS Accessibility prompt, once, for a command that needs the tree.
    /// The result is not thrown away silently: a denied grant surfaces as an empty tree plus the
    /// diagnostic from <see cref="Diagnostics"/>.</summary>
    public void RequestAccessibility() => EnsureAccessibility(prompt: true);

    /// <summary>
    /// Checks Accessibility permission and, when <paramref name="prompt"/> is set, asks the OS to
    /// show its grant dialog. Never throws: a permission probe must not take the process down.
    /// </summary>
    public static bool EnsureAccessibility(bool prompt)
    {
        try
        {
            if (Native.AXIsProcessTrusted()) return true;
            if (!prompt) return false;

            var keys = new[] { AxNames.TrustedCheckPrompt.Handle };
            var values = new[] { Native.CFBooleanTrue };
            var options = Native.CFDictionaryCreate(IntPtr.Zero, keys, values, 1,
                Native.TypeDictionaryKeyCallbacks, Native.TypeDictionaryValueCallbacks);
            if (options == IntPtr.Zero) return false;
            try { return Native.AXIsProcessTrustedWithOptions(options); }
            finally { Native.CFRelease(options); }
        }
        catch (Exception)
        {
            // Missing symbol, denied entitlement, sandbox: report "not trusted" rather than crash.
            return false;
        }
    }

    public void ReleaseCommandResources()
    {
        foreach (var disposable in _perCommand) disposable.Dispose();
        _perCommand.Clear();
    }

    private static readonly CfString CfStringRaise = CfString.Get("AXRaise");
    private static readonly CfString CfStringClose = CfString.Get("AXClose");
}

internal static class MacWindowSelection
{
    /// <summary>Shared filter for the app/window lifecycle commands.</summary>
    internal static List<WindowInfo> Select(string? app, int? pid, long? hwnd)
    {
        IEnumerable<WindowInfo> query = MacWindows.TopWindows();
        if (pid is { } p && p > 0) query = query.Where(w => w.Pid == p);
        if (hwnd is { } h && h > 0) query = query.Where(w => w.Hwnd == h);
        if (!string.IsNullOrWhiteSpace(app))
            query = query.Where(w => w.Name.Contains(app, StringComparison.OrdinalIgnoreCase)
                || w.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase));
        return query.ToList();
    }
}
