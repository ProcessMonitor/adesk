using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace AdeskPro.Ui;

public sealed class AppNotFoundException(string message) : Exception(message);

public sealed record WindowInfo(string Name, int Pid, string ProcessName, long Hwnd);

public sealed class AmbiguousWindowException(IReadOnlyList<WindowInfo> windows)
    : Exception("multiple windows match the filter")
{
    public IReadOnlyList<WindowInfo> Windows { get; } = windows;
}

/// <summary>The real UI Automation source: window lookup, cached-property walks, live resolution.</summary>
public sealed class UiASource : Walker.ISource
{
    private static readonly TreeWalker View = TreeWalker.ControlViewWalker;
    private readonly AutomationElement _root;

    private UiASource(AutomationElement root) { _root = root; }

    private static CacheRequest BuildCache()
    {
        var request = new CacheRequest { TreeScope = TreeScope.Element };
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.AutomationIdProperty);
        request.Add(AutomationElement.ClassNameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.LocalizedControlTypeProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        request.Add(AutomationElement.IsKeyboardFocusableProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.ProcessIdProperty);
        request.Add(AutomationElement.NativeWindowHandleProperty);
        return request;
    }

    private readonly CacheRequest _cache = BuildCache();

    // --- window lookup ---

    public static (AutomationElement Window, WindowInfo Info) FindWindow(string? app, int? pid, long? hwnd)
    {
        if (hwnd is { } h && h > 0)
        {
            var byHandle = AutomationElement.FromHandle(new IntPtr(h));
            return (byHandle, InfoOf(byHandle));
        }
        var windows = AutomationElement.RootElement
            .FindAll(TreeScope.Children, Condition.TrueCondition);
        var candidates = new List<(AutomationElement El, WindowInfo Info)>();
        foreach (AutomationElement w in windows)
        {
            try
            {
                if (w.Current.ControlType != ControlType.Window) continue;
                candidates.Add((w, InfoOf(w)));
            }
            catch (ElementNotAvailableException) { }
        }
        IEnumerable<(AutomationElement El, WindowInfo Info)> query = candidates;
        if (pid is { } p && p > 0) query = query.Where(c => c.Info.Pid == p);
        if (!string.IsNullOrWhiteSpace(app))
        {
            query = query.Where(c =>
                (c.Info.Name?.Contains(app, StringComparison.OrdinalIgnoreCase) ?? false)
                || c.Info.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase));
        }
        var matches = query.ToList();
        return matches.Count switch
        {
            0 => throw new AppNotFoundException("No visible top-level window matches the filter."),
            1 => (matches[0].El, matches[0].Info),
            _ => throw new AmbiguousWindowException(matches.Select(m => m.Info).ToList()),
        };
    }

    public static WindowInfo InfoOf(AutomationElement el)
    {
        int pid = 0;
        long hwnd = 0;
        string name = "";
        try
        {
            name = el.Current.Name ?? "";
            pid = el.Current.ProcessId;
            hwnd = el.Current.NativeWindowHandle;
        }
        catch (ElementNotAvailableException) { }
        return new WindowInfo(name, pid, ProcessNameOf(pid), hwnd);
    }

    public static string ProcessNameOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "";
        }
    }

    public static UiASource ForWindow(AutomationElement window) => new(window);

    // --- Walker.ISource ---

    public UiNodeData Root() => ToData(_root, preferCache: false);

    public IReadOnlyList<UiNodeData> Children(UiNodeData node, WalkBudget budget)
    {
        if (node.Raw is null) return [];
        var list = new List<UiNodeData>();
        try
        {
            var child = View.GetFirstChild(node.Raw, _cache);
            var guard = 0;
            while (child is not null && guard++ < budget.MaxSiblings)
            {
                list.Add(ToData(child, preferCache: true));
                child = View.GetNextSibling(child, _cache);
            }
        }
        catch (ElementNotAvailableException) { }
        return list;
    }

    // --- projection ---

    private UiNodeData ToData(AutomationElement el, bool preferCache)
    {
        var data = (preferCache ? ReadCached(el) : null) ?? ReadCurrent(el);
        data.Raw = el;
        data.Patterns = ReadPatterns(el, data);
        return data;
    }

    private static UiNodeData ReadCached(AutomationElement el)
    {
        try
        {
            var c = el.Cached;
            var node = From(c.Name, c.AutomationId, c.ClassName, c.LocalizedControlType, c.ControlType,
                c.ProcessId, c.IsOffscreen, c.IsKeyboardFocusable, c.BoundingRectangle, c.NativeWindowHandle);
            try { node.RuntimeId = el.GetRuntimeId(); }
            catch (ElementNotAvailableException) { }
            return node;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException)
        {
            return null!; // no active cache on this reference — caller falls back to Current
        }
    }

    private static UiNodeData ReadCurrent(AutomationElement el)
    {
        try
        {
            var c = el.Current;
            var node = From(c.Name, c.AutomationId, c.ClassName, c.LocalizedControlType, c.ControlType,
                c.ProcessId, c.IsOffscreen, c.IsKeyboardFocusable, c.BoundingRectangle, c.NativeWindowHandle);
            try { node.RuntimeId = el.GetRuntimeId(); }
            catch (ElementNotAvailableException) { }
            return node;
        }
        catch (ElementNotAvailableException)
        {
            return Dead();
        }
    }

    private static UiNodeData From(string name, string automationId, string className,
        string localized, ControlType? ct, int pid, bool offscreen, bool focusable,
        System.Windows.Rect rect, long hwnd)
    {
        return new UiNodeData
        {
            Role = string.IsNullOrEmpty(localized) ? ct?.ProgrammaticName ?? "custom" : localized,
            Name = name ?? "",
            AutomationId = automationId ?? "",
            ClassName = className ?? "",
            Pid = pid,
            Hwnd = hwnd,
            IsOffscreen = offscreen,
            IsKeyboardFocusable = focusable,
            Rect = ((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height),
        };
    }

    private static UiNodeData Dead() => new() { Role = "unavailable", IsOffscreen = true };

    private static List<string> ReadPatterns(AutomationElement el, UiNodeData data)
    {
        var patterns = new List<string>();
        if (!data.IsKeyboardFocusable && !KnownInteractive(el)) return patterns;
        TryAdd(el, patterns, InvokePattern.Pattern, "invoke");
        TryAdd(el, patterns, TogglePattern.Pattern, "toggle");
        TryAdd(el, patterns, ValuePattern.Pattern, "value");
        TryAdd(el, patterns, ExpandCollapsePattern.Pattern, "expandcollapse");
        TryAdd(el, patterns, SelectionItemPattern.Pattern, "selectionitem");
        TryAdd(el, patterns, ScrollPattern.Pattern, "scroll");
        return patterns;
    }

    private static bool KnownInteractive(AutomationElement el)
    {
        try { return KnownInteractive(el.Current.ControlType); }
        catch (ElementNotAvailableException) { return false; }
    }

    private static bool KnownInteractive(ControlType? ct) =>
        ct == ControlType.Button || ct == ControlType.Hyperlink || ct == ControlType.MenuItem
        || ct == ControlType.ListItem || ct == ControlType.TreeItem || ct == ControlType.CheckBox
        || ct == ControlType.RadioButton || ct == ControlType.ComboBox || ct == ControlType.Edit
        || ct == ControlType.TabItem || ct == ControlType.Spinner || ct == ControlType.Slider;

    private static void TryAdd(AutomationElement el, List<string> into, AutomationPattern pattern, string name)
    {
        try
        {
            if (el.TryGetCurrentPattern(pattern, out _)) into.Add(name);
        }
        catch (ElementNotAvailableException) { }
    }

    // --- live resolution for actions ---

    public sealed class ResolveOutcome
    {
        public AutomationElement? Element { get; init; }
        public string How { get; init; } = "";
        public JsonObject Diagnostic { get; init; } = new();
    }

    /// <summary>Re-resolves a saved ref: candidate windows by pid (+hwnd), runtime-id pinning, identity match.</summary>
    public static ResolveOutcome Resolve(RefEntry entry)
    {
        var diag = new JsonObject
        {
            ["savedPid"] = entry.Pid,
            ["savedHwnd"] = entry.Hwnd,
            ["savedWindowName"] = entry.WindowName,
            ["runtimeIdLength"] = entry.RuntimeId.Length,
        };

        // candidate windows: every top-level window of the pid (WinUI/Chrome host content islands
        // where FromHandle(hwnd) is NOT the element the snapshot walked), plus the hwnd itself
        var candidates = new List<AutomationElement>();
        if (entry.Hwnd > 0)
        {
            try { candidates.Add(AutomationElement.FromHandle(new IntPtr(entry.Hwnd))); }
            catch (ElementNotAvailableException) { }
        }
        try
        {
            foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, entry.Pid)))
                candidates.Add(w);
        }
        catch (ElementNotAvailableException) { }

        var scannedWindows = 0;
        foreach (var window in candidates.DistinctBy(WindowKeyOf))
        {
            scannedWindows++;
            var info = InfoOf(window);
            diag["lastWindow"] = new JsonObject { ["name"] = info.Name, ["hwnd"] = info.Hwnd };

            var outcome = SearchWindow(window, entry, diag);
            if (outcome is not null) { diag["scannedWindows"] = scannedWindows; return outcome; }
        }
        diag["scannedWindows"] = scannedWindows;
        return new ResolveOutcome { Diagnostic = diag };
    }

    private static long WindowKeyOf(AutomationElement el)
    {
        try { return el.GetRuntimeId().Aggregate(0L, (acc, v) => acc * 31 + v); }
        catch (ElementNotAvailableException) { return Random.Shared.NextInt64(); }
    }

    private static ResolveOutcome? SearchWindow(AutomationElement window, RefEntry entry, JsonObject diag)
    {
        // fast path: scope-path descent
        var byPath = Descend(window, entry.ScopePath);
        if (byPath is not null && MatchesIdentity(byPath, entry))
            return new ResolveOutcome { Element = byPath, How = "scope-path", Diagnostic = diag };

        // identity scan: runtime id first, then identity key
        AutomationElement? runtimeMatch = null;
        var identityMatches = new List<AutomationElement>();
        try
        {
            void Scan(AutomationElement node, int depth)
            {
                if (depth > 40 || runtimeMatch is not null) return;
                AutomationElement? child = View.GetFirstChild(node);
                var guard = 0;
                while (child is not null && guard++ < 3000)
                {
                    if (RuntimeIdEquals(child, entry.RuntimeId)) runtimeMatch = child;
                    if (MatchesIdentity(child, entry)) identityMatches.Add(child);
                    Scan(child, depth + 1);
                    if (runtimeMatch is not null) return;
                    child = View.GetNextSibling(child);
                }
            }
            Scan(window, 0);
        }
        catch (ElementNotAvailableException) { }

        diag["lastWindowRuntimeIdMatched"] = runtimeMatch is not null;
        diag["lastWindowIdentityMatchCount"] = identityMatches.Count;
        if (runtimeMatch is not null)
            return new ResolveOutcome { Element = runtimeMatch, How = "runtime-id", Diagnostic = diag };
        if (identityMatches.Count == 1)
            return new ResolveOutcome { Element = identityMatches[0], How = "identity", Diagnostic = diag };
        if (identityMatches.Count > 1)
            throw new AmbiguousTargetException(
                $"{identityMatches.Count} live elements match the saved identity");
        return null;
    }

    private static bool RuntimeIdEquals(AutomationElement el, int[] saved)
    {
        if (saved.Length == 0) return false;
        try
        {
            var live = el.GetRuntimeId();
            return live.SequenceEqual(saved);
        }
        catch (ElementNotAvailableException) { return false; }
    }

    private static bool MatchesIdentity(AutomationElement el, RefEntry entry)
    {
        try
        {
            var c = el.Current;
            return c.ProcessId == entry.Pid
                && string.Equals(c.AutomationId, entry.AutomationId, StringComparison.Ordinal)
                && string.Equals(c.Name ?? "", entry.Name, StringComparison.Ordinal)
                && string.Equals(c.ClassName ?? "", entry.ClassName, StringComparison.Ordinal)
                && RoleOf(c.ControlType, c.LocalizedControlType) == entry.Role;
        }
        catch (ElementNotAvailableException) { return false; }
    }

    private static string RoleOf(ControlType? ct, string localized) =>
        string.IsNullOrEmpty(localized) ? ct?.ProgrammaticName ?? "custom" : localized;

    private static AutomationElement? Descend(AutomationElement window, List<int> scopePath)
    {
        if (scopePath.Count == 0) return window;
        try
        {
            var node = window;
            foreach (var index in scopePath)
            {
                node = View.GetFirstChild(node);
                for (var i = 0; i < index && node is not null; i++)
                    node = View.GetNextSibling(node);
                if (node is null) return null;
            }
            return node;
        }
        catch (ElementNotAvailableException) { return null; }
    }
}

public sealed class AmbiguousTargetException(string message) : Exception(message);
