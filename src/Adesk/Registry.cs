using System.Text.Json.Nodes;
using Adesk.Ui;

namespace Adesk;

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

/// <summary>Session-scoped state: which snapshot store the command reads and writes.</summary>
public sealed class Session
{
    public required string Name { get; init; }
    public required bool InMemory { get; init; }   // MCP mode keeps state in the server process
    public RefStore Refs { get; set; } = null!;
}

/// <summary>The one table that defines every command. Nothing else hard-codes a command name.</summary>
public static class Registry
{
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
                    $"Every argument to '{spec.Name}' is a flag; run 'adesk {spec.Name} --help'."));
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
                $"Run 'adesk {spec.Name} --help'."));
        return (args, null);
    }

    private static Envelope Unknown(CommandSpec spec, string key) =>
        Envelope.Fail(ErrorCodes.InvalidArgs, $"Unknown flag --{key} for '{spec.Name}'",
            $"Run 'adesk {spec.Name} --help'.");

    private static Envelope Bad(CommandSpec spec, string key) =>
        Envelope.Fail(ErrorCodes.InvalidArgs, $"--{key} needs a value");

    public static string HelpText(CommandSpec spec)
    {
        var lines = new List<string> { $"adesk {spec.Name} — {spec.Description}" };
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
