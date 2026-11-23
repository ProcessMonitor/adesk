using System.Text.Json.Nodes;
using AdeskPro.Platform;
using AdeskPro.Ui;

namespace AdeskPro;

/// <summary>A command flag, shared by the CLI parser, --help text and the MCP JSON schema.</summary>
public sealed record FlagSpec(string Name, FlagKind Kind, bool Required, string Description);

public enum FlagKind { Flag, String, Integer, Ref, Text }

/// <summary>One command. The CLI, --help and the MCP tool catalog are all derived from this table.</summary>
/// <param name="Positional">Flag names filled, in order, from bare (non <c>--flag</c>) arguments.
/// This is what makes <c>adesk-pro click @s8f3k2p9:e12</c> work as documented; the canonical
/// <c>--ref</c> form always stays available.</param>
/// <param name="Verbs">Optional leading noise words accepted before the positionals, e.g. the
/// <c>get</c> in <c>adesk-pro skills get desktop</c>.</param>
/// <param name="NeedsAccessibility">True when the command reads or acts on an app's accessibility
/// tree. Only these commands may ask the OS for permission — a command that reports status or
/// merely lists windows must never pop a permission dialog.</param>
public sealed record CommandSpec(
    string Name,
    string Description,
    FlagSpec[] Flags,
    Func<Args, Session, Envelope> Run,
    string[]? Positional = null,
    string[]? Verbs = null,
    bool NeedsAccessibility = false);

public sealed class Args
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; init; } = "";
    public void Set(string name, object? value) => _values[name] = value;
    public bool Has(string name) => _values.ContainsKey(name);
    public string Str(string name, string def = "") =>
        _values.TryGetValue(name, out var v) && v is string s ? s : def;
    public int Int(string name, int def = 0) =>
        _values.TryGetValue(name, out var v) && v is int i ? i : def;
}

/// <summary>Session-scoped state: the platform adapter and the snapshot store for this session.</summary>
public sealed class Session
{
    public required string Name { get; init; }
    public required bool InMemory { get; init; }   // MCP mode keeps state in the server process
    public required IPlatform Platform { get; init; }
    public RefStore Refs { get; set; } = null!;
}

/// <summary>The one table that defines every command. Nothing else hard-codes a command name.</summary>
public static class Registry
{
    /// <summary>Shared window-target flags for the lifecycle/geometry commands.</summary>
    private static readonly FlagSpec[] WindowTarget =
    [
        new FlagSpec("app", FlagKind.String, false, "Process/window title match"),
        new FlagSpec("pid", FlagKind.Integer, false, "Process id"),
        new FlagSpec("hwnd", FlagKind.Integer, false, "Window handle"),
    ];

