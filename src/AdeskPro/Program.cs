using System.Text.Json.Nodes;
using AdeskPro.Ui;

namespace AdeskPro;

public static class Program
{
    [STAThread]
    public static int Main(string[] argv)
    {
        if (argv.Length == 0 || argv is ["--help"] or ["-h"])
        {
            PrintOverview();
            return 0;
        }
        if (argv[0] == "--version" || argv[0] == "version")
        {
            Console.WriteLine("adesk-pro " + Version.Value);
            return 0;
        }
        if (argv[0] == "mcp")
        {
            var session = TakeSession(ref argv);
            Mcp.McpServer.Run(session);
            return 0;
        }

        var (sessionName, commandArgv) = ExtractSession(argv);
        var spec = Registry.Find(commandArgv.Length > 0 ? commandArgv[0] : "");
        if (spec is null)
        {
            Console.WriteLine(EnvelopeJson.Serialize(Envelope.Fail(
                ErrorCodes.InvalidArgs,
                commandArgv.Length == 0 ? "no command given" : $"unknown command '{commandArgv[0]}'",
                "run 'adesk-pro --help'")));
            return 2;
        }
        if (commandArgv.Skip(1).Any(t => t is "--help" or "-h"))
        {
            Console.WriteLine(Registry.HelpText(spec));
            return 0;
        }
        var (args, failure) = Registry.Parse(spec, commandArgv.Skip(1).ToArray());
        if (failure is not null)
        {
            Console.WriteLine(EnvelopeJson.Serialize(failure));
            return 2;
        }
        var sessionState = new Session
        {
            Name = sessionName,
            InMemory = false,
            Refs = new RefStore(SessionStore.Dir(sessionName)),
        };
        var envelope = spec.Run(args!, sessionState);
        Console.WriteLine(EnvelopeJson.Serialize(envelope));
        return envelope.Ok ? 0 : 1;
    }

    private static string TakeSession(ref string[] argv)
    {
        var (name, rest) = ExtractSession(argv);
        return name;
    }

    private static (string, string[]) ExtractSession(string[] argv)
    {
        var session = "default";
        var rest = new List<string>();
        for (var i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--session" && i + 1 < argv.Length) { session = argv[++i]; continue; }
            if (argv[i].StartsWith("--session=", StringComparison.Ordinal))
            {
                session = argv[i]["--session=".Length..];
                continue;
            }
            rest.Add(argv[i]);
        }
        return (session, rest.ToArray());
    }

    private static void PrintOverview()
    {
        Console.WriteLine("adesk-pro — desktop use through accessibility trees (Windows UI Automation)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  adesk-pro mcp                    serve every command as MCP tools over stdio");
        foreach (var spec in Registry.Commands)
            Console.WriteLine($"  adesk-pro {spec.Name,-14}{spec.Description}");
        Console.WriteLine();
        Console.WriteLine("Global flags:");
        Console.WriteLine("  --session <name>             isolate snapshot state (default: default)");
        Console.WriteLine("  --help                       help for a command, e.g. adesk-pro snapshot --help");
    }
}
