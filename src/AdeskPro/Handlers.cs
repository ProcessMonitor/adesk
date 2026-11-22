using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using AdeskPro.Ui;

namespace AdeskPro;

/// <summary>Command implementations. Every handler returns one Envelope; UIA exceptions map to error codes.</summary>
public static class Handlers
{
    public static Envelope Status(Args a, Session s) => Envelope.Good(new JsonObject
    {
        ["command"] = "status",
        ["platform"] = $"windows {Environment.OSVersion.Version}",
        ["version"] = Version.Value,
        ["session"] = s.Name,
        ["stateMode"] = s.InMemory ? "memory" : "disk",
        ["storeDir"] = s.InMemory ? null : SessionStore.Dir(s.Name),
        ["snapshotCount"] = CountSnapshots(s),
    });

    public static Envelope ListApps(Args a, Session s)
    {
        try
        {
            var apps = TopWindows()
                .Where(w => string.IsNullOrEmpty(a.Str("process")) || w.ProcessName.Contains(a.Str("process"), StringComparison.OrdinalIgnoreCase))
                .Select(w => new JsonObject
                {
                    ["name"] = w.Name,
                    ["pid"] = w.Pid,
                    ["process"] = w.ProcessName,
                    ["hwnd"] = w.Hwnd,
                }).ToList();
            return Envelope.Good(new JsonObject
            {
                ["command"] = "list-apps",
                ["count"] = apps.Count,
                ["apps"] = new JsonArray([.. apps]),
            });
        }
        catch (Exception ex) { return Internal(ex); }
    }

    public static Envelope ListWindows(Args a, Session s)
    {
        try
        {
            var pid = a.Int("pid", 0);
            var proc = a.Str("process");
            var windows = TopWindows()
                .Where(w => pid == 0 || w.Pid == pid)
                .Where(w => proc.Length == 0 || w.ProcessName.Contains(proc, StringComparison.OrdinalIgnoreCase))
                .Select(w => new JsonObject
                {
                    ["name"] = w.Name, ["pid"] = w.Pid,
                    ["process"] = w.ProcessName, ["hwnd"] = w.Hwnd,
                }).ToList();
            return Envelope.Good(new JsonObject { ["command"] = "list-windows", ["count"] = windows.Count, ["windows"] = new JsonArray([.. windows]) });
        }
        catch (Exception ex) { return Internal(ex); }
    }

    public static Envelope Snapshot(Args a, Session s)
    {
        try
        {
            var (window, info) = UiASource.FindWindow(a.Str("app"), OptInt(a, "pid"), OptLong(a, "hwnd"));
            var source = UiASource.ForWindow(window);
            var result = Walker.Walk(source, new Walker.Options
            {
                InteractiveOnly = a.Has("i"),
                Skeleton = a.Has("skeleton"),
                MaxDepth = a.Int("max-depth", 0) > 0 ? a.Int("max-depth") : null,
                Budget = new WalkBudget { DeadlineMs = a.Int("timeout-ms", 5000) },
            });

            var sid = Ids.NewSnapshotId();
            foreach (var e in result.Refs.Values) e.WindowName = info.Name;
            ReStamp(result.Root, sid);
            s.Refs.Save(sid, result.Refs);

            var tree = ToJson(result.Root, a.Has("skeleton"));
            return Envelope.Good(new JsonObject
            {
                ["command"] = "snapshot",
                ["app"] = info.Name,
                ["pid"] = info.Pid,
                ["process"] = info.ProcessName,
                ["hwnd"] = info.Hwnd,
                ["snapshotId"] = sid,
                ["refCount"] = result.Refs.Count,
                ["nodeCount"] = result.NodeCount,
                ["complete"] = result.Complete,
                ["tree"] = tree,
            });
        }
        catch (AppNotFoundException ex) { return Envelope.Fail(ErrorCodes.AppNotFound, ex.Message, "run 'adesk-pro list-apps' to see candidates"); }
        catch (AmbiguousWindowException ex) { return AmbiguousWindows(ex); }
        catch (Exception ex) { return Internal(ex); }
    }