    public static readonly CommandSpec[] Commands =
    [
        new("status", "Show platform, session and store info.", [],
            (a, s) => Handlers.Status(a, s)),
        new("list-apps", "List top-level windows of running apps.",
            [new FlagSpec("process", FlagKind.String, false, "Filter by process name (substring)")],
            (a, s) => Handlers.ListApps(a, s)),
        new("list-windows", "List windows of one app.",
            [new FlagSpec("process", FlagKind.String, false, "Process name filter"),
             new FlagSpec("pid", FlagKind.Integer, false, "Process id filter")],
            (a, s) => Handlers.ListWindows(a, s)),
        new("snapshot", "Read an app window's accessibility tree; interactive elements get refs.",
            [new FlagSpec("app", FlagKind.String, false, "Process/window title match"),
             new FlagSpec("pid", FlagKind.Integer, false, "Process id"),
             new FlagSpec("hwnd", FlagKind.Integer, false, "Window handle"),
             new FlagSpec("i", FlagKind.Flag, false, "Interactive elements only"),
             new FlagSpec("skeleton", FlagKind.Flag, false, "Cut deep subtrees, report children_count"),
             new FlagSpec("root", FlagKind.Ref, false, "Re-drill one subtree of a previous snapshot"),
             new FlagSpec("max-depth", FlagKind.Integer, false, "Override walk depth cap"),
             new FlagSpec("timeout-ms", FlagKind.Integer, false, "Walk budget in milliseconds (default 5000)")],
            (a, s) => Handlers.Snapshot(a, s), NeedsAccessibility: true),
        new("find", "Find elements by name/role inside a window and return refs.",
            [new FlagSpec("app", FlagKind.String, false, "Process/window title match"),
             new FlagSpec("pid", FlagKind.Integer, false, "Process id"),
             new FlagSpec("name", FlagKind.String, true, "Name substring to match"),
             new FlagSpec("role", FlagKind.String, false, "Control type filter"),
             new FlagSpec("limit", FlagKind.Integer, false, "Max matches (default 10)")],
            (a, s) => Handlers.Find(a, s), NeedsAccessibility: true),
        new("get", "Read properties of one ref.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot")],
            (a, s) => Handlers.Get(a, s), ["ref"], NeedsAccessibility: true),
        new("click", "Invoke an element's default action (Invoke pattern).",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot")],
            (a, s) => Handlers.Click(a, s), ["ref"], NeedsAccessibility: true),
        new("toggle", "Toggle a check box / toggle button.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot")],
            (a, s) => Handlers.Toggle(a, s), ["ref"], NeedsAccessibility: true),
        new("expand", "Expand or collapse an element.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot"),
             new FlagSpec("collapse", FlagKind.Flag, false, "Collapse instead of expand")],
            (a, s) => Handlers.Expand(a, s), ["ref"], NeedsAccessibility: true),
        new("select", "Select a list/tree item.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot")],
            (a, s) => Handlers.Select(a, s), ["ref"], NeedsAccessibility: true),
        new("type", "Set or type text into an editable element.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot"),
             new FlagSpec("text", FlagKind.Text, false, "Text to enter"),
             new FlagSpec("stdin", FlagKind.Flag, false, "Read text from stdin instead of --text")],
            (a, s) => Handlers.Type(a, s), ["ref", "text"], NeedsAccessibility: true),
        new("set-value", "Set an element's value via the Value pattern.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot"),
             new FlagSpec("text", FlagKind.Text, false, "New value"),
             new FlagSpec("stdin", FlagKind.Flag, false, "Read value from stdin")],
            (a, s) => Handlers.SetValue(a, s), ["ref", "text"], NeedsAccessibility: true),
        new("press", "Send a keyboard shortcut, e.g. ctrl+s.",
            [new FlagSpec("keys", FlagKind.String, true, "Key combo, + separated"),
             new FlagSpec("app", FlagKind.String, false, "Focus this app's window first")],
            (a, s) => Handlers.Press(a, s), NeedsAccessibility: true),
        new("scroll", "Scroll the nearest scrollable ancestor of a ref.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot"),
             new FlagSpec("dy", FlagKind.Integer, false, "Vertical percent delta, negative = up (default 30)")],
            (a, s) => Handlers.Scroll(a, s), ["ref"], NeedsAccessibility: true),
        new("focus", "Focus an element or window.",
            [new FlagSpec("ref", FlagKind.Ref, false, "Ref to focus"),
             new FlagSpec("app", FlagKind.String, false, "Focus this app's main window")],
            (a, s) => Handlers.Focus(a, s), NeedsAccessibility: true),
        new("clipboard-get", "Read clipboard text.", [], (a, s) => Handlers.ClipboardGet(a, s)),
        new("clipboard-set", "Write clipboard text.",
            [new FlagSpec("text", FlagKind.Text, false, "Text"),
             new FlagSpec("stdin", FlagKind.Flag, false, "Read text from stdin")],
            (a, s) => Handlers.ClipboardSet(a, s), ["text"]),
        new("screenshot", "Capture a window to a PNG (write to --out, or inline base64).",
            [.. WindowTarget, new FlagSpec("out", FlagKind.String, false, "PNG output path; omit for inline base64")],
            (a, s) => Handlers.ScreenshotCmd(a, s), NeedsAccessibility: true),
        new("wait", "Poll until a window appears or a ref turns enabled/visible/gone.",
            [.. WindowTarget, new FlagSpec("ref", FlagKind.Ref, false, "Element to wait on"),
             new FlagSpec("until", FlagKind.String, false, "enabled | visible | gone (default enabled)"),
             new FlagSpec("timeout", FlagKind.Integer, false, "Milliseconds (default 10000)")],
            (a, s) => Handlers.Wait(a, s), NeedsAccessibility: true),
        new("batch", "Run several commands sequentially in this one process.",
            [new FlagSpec("stdin", FlagKind.Flag, false, "Read the JSON batch array from stdin"),
             new FlagSpec("file", FlagKind.String, false, "JSON batch array file"),
             new FlagSpec("stop-on-error", FlagKind.Flag, false, "Stop at the first failed command")],
            (a, s) => Handlers.Batch(a, s), NeedsAccessibility: true),
        new("launch", "Start a program and return its top-level window.",
            [new FlagSpec("exe", FlagKind.String, true, "Executable path"),
             new FlagSpec("args", FlagKind.String, false, "Command-line arguments")],
            (a, s) => Handlers.Launch(a, s), NeedsAccessibility: true),
        new("close-app", "Close an app's windows via the Window pattern.",
            [.. WindowTarget, new FlagSpec("force", FlagKind.Flag, false, "Kill the process as a fallback")],
            (a, s) => Handlers.CloseApp(a, s), NeedsAccessibility: true),
        new("minimize", "Minimize an app's windows.", [.. WindowTarget],
            (a, s) => Handlers.Minimize(a, s), NeedsAccessibility: true),
        new("maximize", "Maximize an app's windows.", [.. WindowTarget],
            (a, s) => Handlers.Maximize(a, s), NeedsAccessibility: true),
        new("restore", "Restore an app's windows.", [.. WindowTarget],
            (a, s) => Handlers.Restore(a, s), NeedsAccessibility: true),
        new("move-window", "Move a window.",
            [.. WindowTarget, new FlagSpec("x", FlagKind.Integer, false, "New X"),
             new FlagSpec("y", FlagKind.Integer, false, "New Y")],
            (a, s) => Handlers.MoveWindow(a, s), NeedsAccessibility: true),
        new("resize-window", "Resize a window.",
            [.. WindowTarget, new FlagSpec("w", FlagKind.Integer, true, "New width"),
             new FlagSpec("h", FlagKind.Integer, true, "New height")],
            (a, s) => Handlers.ResizeWindow(a, s), NeedsAccessibility: true),
        new("mouse-click", "Physical mouse click at screen coordinates (moves the cursor).",
            [new FlagSpec("x", FlagKind.Integer, true, "Screen X"),
             new FlagSpec("y", FlagKind.Integer, true, "Screen Y"),
             new FlagSpec("right", FlagKind.Flag, false, "Right button"),
             new FlagSpec("double", FlagKind.Flag, false, "Double click")],
            (a, s) => Handlers.MouseClick(a, s), NeedsAccessibility: true),
        new("hover", "Move the cursor to screen coordinates.",
            [new FlagSpec("x", FlagKind.Integer, true, "Screen X"),
             new FlagSpec("y", FlagKind.Integer, true, "Screen Y")],
            (a, s) => Handlers.Hover(a, s), NeedsAccessibility: true),
        new("drag", "Press, move, release — from one point to another.",
            [new FlagSpec("from-x", FlagKind.Integer, true, "Start X"),
             new FlagSpec("from-y", FlagKind.Integer, true, "Start Y"),
             new FlagSpec("to-x", FlagKind.Integer, true, "End X"),
             new FlagSpec("to-y", FlagKind.Integer, true, "End Y")],
            (a, s) => Handlers.Drag(a, s), NeedsAccessibility: true),
        new("doctor", "Self-check: platform readiness, permissions and visible windows.",
            [], (a, s) => Handlers.Doctor(a, s)),
        new("install-mcp", "Print (or write) the MCP config snippet pointing at this exact binary.",
            [new FlagSpec("client", FlagKind.String, false, "claude | cursor | zcode | json (default json)"),
             new FlagSpec("write", FlagKind.String, false, "Write the snippet to this file instead of printing it")],
            (a, s) => Handlers.InstallMcp(a, s)),
        new("skills", "Print the built-in agent guide: the loop, refs, delivery and retry rules.",
            [new FlagSpec("name", FlagKind.String, false, "Guide name: desktop (default)")],
            (a, s) => Handlers.Skills(a, s), ["name"], ["get", "show"]),
    ];

    public static CommandSpec? Find(string name) =>
        Array.Find(Commands, c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses argv against a spec: <c>--flag</c>, <c>--key value</c>, <c>--key=value</c>, plus bare
    /// positional arguments for commands that declare <see cref="CommandSpec.Positional"/>.
    /// Returns errors as envelopes.
    /// </summary>
    public static (Args?, Envelope?) Parse(CommandSpec spec, string[] argv)
    {
        var args = new Args { Command = spec.Name };
        var taken = new HashSet<string>(spec.Flags.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var positional = spec.Positional ?? [];
        var positionalIndex = 0;
        for (var i = 0; i < argv.Length; i++)
        {
            var token = argv[i];
            if (!token.StartsWith('-') || token == "-")
            {
                // Documented shorthand: "click @s8f3k2p9:e12", "skills get desktop".
                if (positionalIndex == 0 && spec.Verbs is { Length: > 0 } verbs
                    && verbs.Any(v => string.Equals(v, token, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (positionalIndex >= positional.Length)
                    return (null, Envelope.Fail(ErrorCodes.InvalidArgs,
                        $"Unexpected positional argument '{token}'",
                        $"Every argument to '{spec.Name}' is a flag; run 'adesk-pro {spec.Name} --help'."));
                var positionalName = positional[positionalIndex++];
                if (args.Has(positionalName))
                    return (null, Envelope.Fail(ErrorCodes.InvalidArgs,
                        $"--{positionalName} was given both positionally and as a flag"));
                var positionalSpec = Array.Find(spec.Flags, x =>
                    string.Equals(x.Name, positionalName, StringComparison.OrdinalIgnoreCase))!;
                if (Coerce(spec, args, positionalSpec, token) is { } failure) return (null, failure);
                continue;
            }
            var body = token.StartsWith("--") ? token[2..] : token[1..];
            string key, value;
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                key = body[..eq];
                value = body[(eq + 1)..];
                if (value.Length == 0) return (null, Bad(spec, key));
                if (!taken.Contains(key)) return (null, Unknown(spec, key));
                var fs = Array.Find(spec.Flags, x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase))!;
                if (args.Has(fs.Name))
                    return (null, Envelope.Fail(ErrorCodes.InvalidArgs,
                        $"--{fs.Name} was given both positionally and as a flag"));
                if (Coerce(spec, args, fs, value) is { } failure) return (null, failure);
                continue;
            }
            key = body;
            var flag = Array.Find(spec.Flags, x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase));
            if (flag is null) return (null, Unknown(spec, key));
            if (args.Has(flag.Name))
                return (null, Envelope.Fail(ErrorCodes.InvalidArgs,
                    $"--{flag.Name} was given both positionally and as a flag"));
            if (flag.Kind == FlagKind.Flag) { args.Set(key, true); continue; }
            // A value-taking flag consumes the next token verbatim, so text that itself starts
            // with "--" (e.g. --text "--help") is still deliverable.
            if (i + 1 >= argv.Length) return (null, Bad(spec, key));
            if (Coerce(spec, args, flag, argv[++i]) is { } valueFailure) return (null, valueFailure);
        }
        foreach (var f in spec.Flags.Where(f => f.Required && !args.Has(f.Name)))
            return (null, Envelope.Fail(ErrorCodes.InvalidArgs,
                $"--{f.Name} is required for '{spec.Name}'",
                $"Run 'adesk-pro {spec.Name} --help'."));
        return (args, null);
    }

    /// <summary>Stores one flag value into <paramref name="args"/>, validating it against the flag's kind.</summary>
    private static Envelope? Coerce(CommandSpec spec, Args args, FlagSpec flag, string value)
    {
        switch (flag.Kind)
        {
            case FlagKind.Flag:
                // Positional flags are booleans by nature; accept only explicit true/false words.
                if (!bool.TryParse(value, out var b))
                    return Envelope.Fail(ErrorCodes.InvalidArgs, $"'{value}' is not true or false");
                args.Set(flag.Name, b);
                return null;
            case FlagKind.Integer:
                if (!int.TryParse(value, out var n)) return Bad(spec, flag.Name);
                args.Set(flag.Name, n);
                return null;
            case FlagKind.Ref:
                if (Ids.ParseRef(value) is null)
                    return Envelope.Fail(ErrorCodes.InvalidArgs,
                        $"--{flag.Name}: '{value}' is not a ref like @s8f3k2p9:e8");
                args.Set(flag.Name, value);
                return null;
            default:
                args.Set(flag.Name, value);
                return null;
        }
    }

    private static Envelope Unknown(CommandSpec spec, string key) =>
        Envelope.Fail(ErrorCodes.InvalidArgs, $"Unknown flag --{key} for '{spec.Name}'",
            $"Run 'adesk-pro {spec.Name} --help'.");

    private static Envelope Bad(CommandSpec spec, string key) =>
        Envelope.Fail(ErrorCodes.InvalidArgs, $"--{key} needs a value");

    public static string HelpText(CommandSpec spec)
    {
        var lines = new List<string> { $"adesk-pro {spec.Name} — {spec.Description}" };
        if (spec.Positional is { Length: > 0 } positional)
            lines.Add($"  usage: adesk-pro {spec.Name} " +
                string.Join(' ', positional.Select(p => $"<{p}>")) + "  (or use the long flags below)");
        foreach (var f in spec.Flags)
        {
            var kind = f.Kind switch
            {
                FlagKind.Flag => "flag",
                FlagKind.Integer => "int",
                FlagKind.Ref => "ref",
                _ => "text",
            };
            lines.Add($"  --{f.Name} <{kind}>{(f.Required ? " (required)" : "")}  {f.Description}");
        }
        return string.Join('\n', lines);
    }

    /// <summary>True when <c>--help</c>/<c>-h</c> appears as a real flag, not as a value of the
    /// preceding value-taking flag (so <c>type --ref X --text --help</c> types the literal text).</summary>
    public static bool WantsHelp(CommandSpec spec, string[] argv)
    {
        for (var i = 0; i < argv.Length; i++)
        {
            var token = argv[i];
            if (token is not ("--help" or "-h")) continue;
            if (i == 0) return true;
            var prev = argv[i - 1];
            if (prev.StartsWith("--") && !prev.Contains('='))
            {
                var name = prev[2..];
                var flag = Array.Find(spec.Flags, x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (flag is { Kind: not FlagKind.Flag }) continue;   // it is a value, not help
            }
            return true;
        }
        return false;
    }
}
