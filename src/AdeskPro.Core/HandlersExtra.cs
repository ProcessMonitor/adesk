using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdeskPro.Platform;
using AdeskPro.Ui;

namespace AdeskPro;

/// <summary>Window ops, headed mouse, screenshot, wait and batch — the second half of the command surface.</summary>
public static partial class Handlers
{
    // --- single dispatch entry shared by the CLI registry, MCP tools and batch ---

    /// <summary>Validates arguments against the command spec and runs it. The one path every caller goes through.</summary>
    public static Envelope Dispatch(string name, JsonObject arguments, Session session)
    {
        var spec = Registry.Find(name);
        if (spec is null)
            return Envelope.Fail(ErrorCodes.InvalidArgs, $"unknown command '{name}'", "run 'adesk-pro --help'");
        var args = new Args { Command = spec.Name };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in arguments)
        {
            var flag = spec.Flags.FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));
            if (flag is null)
                return Envelope.Fail(ErrorCodes.InvalidArgs, $"unknown flag --{key} for '{name}'",
                    $"run 'adesk-pro {name} --help'");
            if (!seen.Add(flag.Name))
                return Envelope.Fail(ErrorCodes.InvalidArgs, $"--{key} given twice");
            switch (flag.Kind)
            {
                case FlagKind.Flag:
                    // Accept real booleans and the strings clients actually send ("true"/"false"),
                    // so {"i": "false"} does not silently switch the filter on.
                    if (value is JsonValue bv && bv.TryGetValue<bool>(out var b)) args.Set(flag.Name, b);
                    else if (value is JsonValue svb && svb.TryGetValue<string>(out var sb) && bool.TryParse(sb, out var parsedBool))
                        args.Set(flag.Name, parsedBool);
                    else if (value is not null)
                        return Envelope.Fail(ErrorCodes.InvalidArgs,
                            $"--{key} expects true or false, got {value.ToJsonString()}");
                    break;
                case FlagKind.Integer:
                    if (value is JsonValue iv && iv.TryGetValue<int>(out var n)) args.Set(flag.Name, n);
                    else if (value is JsonValue sv2 && sv2.TryGetValue<string>(out var si) && int.TryParse(si, out var n2)) args.Set(flag.Name, n2);
                    else return Envelope.Fail(ErrorCodes.InvalidArgs, $"--{key} expects an integer");
                    break;
                case FlagKind.Ref:
                    var refText = value is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : value?.ToJsonString() ?? "";
                    if (Ids.ParseRef(refText) is null)
                        return Envelope.Fail(ErrorCodes.InvalidArgs, $"--{key}: '{refText}' is not a ref like @s8f3k2p9:e8");
                    args.Set(flag.Name, refText);
                    break;
                default:
                    var text = value is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "";
                    args.Set(flag.Name, text);
                    break;
            }
        }
        foreach (var f in spec.Flags.Where(f => f.Required && !args.Has(f.Name)))
            return Envelope.Fail(ErrorCodes.InvalidArgs, $"--{f.Name} is required for '{name}'");
        return spec.Run(args, session);
    }

    // --- screenshot ---

    public static Envelope ScreenshotCmd(Args a, Session s)
    {
        try
        {
            var info = s.Platform.FindWindow(a.Str("app"), OptInt(a, "pid"), OptLong(a, "hwnd"));
            var png = s.Platform.CapturePng(info.Hwnd);
            var data = new JsonObject
            {
                ["command"] = "screenshot",
                ["app"] = info.Name,
                ["format"] = "png",
            };
            var outPath = a.Str("out");
            if (outPath.Length > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                File.WriteAllBytes(outPath, png);
                data["path"] = Path.GetFullPath(outPath);
                data["bytes"] = png.Length;
            }
            else
            {
                data["data"] = Convert.ToBase64String(png);
            }
            return Envelope.Good(data);
        }
        catch (AppNotFoundException ex) { return Envelope.Fail(ErrorCodes.AppNotFound, ex.Message, "run 'adesk-pro list-apps'"); }
        catch (AmbiguousWindowException ex) { return AmbiguousWindows(ex); }
        catch (ArgumentException ex) { return Envelope.Fail(ErrorCodes.NotSupported, ex.Message); }
        catch (Exception ex) { return Internal(ex); }
    }

    // --- wait ---

    public static Envelope Wait(Args a, Session s)
    {
        var timeout = Math.Max(500, a.Int("timeout", 10000));
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        try
        {
            if (a.Str("ref") is { Length: > 0 } token)
            {
                var until = a.Str("until", "enabled");
                if (until is not ("enabled" or "visible" or "gone"))
                    return Envelope.Fail(ErrorCodes.InvalidArgs, "--until must be enabled, visible or gone");
                var parsed = Ids.ParseRef(token);
                if (parsed is null) return Envelope.Fail(ErrorCodes.InvalidArgs, $"'{token}' is not a ref");
                var (sid, index) = parsed.Value;
                var entry = s.Refs.Load(sid)?.GetValueOrDefault(index);
                if (entry is null) return Envelope.Fail(ErrorCodes.StaleRef, $"snapshot '{sid}' is unknown or expired");
                while (DateTime.UtcNow < deadline)
                {
                    var resolved = s.Platform.Resolve(entry);
                    var met = until switch
                    {
                        "gone" => resolved.Element is null,
                        "enabled" => resolved.Element?.Enabled == true,
                        "visible" => resolved.Element is not null && !resolved.Element.Offscreen,
                        _ => false,
                    };
                    if (met)
                        return Envelope.Good(new JsonObject
                        {
                            ["command"] = "wait", ["kind"] = "ref", ["until"] = until,
                            ["result"] = "met", ["waitedMs"] = timeout - Remaining(deadline),
                        });
                    Thread.Sleep(300);
                }
                // A timeout is a failure, not a success with a note: an agent that treats it as
                // ok will keep acting on a window/element that never showed up.
                return Envelope.Fail(ErrorCodes.Timeout,
                    $"a ref did not become '{until}' within {timeout}ms",
                    "the app may be slower than the budget; raise --timeout, or snapshot again to see the live tree",
                    details: new JsonObject
                    {
                        ["command"] = "wait", ["kind"] = "ref", ["until"] = until, ["timeout"] = timeout,
                    });
            }
            if (!string.IsNullOrWhiteSpace(a.Str("app")))
            {
                var app = a.Str("app");
                while (DateTime.UtcNow < deadline)
                {
                    if (s.Platform.ListTopWindows().Any(w =>
                        (w.Name?.Contains(app, StringComparison.OrdinalIgnoreCase) ?? false)
                        || w.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase)))
                        return Envelope.Good(new JsonObject
                        {
                            ["command"] = "wait", ["kind"] = "window", ["app"] = app,
                            ["result"] = "appeared", ["waitedMs"] = timeout - Remaining(deadline),
                        });
                    Thread.Sleep(300);
                }
                return Envelope.Fail(ErrorCodes.Timeout,
                    $"no window matching '{app}' appeared within {timeout}ms",
                    "run 'adesk-pro list-apps' to see the windows that do exist, then match on --app/--pid",
                    details: new JsonObject
                    {
                        ["command"] = "wait", ["kind"] = "window", ["app"] = app, ["timeout"] = timeout,
                    });
            }
            return Envelope.Fail(ErrorCodes.InvalidArgs, "wait needs --app or --ref");
        }
        catch (Exception ex) { return Internal(ex); }
    }

    private static int Remaining(DateTime deadline) =>
        Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);

    // --- batch ---

    public static Envelope Batch(Args a, Session s)
    {
        string? json = a.Has("stdin") ? Console.In.ReadToEnd()
            : a.Str("file") is { Length: > 0 } file ? File.ReadAllText(file)
            : null;
        if (string.IsNullOrWhiteSpace(json))
            return Envelope.Fail(ErrorCodes.InvalidArgs, "--stdin or --file with a JSON array is required");
        JsonArray? items;
        try { items = JsonNode.Parse(json) as JsonArray; }
        catch (JsonException ex) { return Envelope.Fail(ErrorCodes.InvalidArgs, $"batch input is not valid JSON: {ex.Message}"); }
        if (items is null)
            return Envelope.Fail(ErrorCodes.InvalidArgs, "batch input must be a JSON array of {command, arguments}");
        var stopOnError = a.Has("stop-on-error");
        var results = new JsonArray();
        var allOk = true;
        var ran = 0;
        foreach (var item in items)
        {
            if (item is not JsonObject obj)
            {
                allOk = false;
                results.Add(new JsonObject { ["error"] = "each batch item must be an object" });
                if (stopOnError) break;
                continue;
            }
            var name = obj["command"]?.GetValue<string>() ?? "";
            var arguments = obj["arguments"] as JsonObject ?? new JsonObject();
            var envelope = Dispatch(name, arguments, s);
            ran++;
            if (!envelope.Ok) allOk = false;
            var entryOut = new JsonObject { ["command"] = name, ["ok"] = envelope.Ok };
            if (envelope.Ok) entryOut["data"] = envelope.Data;
            else if (envelope.Error is not null)
                entryOut["error"] = JsonSerializer.SerializeToNode(envelope.Error, EnvelopeJson.Options);
            results.Add(entryOut);
            if (!envelope.Ok && stopOnError) break;
        }
        return Envelope.Good(new JsonObject
        {
            ["command"] = "batch",
            ["ran"] = ran,
            ["ok"] = allOk,
            ["stopped"] = stopOnError && !allOk && ran < items.Count,
            ["results"] = results,
        });
    }

    // --- app lifecycle and window geometry ---

    public static Envelope Launch(Args a, Session s)
    {
        try
        {
            var (pid, hwnd, name) = s.Platform.Launch(a.Str("exe"), a.Str("args"));
            return Envelope.Good(new JsonObject
            {
                ["command"] = "launch",
                ["exe"] = a.Str("exe"),
                ["pid"] = pid,
                ["hwnd"] = hwnd,
                ["window"] = name,
            });
        }
        catch (Exception ex) { return Internal(ex); }
    }

    public static Envelope CloseApp(Args a, Session s)
    {
        try
        {
            var closed = s.Platform.CloseApp(a.Str("app"), OptInt(a, "pid"), OptLong(a, "hwnd"), a.Has("force"));
            return Envelope.Good(new JsonObject { ["command"] = "close-app", ["closed"] = closed });
        }
        catch (AppNotFoundException ex) { return Envelope.Fail(ErrorCodes.AppNotFound, ex.Message); }
        catch (Exception ex) { return Internal(ex); }
    }

    public static Envelope Minimize(Args a, Session s) => WindowStateCmd(a, s, "minimize");
    public static Envelope Maximize(Args a, Session s) => WindowStateCmd(a, s, "maximize");
    public static Envelope Restore(Args a, Session s) => WindowStateCmd(a, s, "restore");

    private static Envelope WindowStateCmd(Args a, Session s, string state)
    {
        try
        {
            var changed = s.Platform.WindowState(a.Str("app"), OptInt(a, "pid"), OptLong(a, "hwnd"), state);
            if (changed == 0) return Envelope.Fail(ErrorCodes.NoPattern, "no matched window exposes the Window pattern");
            return Envelope.Good(new JsonObject { ["command"] = state, ["changed"] = changed });
        }
        catch (AppNotFoundException ex) { return Envelope.Fail(ErrorCodes.AppNotFound, ex.Message); }
        catch (Exception ex) { return Internal(ex); }
    }

    public static Envelope MoveWindow(Args a, Session s) => Geometry(a, s, "move-window", resize: false);
    public static Envelope ResizeWindow(Args a, Session s) => Geometry(a, s, "resize-window", resize: true);

    private static Envelope Geometry(Args a, Session s, string command, bool resize)
    {
        try
        {
            var pid = OptInt(a, "pid");
            var hwnd = OptLong(a, "hwnd");
            var app = a.Str("app");
            var windows = s.Platform.ListTopWindows()
                .Where(w => pid is null || w.Pid == pid)
                .Where(w => hwnd is null || w.Hwnd == hwnd)
                .Where(w => app.Length == 0
                    || (w.Name?.Contains(app, StringComparison.OrdinalIgnoreCase) ?? false)
                    || w.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (windows.Count == 0) return Envelope.Fail(ErrorCodes.AppNotFound, "no window matches the filter");
            if (windows.Count > 1) return AmbiguousWindows(new AmbiguousWindowException(windows));
            var info = windows[0];
            int x = a.Int("x", -1), y = a.Int("y", -1), w = a.Int("w", -1), h = a.Int("h", -1);
            if (resize && (w < 0 || h < 0))
                return Envelope.Fail(ErrorCodes.InvalidArgs, "--w and --h are required for resize-window");
            if (!resize && x < 0 && y < 0)
                return Envelope.Fail(ErrorCodes.InvalidArgs, "give --x and/or --y for move-window");
            s.Platform.MoveResize(info.Hwnd,
                x >= 0 ? x : null, y >= 0 ? y : null,
                resize ? w : null, resize ? h : null);
            return Envelope.Good(new JsonObject
            {
                ["command"] = command, ["hwnd"] = info.Hwnd,
                ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h,
            });
        }
        catch (Exception ex) { return Internal(ex); }
    }

    // --- headed mouse ---

    public static Envelope MouseClick(Args a, Session s)
    {
        try
        {
            s.Platform.MouseClick(a.Int("x"), a.Int("y"), a.Has("right"), a.Has("double"));
            return Envelope.Good(new JsonObject
            {
                ["command"] = "mouse-click", ["x"] = a.Int("x"), ["y"] = a.Int("y"),
                ["button"] = a.Has("right") ? "right" : "left",
                ["delivered"] = true,
                ["disposition"] = new JsonObject { ["delivery"] = "delivered", ["retry"] = "unsafe" },
            });
        }
        catch (Exception ex) { return Internal(ex, mayHaveDelivered: true); }
    }

    public static Envelope Hover(Args a, Session s)
    {
        try
        {
            s.Platform.Hover(a.Int("x"), a.Int("y"));
            return Envelope.Good(new JsonObject { ["command"] = "hover", ["x"] = a.Int("x"), ["y"] = a.Int("y") });
        }
        catch (Exception ex) { return Internal(ex, mayHaveDelivered: true); }
    }

    public static Envelope Drag(Args a, Session s)
    {
        try
        {
            s.Platform.Drag(a.Int("from-x"), a.Int("from-y"), a.Int("to-x"), a.Int("to-y"));
            return Envelope.Good(new JsonObject
            {
                ["command"] = "drag",
                ["from"] = new JsonObject { ["x"] = a.Int("from-x"), ["y"] = a.Int("from-y") },
                ["to"] = new JsonObject { ["x"] = a.Int("to-x"), ["y"] = a.Int("to-y") },
                ["delivered"] = true,
                ["disposition"] = new JsonObject { ["delivery"] = "delivered", ["retry"] = "unsafe" },
            });
        }
        catch (Exception ex) { return Internal(ex, mayHaveDelivered: true); }
    }

    // --- self-check ---

    /// <summary>One command that answers "why can't the agent see anything?" before the user
    /// has to guess at permissions, session mismatches or an empty tree.</summary>
    public static Envelope Doctor(Args a, Session s)
    {
        var checks = new JsonObject();
        var problems = new JsonArray();
        checks["platform"] = s.Platform.PlatformName;
        checks["version"] = Version.Value;

        foreach (var (key, value) in s.Platform.Diagnostics())
        {
            checks[key] = value;
            if (key is "warning" or "error") problems.Add(value);
        }

        try
        {
            var windows = s.Platform.ListTopWindows();
            checks["visibleWindows"] = windows.Count;
            if (windows.Count == 0)
                problems.Add("no visible top-level windows — check permissions and that the target app is running");
            else
                checks["sampleWindows"] = new JsonArray([.. windows.Take(5).Select(w => (JsonNode)new JsonObject
                {
                    ["name"] = w.Name, ["pid"] = w.Pid, ["process"] = w.ProcessName,
                })]);
        }
        catch (Exception ex)
        {
            checks["listWindowsError"] = ex.GetType().Name + ": " + ex.Message;
            problems.Add("listing windows failed: " + ex.Message);
        }

        return Envelope.Good(new JsonObject
        {
            ["command"] = "doctor",
            ["healthy"] = problems.Count == 0,
            ["checks"] = checks,
            ["problems"] = problems,
        });
    }

    // --- MCP config generation ---

    /// <summary>Set by the npm launcher so the binary knows it was reached through npm.
    /// The value is the npm package name to emit in the generated config.</summary>
    public const string LaunchContextEnvVar = "ADESK_PRO_NPM_LAUNCH";

    /// <summary>
    /// Emits an MCP server entry that points at <em>this running binary's</em> absolute path.
    /// Hand-writing a path into a config file is the single most common way a first install fails,
    /// so the binary writes it for you.
    ///
    /// When the npm launcher started this process, the snippet is emitted in <c>npx</c> form
    /// instead. A hard-coded path into <c>node_modules</c> would break on the next npm upgrade —
    /// the version-pinned platform directory stops existing.
    /// </summary>
    public static Envelope InstallMcp(Args a, Session s)
    {
        var client = string.IsNullOrWhiteSpace(a.Str("client")) ? "json" : a.Str("client").ToLowerInvariant();
        if (client is not ("json" or "claude" or "cursor" or "zcode"))
            return Envelope.Fail(ErrorCodes.InvalidArgs, $"unknown client '{client}'",
                "use claude, cursor, zcode or json");

        // The launcher marks the process; absent that mark we are the binary itself.
        var npmPackage = Environment.GetEnvironmentVariable(LaunchContextEnvVar);
        var viaNpm = !string.IsNullOrWhiteSpace(npmPackage);

        var exe = Environment.ProcessPath;
        if (!viaNpm && string.IsNullOrWhiteSpace(exe))
            return Envelope.Fail(ErrorCodes.Internal, "cannot determine this binary's path",
                "run the command from the binary itself, not from a tool that re-hosts it");

        // npx form survives `npm update`; an absolute path goes stale.
        var command = viaNpm ? "npx" : exe!;
        var args = viaNpm
            ? new JsonArray("-y", npmPackage, "mcp")
            : new JsonArray("mcp");

        var entry = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["adesk-pro"] = new JsonObject
                {
                    ["command"] = command,
                    ["args"] = args,
                },
            },
        };
        var snippet = entry.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        });

        var destination = a.Str("write");
        if (destination.Length > 0)
        {
            try
            {
                var full = Path.GetFullPath(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                if (File.Exists(full))
                    return Envelope.Fail(ErrorCodes.InvalidArgs,
                        $"'{full}' already exists",
                        "merge the snippet below by hand, or pass a new --write path",
                        details: new JsonObject { ["snippet"] = snippet });
                File.WriteAllText(full, snippet);
                return Envelope.Good(new JsonObject
                {
                    ["command"] = "install-mcp",
                    ["client"] = client,
                    ["via"] = viaNpm ? "npm" : "binary",
                    ["command_"] = command,
                    ["path"] = full,
                    ["snippet"] = snippet,
                    ["next"] = "restart your agent so it picks up the new config",
                });
            }
            catch (Exception ex) { return Internal(ex); }
        }

        var hint = client switch
        {
            "claude" when viaNpm => $"claude mcp add adesk-pro -- npx -y {npmPackage} mcp",
            "claude" => $"claude mcp add adesk-pro -- \"{exe}\" mcp",
            "cursor" => "paste this under 'mcpServers' in ~/.cursor/mcp.json, then restart Cursor",
            "zcode" => "paste this into your ZCode MCP configuration, then restart ZCode",
            _ => "paste this into your client's mcpServers block, then restart the client",
        };

        return Envelope.Good(new JsonObject
        {
            ["command"] = "install-mcp",
            ["client"] = client,
            ["via"] = viaNpm ? "npm" : "binary",
            ["command_"] = command,
            ["snippet"] = snippet,
            ["hint"] = hint,
        });
    }

    // --- embedded agent guides ---

    /// <summary>Prints an agent guide embedded in the running binary, so guidance always matches the version.</summary>
    public static Envelope Skills(Args a, Session s)
    {
        var name = string.IsNullOrWhiteSpace(a.Str("name")) ? "desktop" : a.Str("name");
        if (name is not ("desktop" or "platform"))
            return Envelope.Fail(ErrorCodes.InvalidArgs, $"unknown guide '{name}'",
                "available guides: desktop, platform");
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($"skills.{name}.md", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null) continue;
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            return Envelope.Good(new JsonObject
            {
                ["command"] = "skills",
                ["name"] = name,
                ["content"] = reader.ReadToEnd(),
            });
        }
        return Envelope.Fail(ErrorCodes.NotSupported, $"no guide named '{name}' is embedded in this build",
            "guides ship in the adesk-pro binary");
    }
}