    public static Envelope Find(Args a, Session s)
    {
        try
        {
            var (window, info) = UiASource.FindWindow(a.Str("app"), OptInt(a, "pid"), null);
            var needle = a.Str("name");
            var role = a.Str("role");
            var limit = Math.Max(1, a.Int("limit", 10));
            var source = UiASource.ForWindow(window);
            var result = Walker.Walk(source, new Walker.Options { Budget = new WalkBudget { DeadlineMs = 5000 } });

            var sid = Ids.NewSnapshotId();
            var matches = new JsonArray();
            var index = result.Refs.Count > 0 ? result.Refs.Keys.Max() : 0;
            var found = 0;
            Collect(result.Root);
            void Collect(UiNodeData n)
            {
                if (found >= limit) return;
                if (n.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    && (role.Length == 0 || string.Equals(n.Role, role, StringComparison.OrdinalIgnoreCase)))
                {
                    if (n.Ref is null && n.Raw is not null)
                    {
                        index++;
                        result.Refs[index] = new RefEntry
                        {
                            Pid = n.Pid, Hwnd = n.Hwnd, RuntimeId = n.RuntimeId,
                            Role = n.Role, Name = n.Name, AutomationId = n.AutomationId,
                            ClassName = n.ClassName, ScopePath = n.ScopePath, Patterns = n.Patterns,
                            CapturedUnique = false, Rect = [n.Rect.X, n.Rect.Y, n.Rect.W, n.Rect.H],
                        };
                        n.Ref = Ids.FormatRef("__sid__", index);
                    }
                    if (n.Ref is not null)
                    {
                        found++;
                        ReStampNode(n, sid);
                        matches.Add(new JsonObject
                        {
                            ["ref"] = n.Ref,
                            ["role"] = n.Role,
                            ["name"] = n.Name,
                            ["patterns"] = new JsonArray([.. n.Patterns.Select(p => JsonValue.Create(p))]),
                        });
                    }
                }
                foreach (var child in n.Children) Collect(child);
            }
            s.Refs.Save(sid, result.Refs);
            return Envelope.Good(new JsonObject
            {
                ["command"] = "find",
                ["app"] = info.Name,
                ["snapshotId"] = sid,
                ["count"] = found,
                ["matches"] = matches,
            });
        }
        catch (AppNotFoundException ex) { return Envelope.Fail(ErrorCodes.AppNotFound, ex.Message, "run 'adesk-pro list-apps'"); }
        catch (AmbiguousWindowException ex) { return AmbiguousWindows(ex); }
        catch (Exception ex) { return Internal(ex); }
    }

