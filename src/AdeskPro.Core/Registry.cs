using System.Text.Json.Nodes;
using AdeskPro.Platform;
using AdeskPro.Ui;

namespace AdeskPro;

/// <summary>A command flag, shared by the CLI parser, --help text and the MCP JSON schema.</summary>
public sealed record FlagSpec(string Name, FlagKind Kind, bool Required, string Description);

public enum FlagKind { Flag, String, Integer, Ref, Text }

/// <summary>One command. The CLI, --help and the MCP tool catalog are all derived from this table.</summary>
public sealed record CommandSpec(
    string Name,
    string Description,
    FlagSpec[] Flags,
    Func<Args, Session, Envelope> Run);

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
            (a, s) => Handlers.Snapshot(a, s)),
        new("find", "Find elements by name/role inside a window and return refs.",
            [new FlagSpec("app", FlagKind.String, false, "Process/window title match"),
             new FlagSpec("pid", FlagKind.Integer, false, "Process id"),
             new FlagSpec("name", FlagKind.String, true, "Name substring to match"),
             new FlagSpec("role", FlagKind.String, false, "Control type filter"),
             new FlagSpec("limit", FlagKind.Integer, false, "Max matches (default 10)")],
            (a, s) => Handlers.Find(a, s)),
        new("get", "Read properties of one ref.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot")],
            (a, s) => Handlers.Get(a, s)),
        new("click", "Invoke an element's default action (Invoke pattern).",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot")],
            (a, s) => Handlers.Click(a, s)),
        new("toggle", "Toggle a check box / toggle button.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot")],
            (a, s) => Handlers.Toggle(a, s)),
        new("expand", "Expand or collapse an element.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot"),
             new FlagSpec("collapse", FlagKind.Flag, false, "Collapse instead of expand")],
            (a, s) => Handlers.Expand(a, s)),
        new("select", "Select a list/tree item.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot")],
            (a, s) => Handlers.Select(a, s)),
        new("type", "Set or type text into an editable element.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot"),
             new FlagSpec("text", FlagKind.Text, false, "Text to enter"),
             new FlagSpec("stdin", FlagKind.Flag, false, "Read text from stdin instead of --text")],
            (a, s) => Handlers.Type(a, s)),
        new("set-value", "Set an element's value via the Value pattern.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot"),
             new FlagSpec("text", FlagKind.Text, false, "New value"),
             new FlagSpec("stdin", FlagKind.Flag, false, "Read value from stdin")],
            (a, s) => Handlers.SetValue(a, s)),
        new("press", "Send a keyboard shortcut, e.g. ctrl+s.",
            [new FlagSpec("keys", FlagKind.String, true, "Key combo, + separated"),
             new FlagSpec("app", FlagKind.String, false, "Focus this app's window first")],
            (a, s) => Handlers.Press(a, s)),
        new("scroll", "Scroll the nearest scrollable ancestor of a ref.",
            [new FlagSpec("ref", FlagKind.Ref, true, "Ref from a snapshot"),
             new FlagSpec("dy", FlagKind.Integer, false, "Vertical percent delta, negative = up (default 30)")],
            (a, s) => Handlers.Scroll(a, s)),
        new("focus", "Focus an element or window.",
            [new FlagSpec("ref", FlagKind.Ref, false, "Ref to focus"),
             new FlagSpec("app", FlagKind.String, false, "Focus this app's main window")],
            (a, s) => Handlers.Focus(a, s)),
        new("clipboard-get", "Read clipboard text.", [], (a, s) => Handlers.ClipboardGet(a, s)),
        new("clipboard-set", "Write clipboard text.",
            [new FlagSpec("text", FlagKind.Text, false, "Text"),
             new FlagSpec("stdin", FlagKind.Flag, false, "Read text from stdin")],
            (a, s) => Handlers.ClipboardSet(a, s)),
        new("screenshot", "Capture a window to a PNG (write to --out, or inline base64).",
            [.. WindowTarget, new FlagSpec("out", FlagKind.String, false, "PNG output path; omit for inline base64")],
            (a, s) => Handlers.ScreenshotCmd(a, s)),
        new("wait", "Poll until a window appears or a ref turns enabled/visible/gone.",
            [.. WindowTarget, new FlagSpec("ref", FlagKind.Ref, false, "Element to wait on"),
             new FlagSpec("until", FlagKind.String, false, "enabled | visible | gone (default enabled)"),
             new FlagSpec("timeout", FlagKind.Integer, false, "Milliseconds (default 10000)")],
            (a, s) => Handlers.Wait(a, s)),
        new("batch", "Run several commands sequentially in this one process.",
            [new FlagSpec("stdin", FlagKind.Flag, false, "Read the JSON batch array from stdin"),
             new FlagSpec("file", FlagKind.String, false, "JSON batch array file"),
             new FlagSpec("stop-on-error", FlagKind.Flag, false, "Stop at the first failed command")],
            (a, s) => Handlers.Batch(a, s)),
        new("launch", "Start a program and return its top-level window.",
            [new FlagSpec("exe", FlagKind.String, true, "Executable path"),
             new FlagSpec("args", FlagKind.String, false, "Command-line arguments")],
            (a, s) => Handlers.Launch(a, s)),
        new("close-app", "Close an app's windows via the Window pattern.",
            [.. WindowTarget, new FlagSpec("force", FlagKind.Flag, false, "Kill the process as a fallback")],
            (a, s) => Handlers.CloseApp(a, s)),
        new("minimize", "Minimize an app's windows.", [.. WindowTarget],
            (a, s) => Handlers.Minimize(a, s)),
        new("maximize", "Maximize an app's windows.", [.. WindowTarget],
            (a, s) => Handlers.Maximize(a, s)),
        new("restore", "Restore an app's windows.", [.. WindowTarget],
            (a, s) => Handlers.Restore(a, s)),
        new("move-window", "Move a window.",
            [.. WindowTarget, new FlagSpec("x", FlagKind.Integer, false, "New X"),
             new FlagSpec("y", FlagKind.Integer, false, "New Y")],
            (a, s) => Handlers.MoveWindow(a, s)),
        new("resize-window", "Resize a window.",
            [.. WindowTarget, new FlagSpec("w", FlagKind.Integer, true, "New width"),
             new FlagSpec("h", FlagKind.Integer, true, "New height")],
            (a, s) => Handlers.ResizeWindow(a, s)),
        new("mouse-click", "Physical mouse click at screen coordinates (moves the cursor).",
            [new FlagSpec("x", FlagKind.Integer, true, "Screen X"),
             new FlagSpec("y", FlagKind.Integer, true, "Screen Y"),
             new FlagSpec("right", FlagKind.Flag, false, "Right button"),
             new FlagSpec("double", FlagKind.Flag, false, "Double click")],
            (a, s) => Handlers.MouseClick(a, s)),
        new("hover", "Move the cursor to screen coordinates.",
            [new FlagSpec("x", FlagKind.Integer, true, "Screen X"),
             new FlagSpec("y", FlagKind.Integer, true, "Screen Y")],
            (a, s) => Handlers.Hover(a, s)),
        new("drag", "Press, move, release — from one point to another.",
            [new FlagSpec("from-x", FlagKind.Integer, true, "Start X"),
             new FlagSpec("from-y", FlagKind.Integer, true, "Start Y"),
             new FlagSpec("to-x", FlagKind.Integer, true, "End X"),
             new FlagSpec("to-y", FlagKind.Integer, true, "End Y")],
            (a, s) => Handlers.Drag(a, s)),
        new("skills", "Print the built-in agent guide: the loop, refs, delivery and retry rules.",
            [new FlagSpec("name", FlagKind.String, false, "Guide name: desktop (default)")],
            (a, s) => Handlers.Skills(a, s)),
    ];

    public static CommandSpec? Find(string name) =>
        Array.Find(Commands, c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Parses argv against a spec: --flag, --key value, --key=value. Returns errors as envelopes.</summary>
    public static (Args?, Envelope?) Parse(CommandSpec spec, string[] argv)
    {
        var args = new Args { Command = spec.Name };
        var taken = new HashSet<string>(spec.Flags.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < argv.Length; i++)
        {
            var token = argv[i];
            if (!token.StartsWith('-') || token == "-")
                return (null, Envelope.Fail(ErrorCodes.InvalidArgs,
                    $"Unexpected positional argument '{token}'",
                    $"Every argument to '{spec.Name}' is a flag; run 'adesk-pro {spec.Name} --help'."));
            var body = token.StartsWith("--") ? token[2..] : token[1..];
            string key, value;
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                key = body[..eq];
                value = body[(eq + 1)..];
                if (value.Length == 0) return (null, Bad(spec, key));
            }
            else
            {
                key = body;
                var f = Array.Find(spec.Flags, x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase));
                if (f is null) return (null, Unknown(spec, key));
                if (f.Kind == FlagKind.Flag) { args.Set(key, true); continue; }
                if (i + 1 >= argv.Length || argv[i + 1].StartsWith("--"))
                    return (null, Bad(spec, key));
                value = argv[++i];
            }
            if (!taken.Contains(key)) return (null, Unknown(spec, key));
            var fs = Array.Find(spec.Flags, x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase))!;
            switch (fs.Kind)
            {
                case FlagKind.Flag:
                    args.Set(key, true);
                    break;
                case FlagKind.Integer:
                    if (!int.TryParse(value, out var n)) return (null, Bad(spec, key));
                    args.Set(key, n);
                    break;
                case FlagKind.Ref:
                    if (Ids.ParseRef(value) is null)
                        return (null, Envelope.Fail(ErrorCodes.InvalidArgs,
                            $"--{key}: '{value}' is not a ref like @s8f3k2p9:e8"));
                    args.Set(key, value);
                    break;
                default:
                    args.Set(key, value);
                    break;
            }
        }
        foreach (var f in spec.Flags.Where(f => f.Required && !args.Has(f.Name)))
            return (null, Envelope.Fail(ErrorCodes.InvalidArgs,
                $"--{f.Name} is required for '{spec.Name}'",
                $"Run 'adesk-pro {spec.Name} --help'."));
        return (args, null);
    }

    private static Envelope Unknown(CommandSpec spec, string key) =>
        Envelope.Fail(ErrorCodes.InvalidArgs, $"Unknown flag --{key} for '{spec.Name}'",
            $"Run 'adesk-pro {spec.Name} --help'.");

    private static Envelope Bad(CommandSpec spec, string key) =>
        Envelope.Fail(ErrorCodes.InvalidArgs, $"--{key} needs a value");

    public static string HelpText(CommandSpec spec)
    {
        var lines = new List<string> { $"adesk-pro {spec.Name} — {spec.Description}" };
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
}