/// <summary>Scope-path math for drill-downs, kept pure so it can be tested without UIA.</summary>
public static class DrillMath
{
    /// <summary>Re-stamps a drilled tree's refs from the local→global index map; refs that were
    /// dropped by the merge become null.</summary>
    public static void StampDrilled(UiNodeData n, string sid, Dictionary<int, int> localToGlobal)
    {
        if (n.Ref is not null)
        {
            n.Ref = Ids.ParseRef(n.Ref) is { } p && localToGlobal.TryGetValue(p.Index, out var global)
                ? Ids.FormatRef(sid, global)
                : null;
        }
        foreach (var child in n.Children) StampDrilled(child, sid, localToGlobal);
    }

    /// <summary>Merges a fresh subtree walk into an existing snapshot: keeps entries outside the subtree,
    /// composes fresh scope paths onto the drilled root's path. Fresh ids start above the highest
    /// pre-existing id — a just-freed id is never reused, or an old ref would silently resolve
    /// to a different element.</summary>
    public static (Dictionary<int, RefEntry> Merged, Dictionary<int, int> LocalToGlobal) Merge(
        Dictionary<int, RefEntry> existing, int rootIndex, RefEntry root, Dictionary<int, RefEntry> fresh)
    {
        static bool StrictlyInside(List<int> scope, List<int> rootPath) =>
            scope.Count > rootPath.Count && !scope.Take(rootPath.Count).Where((v, i) => v != rootPath[i]).Any();

        var next = existing.Count > 0 ? existing.Keys.Max() + 1 : 1;
        var merged = existing
            .Where(kv => kv.Key == rootIndex || !StrictlyInside(kv.Value.ScopePath, root.ScopePath))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var localToGlobal = new Dictionary<int, int>();
        foreach (var (local, entry) in fresh)
        {
            entry.ScopePath = root.ScopePath.Concat(entry.ScopePath).ToList();
            entry.WindowName = root.WindowName;
            merged[next] = entry;
            localToGlobal[local] = next;
            next++;
        }
        return (merged, localToGlobal);
    }
}