    public static Envelope Get(Args a, Session s)
    {
        return WithElement(a, s, (el, entry, how) =>
        {
            string? value = null;
            try { if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var v)) value = ((ValuePattern)v).Current.Value; }
            catch (ElementNotAvailableException) { }
            var rect = el.Current.BoundingRectangle;
            return Envelope.Good(new JsonObject
            {
                ["command"] = "get",
                ["ref"] = a.Str("ref"),
                ["resolvedBy"] = how,
                ["role"] = entry.Role,
                ["name"] = el.Current.Name,
                ["value"] = value,
                ["enabled"] = el.Current.IsEnabled,
                ["offscreen"] = el.Current.IsOffscreen,
                ["rect"] = new JsonObject { ["x"] = (int)rect.X, ["y"] = (int)rect.Y, ["w"] = (int)rect.Width, ["h"] = (int)rect.Height },
                ["patterns"] = new JsonArray([.. entry.Patterns.Select(p => JsonValue.Create(p))]),
            });
        });
    }

    public static Envelope Click(Args a, Session s) =>
        Act(a, s, (el) => Actions.Click(el), "click");

    public static Envelope Toggle(Args a, Session s) =>
        Act(a, s, (el) => Actions.Toggle(el), "toggle");

    public static Envelope Expand(Args a, Session s)
    {
        var collapse = a.Has("collapse");
        return Act(a, s, (el) => Actions.Expand(el, collapse), collapse ? "collapse" : "expand");
    }

    public static Envelope Select(Args a, Session s) =>
        Act(a, s, (el) => Actions.Select(el), "select");

    public static Envelope Focus(Args a, Session s)
    {
        if (a.Str("ref") is { Length: > 0 } rf)
            return Act(a, s, (el) => Actions.FocusEl(el), "focus");
        try
        {
            var (window, info) = UiASource.FindWindow(a.Str("app"), OptInt(a, "pid"), null);
            window.SetFocus();
            return Envelope.Good(new JsonObject { ["command"] = "focus", ["app"] = info.Name, ["how"] = "window" });
        }
        catch (AppNotFoundException ex) { return Envelope.Fail(ErrorCodes.AppNotFound, ex.Message); }
        catch (Exception ex) { return Internal(ex); }
    }

    public static Envelope Type(Args a, Session s) => TextAction(a, s, "type");

    public static Envelope SetValue(Args a, Session s) => TextAction(a, s, "set-value");

    public static Envelope Press(Args a, Session s)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(a.Str("app")))
            {
                var (window, _) = UiASource.FindWindow(a.Str("app"), OptInt(a, "pid"), null);
                window.SetFocus();
            }
            Actions.PressKeys(a.Str("keys"));
            return Envelope.Good(new JsonObject
            {
                ["command"] = "press",
                ["keys"] = a.Str("keys"),
                ["delivered"] = true,
                ["disposition"] = new JsonObject { ["delivery"] = "delivered", ["retry"] = "unsafe" },
            });
        }
        catch (Ui.NoPatternException ex) { return NoPattern(ex); }
        catch (Exception ex) { return Internal(ex); }
    }

    public static Envelope Scroll(Args a, Session s)
    {
        var dy = a.Int("dy", 30);
        return Act(a, s, (el) => Actions.ScrollDy(el, dy), "scroll");
    }

    public static Envelope ClipboardGet(Args a, Session s)
    {
        try { return Envelope.Good(new JsonObject { ["command"] = "clipboard-get", ["text"] = Actions.ClipboardGet() }); }
        catch (Exception ex) { return Internal(ex); }
    }

    public static Envelope ClipboardSet(Args a, Session s)
    {
        var text = ReadText(a);
        if (text is null) return Envelope.Fail(ErrorCodes.InvalidArgs, "--text or --stdin is required");
        try { Actions.ClipboardSet(text); return Envelope.Good(new JsonObject { ["command"] = "clipboard-set" }); }
        catch (Exception ex) { return Internal(ex); }
    }

    // --- shared plumbing ---

    private static Envelope TextAction(Args a, Session s, string command)
    {
        var text = ReadText(a);
        if (text is null) return Envelope.Fail(ErrorCodes.InvalidArgs, "--text or --stdin is required");
        return Act(a, s, el =>
        {
            if (command == "type")
            {
                var typed = Actions.TypeText(el, text);
                return (typed.How, (string?)typed.Value);
            }
            var value = Actions.SetValue(el, text);
            return ("value", (string?)value);
        }, command, extra: new JsonObject { ["chars"] = text.Length });
    }

    private static Envelope Act(Args a, Session s, Func<AutomationElement, string> action,
        string command, JsonObject? extra = null)
        => Act(a, s, el => (action(el), (string?)null), command, extra);

    private static Envelope Act(Args a, Session s, Func<AutomationElement, (string How, string? Value)> action,
        string command, JsonObject? extra = null)
    {
        return WithElement(a, s, (el, entry, how) =>
        {
            try
            {
                var (actionHow, value) = action(el);
                var data = new JsonObject
                {
                    ["command"] = command,
                    ["ref"] = a.Str("ref"),
                    ["resolvedBy"] = how,
                    ["how"] = actionHow,
                    ["delivered"] = true,
                    ["disposition"] = new JsonObject { ["delivery"] = "delivered", ["retry"] = "unsafe" },
                };
                if (value is not null) data["value"] = value;
                if (extra is not null) foreach (var kv in extra) data[kv.Key] = kv.Value?.DeepClone();
                return Envelope.Good(data);
            }
            catch (Ui.NoPatternException ex) { return NoPattern(ex); }
            catch (ElementNotAvailableException) { return Stale(a.Str("ref")); }
            catch (Exception ex) { return Internal(ex); }
        });
    }

    /// <summary>Resolves a ref (from --ref) to a live element and hands it to the continuation.</summary>
    private static Envelope WithElement(Args a, Session s,
        Func<AutomationElement, RefEntry, string, Envelope> continuation)
    {
        var token = a.Str("ref");
        var parsed = Ids.ParseRef(token);
        if (parsed is null) return Envelope.Fail(ErrorCodes.InvalidArgs, $"'{token}' is not a ref like @s8f3k2p9:e8");
        var (sid, index) = parsed.Value;
        var entries = s.Refs.Load(sid);
        if (entries is null || !entries.TryGetValue(index, out var entry))
            return Envelope.Fail(ErrorCodes.StaleRef, $"snapshot '{sid}' is unknown or expired",
                "take a new snapshot");
        try
        {
            var resolved = UiASource.Resolve(entry);
            if (resolved.Element is null)
                return Envelope.Fail(ErrorCodes.StaleRef,
                    "the element or its window is gone",
                    "take a new snapshot and use the fresh ref",
                    details: new JsonObject { ["kind"] = "element_absent", ["diagnostic"] = resolved.Diagnostic });
            return continuation(resolved.Element, entry, resolved.How);
        }
        catch (AmbiguousTargetException ex)
        {
            return Envelope.Fail(ErrorCodes.AmbiguousTarget, ex.Message,
                "take a new snapshot of the region and pick a unique element",
                details: new JsonObject { ["kind"] = "identity_ambiguous" });
        }
        catch (Exception ex) { return Internal(ex); }
    }

    private static Envelope Stale(string token) =>
        Envelope.Fail(ErrorCodes.StaleRef, $"the element behind {token} is gone", "take a new snapshot");

    private static Envelope NoPattern(Ui.NoPatternException ex) =>
        Envelope.Fail(ErrorCodes.NoPattern, ex.Message, ex.Suggestion);

    private static Envelope AmbiguousWindows(AmbiguousWindowException ex) =>
        Envelope.Fail(ErrorCodes.AmbiguousWindow, ex.Message,
            "narrow with --pid or --hwnd; use 'adesk-pro list-windows' for candidates",
            details: new JsonObject
            {
                ["windows"] = new JsonArray([.. ex.Windows.Select(w => new JsonObject
                {
                    ["name"] = w.Name, ["pid"] = w.Pid, ["process"] = w.ProcessName, ["hwnd"] = w.Hwnd,
                })]),
            });

    private static Envelope Internal(Exception ex) =>
        Envelope.Fail(ErrorCodes.Internal, ex.GetType().Name + ": " + ex.Message,
            details: new JsonObject
            {
                ["stack"] = string.Join('\n', (ex.StackTrace ?? "").Split('\n').Take(4)),
            });

    private static string? ReadText(Args a)
    {
        if (a.Has("stdin")) return Console.In.ReadToEnd().TrimEnd('\r', '\n');
        var text = a.Str("text");
        return text.Length > 0 || a.Has("text") ? text : null;
    }

    private static int? OptInt(Args a, string name) => a.Int(name, 0) > 0 ? a.Int(name) : null;
    private static long? OptLong(Args a, string name) => a.Int(name, 0) > 0 ? a.Int(name) : null;

    private static IReadOnlyList<WindowInfo> TopWindows()
    {
        var list = new List<WindowInfo>();
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
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

    private static JsonObject? ToJson(UiNodeData n, bool skeleton)
    {
        var o = new JsonObject
        {
            ["role"] = n.Role,
            ["name"] = n.Name,
        };
        if (n.Ref is not null) o["ref"] = n.Ref;
        if (n.Patterns.Count > 0) o["patterns"] = new JsonArray([.. n.Patterns.Select(p => JsonValue.Create(p))]);
        if (skeleton && n.SkeletonCut) { o["childrenCount"] = n.ChildCount; return o; }
        if (n.Children.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var child in n.Children)
            {
                var childJson = ToJson(child, skeleton);
                if (childJson is not null) arr.Add(childJson);
            }
            o["children"] = arr;
        }
        return o;
    }

    private static void ReStamp(UiNodeData n, string sid)
    {
        if (n.Ref is not null) n.Ref = n.Ref.Replace("__sid__", sid);
        foreach (var child in n.Children) ReStamp(child, sid);
    }

    private static void ReStampNode(UiNodeData n, string sid)
    {
        if (n.Ref is not null) n.Ref = n.Ref.Replace("__sid__", sid);
    }

    private static int CountSnapshots(Session s) => s.InMemory
        ? s.Refs.MemorySnapshotCount
        : Directory.Exists(Path.Join(SessionStore.Dir(s.Name), "snapshots"))
            ? Directory.GetFiles(Path.Join(SessionStore.Dir(s.Name), "snapshots"), "*.json").Length
            : 0;
}

public static class SessionStore
{
    public static string Dir(string session) =>
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "adesk-pro", "sessions", session);
}

public static class Version
{
    public static string Value { get; } = typeof(Handlers).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
