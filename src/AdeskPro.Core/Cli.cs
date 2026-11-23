using AdeskPro.Platform;
using AdeskPro.Ui;

namespace AdeskPro;

/// <summary>
/// The CLI entry point. Platform-agnostic: each OS binary hands in its own
/// <see cref="IPlatform"/> factory, so one command table, one parser and one help text serve
/// Windows, macOS and the test fake.
/// </summary>
public static class Cli
{
    public static int Run(string[] argv, Func<IPlatform> platformFactory)
    {
        if (argv.Length == 0 || argv is ["--help"] or ["-h"] or ["help"])
        {
            PrintOverview();
            return 0;
        }
        if (argv[0] is "--version" or "version")
        {
            Console.WriteLine("adesk-pro " + Version.Value);
            return 0;
        }
        if (argv[0] == "mcp")
        {
            var mcpSession = TakeSession(ref argv);
            Mcp.McpServer.Run(platformFactory(), mcpSession);
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
        var rest = commandArgv.Skip(1).ToArray();
        if (Registry.WantsHelp(spec, rest))
        {
            Console.WriteLine(Registry.HelpText(spec));
            return 0;
        }
        var (args, failure) = Registry.Parse(spec, rest);
        if (failure is not null)
        {
            Console.WriteLine(EnvelopeJson.Serialize(failure));
            return 2;
        }

        var platform = platformFactory();
        // Ask the OS for permission only for commands that actually read or act on an
        // accessibility tree. Reporting commands (status, doctor, list-*) can answer without it,
        // and --help/--version never reach this point at all.
        if (spec.NeedsAccessibility) platform.RequestAccessibility();
        var session = new Session
        {
            Name = sessionName,
            InMemory = false,
            Refs = new RefStore(SessionStore.Dir(sessionName)),
            Platform = platform,
        };
        var envelope = RunAndRelease(spec, args!, session, platform);
        Console.WriteLine(EnvelopeJson.Serialize(envelope));
        return envelope.Ok ? 0 : 1;

        // Native element references must be released before the process would otherwise keep
        // them alive for the whole session (long-lived MCP servers especially).
        static Envelope RunAndRelease(CommandSpec spec, Args args, Session session, IPlatform platform)
        {
            try { return spec.Run(args, session); }
            finally { platform.ReleaseCommandResources(); }
        }
    }

    private static string TakeSession(ref string[] argv)
    {
        var (name, rest) = ExtractSession(argv);
        argv = rest;
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
        Console.WriteLine("adesk-pro — desktop use through accessibility trees");
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
